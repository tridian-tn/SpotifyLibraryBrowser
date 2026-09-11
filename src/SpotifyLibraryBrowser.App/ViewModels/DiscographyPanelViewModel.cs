using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpotifyLibraryBrowser.Core.Data;
using SpotifyLibraryBrowser.Core.Discography;
using SpotifyLibraryBrowser.Core.Library;
using SpotifyLibraryBrowser.Core.Playback;

namespace SpotifyLibraryBrowser.App.ViewModels;

/// <summary>One release in the discography panel.</summary>
/// <param name="album">The release as read from the index</param>
public sealed partial class DiscographyAlbumViewModel(DiscographyAlbum album) : ObservableObject
{
    /// <summary>Whether the album is in the library, flipped optimistically when saved.</summary>
    [ObservableProperty]
    private bool _isSaved = album.IsSaved;

    /// <summary>The Spotify album ID.</summary>
    public string Id => album.Id;

    /// <summary>The Spotify URI, for playing or saving it.</summary>
    public string Uri => album.Uri;

    /// <summary>The album title.</summary>
    public string Name => album.Name;

    /// <summary>Release year and track count, as one line.</summary>
    public string Detail =>
        $"{album.Year?.ToString() ?? "—"} · {album.TotalTracks} track{(album.TotalTracks == 1 ? "" : "s")}";

    /// <summary>How many of its tracks the library already holds.</summary>
    public int TracksHeld => album.TracksHeld;

    /// <summary>
    /// A note about what the library has of this record, blank when it has nothing.
    /// </summary>
    /// <remarks>
    /// The partial case is the interesting one: a few liked tracks off a record you never saved is
    /// exactly the prompt to save it.
    /// </remarks>
    public string Held => album.IsPartiallyHeld
        ? $"{album.TracksHeld} of {album.TotalTracks} in library"
        : string.Empty;
}

/// <summary>
/// Shows the full discography of whichever artist is selected, including releases the library
/// doesn't have.
/// </summary>
/// <remarks>
/// A panel rather than a browser column on purpose. Every column is a projection over tracks in
/// the local index and columns filter one another; these albums have no tracks indexed, so as a
/// column they could neither be filtered by nor filter anything. As a view they answer the
/// question directly: what else did this artist make, and how much of it do I have?
/// </remarks>
public sealed partial class DiscographyPanelViewModel : ObservableObject
{
    private DiscographyService? _service;
    private LibraryWriteService? _library;
    private PlaybackController? _playback;
    private CancellationTokenSource? _loading;
    private IReadOnlyList<DiscographyAlbum> _loaded = [];
    private string? _artistId;
    private bool _restoring;

    [ObservableProperty]
    private string? _artistName;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private DiscographySort _sort = DiscographySort.NewestFirst;

    [ObservableProperty]
    private DiscographyGroups _groups = DiscographyGroups.Albums | DiscographyGroups.Singles;

    /// <summary>The releases on show.</summary>
    [ObservableProperty]
    private ObservableCollection<DiscographyAlbumViewModel> _albums = [];

    /// <summary>Raised when a save changes the library, so the browser can reload.</summary>
    public event Action? LibraryChanged;

    /// <summary>The sort orders offered, paired with something readable.</summary>
    public IReadOnlyList<SortChoice> SortChoices { get; } =
    [
        new(DiscographySort.NewestFirst, "Newest first"),
        new(DiscographySort.OldestFirst, "Oldest first"),
        new(DiscographySort.Name, "Title"),
        new(DiscographySort.TrackCount, "Longest first"),
        new(DiscographySort.InLibraryFirst, "In library first")
    ];

    /// <summary>The release kinds offered.</summary>
    public IReadOnlyList<GroupChoice> GroupChoices { get; } =
    [
        new(DiscographyGroups.Albums, "Albums"),
        new(DiscographyGroups.Albums | DiscographyGroups.Singles, "Albums and singles"),
        new(DiscographyGroups.Albums | DiscographyGroups.Singles | DiscographyGroups.Compilations,
            "Albums, singles and compilations")
    ];

    /// <summary>Hands the panel the services it needs, once there's a signed-in client.</summary>
    /// <param name="service">Fetches and caches discographies</param>
    /// <param name="library">Used to save an album straight from the panel</param>
    /// <param name="playback">Used to play one</param>
    public void Connect(DiscographyService service, LibraryWriteService library, PlaybackController playback)
    {
        _service = service;
        _library = library;
        _playback = playback;
    }

    /// <summary>Restores the remembered sort and grouping without triggering a reload.</summary>
    /// <remarks>
    /// Set through the properties, so the view still hears about it, with the change handlers
    /// held off — restoring a preference isn't the user asking for a fetch.
    /// </remarks>
    /// <param name="sort">The stored sort order</param>
    /// <param name="groups">The stored release kinds</param>
    public void Restore(DiscographySort sort, DiscographyGroups groups)
    {
        _restoring = true;

        try
        {
            Sort = sort;
            Groups = groups;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// Shows an artist's discography, fetching it if it isn't already cached.
    /// </summary>
    /// <param name="artistId">The artist to show</param>
    /// <param name="artistName">Their name, for the header</param>
    public async Task ShowAsync(string artistId, string artistName)
    {
        if (_service is null) return;

        // Already showing this artist, so the fetch would be for nothing.
        if (_artistId == artistId && Albums.Count > 0 && !IsLoading) return;

        _artistId = artistId;
        ArtistName = artistName;
        IsVisible = true;

        await LoadAsync(refresh: false);
    }

    /// <summary>Hides the panel, for when no single artist is selected.</summary>
    public void Hide()
    {
        _loading?.Cancel();
        _artistId = null;
        IsVisible = false;
        Albums = [];
        Message = null;
    }

    /// <summary>Fetches the current artist's releases again, ignoring the cache.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refresh: true);

    /// <summary>Plays a release on the chosen Connect device.</summary>
    /// <param name="album">The release to play</param>
    [RelayCommand]
    private async Task PlayAlbumAsync(DiscographyAlbumViewModel? album)
    {
        if (album is null || _playback is null) return;

        await _playback.PlayContextAsync(album.Uri);
    }

    /// <summary>Adds a release to the library, or takes it out again.</summary>
    /// <param name="album">The release to change</param>
    [RelayCommand]
    private async Task ToggleSavedAsync(DiscographyAlbumViewModel? album)
    {
        if (album is null || _library is null) return;

        var save = !album.IsSaved;
        album.IsSaved = save;

        try
        {
            await _library.SetAlbumsSavedAsync([album.Id], save);
            Message = save ? $"Saved {album.Name}." : $"Removed {album.Name}.";

            // The browser's Album and Album Artist columns read saved state, so they're now stale.
            LibraryChanged?.Invoke();
        }
        catch (Exception e)
        {
            album.IsSaved = !save;
            Message = $"Couldn't update your library: {e.Message}";
        }
    }

    /// <summary>Loads the current artist's releases.</summary>
    /// <param name="refresh">Fetch again even when a usable cache exists</param>
    private async Task LoadAsync(bool refresh)
    {
        if (_service is null || _artistId is null) return;

        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;

        IsLoading = true;
        Message = null;

        try
        {
            _loaded = await _service.GetAsync(_artistId, Groups, Sort, refresh, cts.Token);
            Apply(_loaded);

            if (_loaded.Count == 0) Message = "Spotify lists no releases for this artist.";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection; that load will fill the panel instead.
        }
        catch (Exception e)
        {
            Albums = [];
            Message = $"Couldn't load the discography: {e.Message}";
        }
        finally
        {
            if (_loading == cts) IsLoading = false;
        }
    }

    /// <summary>Puts a set of releases on show.</summary>
    /// <param name="albums">The releases to show</param>
    private void Apply(IEnumerable<DiscographyAlbum> albums) =>
        Albums = new ObservableCollection<DiscographyAlbumViewModel>(
            albums.Select(a => new DiscographyAlbumViewModel(a)));

    /// <summary>Re-sorts what's already loaded, which needs no fetch.</summary>
    /// <param name="value">The newly chosen order</param>
    partial void OnSortChanged(DiscographySort value)
    {
        if (_restoring || _loaded.Count == 0) return;

        Apply(DiscographyRepository.Sort(_loaded, value));
    }

    /// <summary>Refetches, since a different set of groups is a different request.</summary>
    /// <param name="value">The newly chosen release kinds</param>
    partial void OnGroupsChanged(DiscographyGroups value)
    {
        if (_restoring || _artistId is null) return;

        _ = LoadAsync(refresh: false);
    }
}

/// <summary>A sort order paired with its label.</summary>
/// <remarks>Concrete rather than generic: XAML's x:DataType can't name a closed generic.</remarks>
/// <param name="Value">The order chosen</param>
/// <param name="Label">What the user sees</param>
public sealed record SortChoice(DiscographySort Value, string Label);

/// <summary>A set of release kinds paired with its label.</summary>
/// <param name="Value">The kinds chosen</param>
/// <param name="Label">What the user sees</param>
public sealed record GroupChoice(DiscographyGroups Value, string Label);
