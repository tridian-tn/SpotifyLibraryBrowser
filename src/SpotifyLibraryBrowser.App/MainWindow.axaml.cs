using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SpotifyLibraryBrowser.App.ViewModels;
using SpotifyLibraryBrowser.Core.Browsing;

namespace SpotifyLibraryBrowser.App;

/// <summary>
/// The main window. Selection is wired here rather than through bindings: the column browser is a
/// dynamic set of list boxes, so one bubbled handler on the host is simpler and more reliable than
/// binding <c>SelectedItems</c> per column.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private MainWindowViewModel? _model;
    private bool _restoring;

    /// <summary>Creates the window and hooks up the events the view model needs.</summary>
    public MainWindow()
    {
        InitializeComponent();

        ColumnsHost.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnColumnSelectionChanged);
        TrackTable.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnTrackSelectionChanged);
        TrackTable.DoubleTapped += OnTrackDoubleTapped;
        DiscographyList.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnDiscographySelectionChanged);

        _playbackTimer.Tick += async (_, _) => await RefreshPlaybackAsync();

        Opened += OnOpened;
        Closing += OnClosing;
        Activated += OnActivated;
        Deactivated += (_, _) => _playbackTimer.Stop();
    }

    /// <summary>Starts the view model once the window is up.</summary>
    /// <param name="sender">The window</param>
    /// <param name="e">The event data</param>
    private async void OnOpened(object? sender, EventArgs e)
    {
        _model = (MainWindowViewModel)DataContext!;
        _model.RefreshCompleted += OnRefreshCompleted;

        // This handler is async void, so anything escaping InitialiseAsync would take the whole
        // app down with a stack trace instead of showing the user something they can act on.
        try
        {
            await _model.InitialiseAsync();
        }
        catch (Exception ex)
        {
            _model.ReportStartupFailure(ex);
        }

        Width = _model.SavedWidth;
        Height = _model.SavedHeight;

        // The browser row is what the splitter moves, so its height is the thing worth restoring.
        // Guarded against a stored value taller than the window, which would leave no track list.
        var browser = Math.Clamp(_model.SavedBrowserHeight, 120, Math.Max(120, Height - 260));
        BrowserGrid.RowDefinitions[1].Height = new GridLength(browser, GridUnitType.Pixel);
    }

    /// <summary>Saves the layout on the way out.</summary>
    /// <param name="sender">The window</param>
    /// <param name="e">The event data</param>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_model is null) return;

        // The measured height, not the declared one: a splitter may leave a row star-sized, and
        // Height.Value would then be a star factor rather than a number of pixels.
        await _model.SaveLayoutAsync(Width, Height, BrowserGrid.RowDefinitions[1].ActualHeight);
    }

    /// <summary>
    /// Re-checks liked state whenever the window comes forward, and restarts playback polling.
    /// </summary>
    /// <remarks>Polling only runs while the window has focus, to stay well clear of the rate limit.</remarks>
    /// <param name="sender">The window</param>
    /// <param name="e">The event data</param>
    private async void OnActivated(object? sender, EventArgs e)
    {
        if (_model is null || !_model.IsReady) return;

        _playbackTimer.Start();
        await _model.ReconcileLikesAsync();
        await RefreshPlaybackAsync();
    }

    /// <summary>Refreshes devices and the now-playing line, ignoring a closed window.</summary>
    private async System.Threading.Tasks.Task RefreshPlaybackAsync()
    {
        if (_model is { IsReady: true }) await _model.RefreshPlaybackAsync();
    }

    /// <summary>
    /// Pushes a column's selection into its view model and rebuilds everything downstream.
    /// </summary>
    /// <param name="sender">The columns host</param>
    /// <param name="e">The selection change</param>
    private async void OnColumnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_model is null || _restoring || _model.IsRefreshing) return;
        if (e.Source is not ListBox list) return;
        if (list.DataContext is not ColumnViewModel column) return;

        var index = _model.Columns.IndexOf(column);
        if (index < 0) return;

        var keys = list.SelectedItems?
            .OfType<BrowseValue>()
            .Select(v => v.Key)
            .ToList() ?? [];

        column.SetSelection(keys);
        await _model.RefreshFromAsync(index);
    }

    /// <summary>
    /// Puts each column's list box selection back after its values were replaced.
    /// </summary>
    /// <remarks>
    /// Refilling a column loses the highlight even though the view model still holds the
    /// selection, so without this a search or a sync would appear to reset the browser.
    /// The work is posted at background priority because containers aren't realised until
    /// after the items change has been laid out.
    /// </remarks>
    private void OnRefreshCompleted() =>
        Dispatcher.UIThread.Post(RestoreColumnSelections, DispatcherPriority.Background);

    /// <summary>Re-selects the values each column's view model still considers selected.</summary>
    private void RestoreColumnSelections()
    {
        if (_model is null) return;

        _restoring = true;

        try
        {
            for (var i = 0; i < _model.Columns.Count; i++)
            {
                var column = _model.Columns[i];

                if (ColumnsHost.ContainerFromIndex(i) is not Control container) continue;

                var list = container.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
                if (list?.SelectedItems is null) continue;

                var wanted = column.Values
                    .Where(v => column.SelectedKeys.Contains(v.Key))
                    .ToList();

                list.SelectedItems.Clear();

                foreach (var value in wanted)
                {
                    list.SelectedItems.Add(value);
                }
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Mirrors the track list's selection into the view model.</summary>
    /// <param name="sender">The track table</param>
    /// <param name="e">The selection change</param>
    private void OnTrackSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_model is null) return;

        var selected = TrackTable.SelectedItems?.OfType<TrackViewModel>().ToList()
                       ?? new List<TrackViewModel>();

        _model.SelectedTracks.Clear();

        foreach (var track in selected)
        {
            _model.SelectedTracks.Add(track);
        }
    }

    /// <summary>
    /// Copies the redirect URI so it can be pasted into the dashboard without retyping.
    /// </summary>
    /// <remarks>The clipboard hangs off the top level, so this needs the view rather than the model.</remarks>
    /// <param name="sender">The copy button</param>
    /// <param name="e">The event data</param>
    private async void OnCopyRedirectUri(object? sender, RoutedEventArgs e)
    {
        if (_model is null) return;

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(_model.RedirectUri);
        _model.RedirectUriCopied = true;
    }

    /// <summary>
    /// Lists the tracks of whichever release was picked in the discography panel.
    /// </summary>
    /// <param name="sender">The discography list</param>
    /// <param name="e">The selection change</param>
    private void OnDiscographySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_model is null) return;
        if (DiscographyList.SelectedItem is not DiscographyAlbumViewModel album) return;

        _model.Discography.OpenAlbumCommand.Execute(album);
    }

    /// <summary>Plays the double-clicked track.</summary>
    /// <param name="sender">The track table</param>
    /// <param name="e">The event data</param>
    private void OnTrackDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (_model is null || TrackTable.SelectedItem is not TrackViewModel track) return;

        _model.PlayTrackCommand.Execute(track);
    }
}
