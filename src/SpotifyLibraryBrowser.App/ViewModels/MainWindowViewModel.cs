using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpotifyAPI.Web;
using SpotifyLibraryBrowser.App.Configuration;
using SpotifyLibraryBrowser.Core.Api;
using SpotifyLibraryBrowser.Core.Auth;
using SpotifyLibraryBrowser.Core.Browsing;
using SpotifyLibraryBrowser.Core.Data;
using SpotifyLibraryBrowser.Core.Discography;
using SpotifyLibraryBrowser.Core.Library;
using SpotifyLibraryBrowser.Core.Playback;
using SpotifyLibraryBrowser.Core.Sync;

namespace SpotifyLibraryBrowser.App.ViewModels;

/// <summary>Which screen the window is showing.</summary>
public enum AppState
{
    /// <summary>Starting up.</summary>
    Loading = 0,

    /// <summary>No Client ID has been entered yet.</summary>
    NeedsClientId = 1,

    /// <summary>Configured but not signed in.</summary>
    SignedOut = 2,

    /// <summary>Signed in and browsable.</summary>
    Ready = 3
}

/// <summary>Drives the whole window: sign-in, sync, the column browser, playback and liking.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly RequestThrottle _throttle = new();
    private readonly List<string> _lastLikeChange = [];

    private LibraryDatabase? _database;
    private LibraryRepository? _repository;
    private BrowseRepository? _browse;
    private LibraryWriteService? _library;
    private DiscographyRepository? _discographyStore;
    private PlaybackController? _playback;
    private LibrarySyncService? _sync;
    private PkceAuthService? _auth;
    private AppSettings _settings = new();
    private CancellationTokenSource? _refresh;
    private bool _lastLikeWasLiking;
    private bool _suppressRefresh;
    private bool _showingDiscographyAlbum;

    [ObservableProperty]
    private AppState _state = AppState.Loading;

    [ObservableProperty]
    private string _clientIdInput = string.Empty;

    [ObservableProperty]
    private string? _clientIdError;

    [ObservableProperty]
    private bool _redirectUriCopied;

    [ObservableProperty]
    private string _status = "Starting…";

    [ObservableProperty]
    private string _search = string.Empty;

    [ObservableProperty]
    private bool _likedOnly;

    [ObservableProperty]
    private bool _savedAlbumsOnly = true;

    /// <summary>Whether list rows are tightened up, so more fits on screen.</summary>
    [ObservableProperty]
    private bool _compactRows;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _undoMessage;

    [ObservableProperty]
    private string _nowPlaying = string.Empty;

    [ObservableProperty]
    private DeviceRow? _selectedDevice;

    [ObservableProperty]
    private int _trackCount;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _selectedAlbumsAreSaved;

    /// <summary>What the album action in the track list's menu currently offers to do.</summary>
    [ObservableProperty]
    private string _albumActionText = "Save album to library";

    /// <summary>Watches the selection so the album action can offer the right verb for it.</summary>
    public MainWindowViewModel()
    {
        SelectedTracks.CollectionChanged += (_, _) => UpdateSelectionState();

        // Saving from the panel changes what the Album and Album Artist columns browse.
        Discography.LibraryChanged += () => _ = RefreshAllAsync();

        // Picking a release in the panel takes the track list over until the browser moves on.
        Discography.AlbumPicked += ShowDiscographyTracks;
    }

    /// <summary>The browser's columns, left to right.</summary>
    public ObservableCollection<ColumnViewModel> Columns { get; } = [];

    /// <summary>The discography panel, shown when a single artist is selected.</summary>
    public DiscographyPanelViewModel Discography { get; } = new();

    /// <summary>
    /// The tracks matching the current selection.
    /// </summary>
    /// <remarks>
    /// Swapped wholesale rather than cleared and refilled: an unfiltered library is thousands of
    /// rows, and adding them one at a time means thousands of change notifications for a list the
    /// user is about to replace anyway.
    /// </remarks>
    [ObservableProperty]
    private ObservableCollection<TrackViewModel> _tracks = [];

    /// <summary>The Connect devices available to play on.</summary>
    public ObservableCollection<DeviceRow> Devices { get; } = [];

    /// <summary>The tracks the user has highlighted in the list.</summary>
    public ObservableCollection<TrackViewModel> SelectedTracks { get; } = [];

    /// <summary>
    /// Whether columns are being repopulated right now.
    /// </summary>
    /// <remarks>
    /// The view checks this before acting on a selection change: emptying a list box to refill it
    /// raises one, and treating that echo as a real click would wipe the user's selection.
    /// </remarks>
    public bool IsRefreshing => _suppressRefresh;

    /// <summary>Raised once columns have been repopulated, so the view can re-show selections.</summary>
    public event Action? RefreshCompleted;

    /// <summary>Whether an unlike can still be taken back.</summary>
    public bool CanUndo => _lastLikeChange.Count > 0;

    /// <summary>The redirect URI the user must register, taken from the flow that will use it.</summary>
    public string RedirectUri { get; } = PkceAuthService.CallbackUriFor().ToString();

    /// <summary>The dashboard the onboarding link opens.</summary>
    public string DashboardUrl => PkceAuthService.DashboardUrl;

    /// <summary>Whether the Client ID prompt should be showing.</summary>
    public bool IsNeedsClientId => State == AppState.NeedsClientId;

    /// <summary>Whether the sign-in prompt should be showing.</summary>
    public bool IsSignedOut => State == AppState.SignedOut;

    /// <summary>Whether the browser itself should be showing.</summary>
    public bool IsReady => State == AppState.Ready;

    /// <summary>Opens the index and restores any previous session.</summary>
    public async Task InitialiseAsync()
    {
        _settings = await SettingsStore.LoadAsync();
        LikedOnly = _settings.LikedOnly;
        SavedAlbumsOnly = _settings.SavedAlbumsOnly;
        CompactRows = _settings.CompactRows;

        _database = await LibraryDatabase.OpenAsync(AppPaths.DatabaseFile);
        _repository = new LibraryRepository(_database);
        _browse = new BrowseRepository(_database);
        _discographyStore = new DiscographyRepository(_database);

        Discography.Restore(_settings.DiscographySort, _settings.DiscographyGroups);

        BuildColumns(_settings.Columns);

        if (string.IsNullOrWhiteSpace(_settings.ClientId))
        {
            State = AppState.NeedsClientId;
            Status = "Enter the Client ID from your Spotify Developer Dashboard app.";
            return;
        }

        _auth = CreateAuthService(_settings.ClientId);

        Status = "Restoring session…";
        var client = await _auth.TryRestoreAsync();

        if (client is null)
        {
            State = AppState.SignedOut;
            Status = "Sign in to load your library.";
            return;
        }

        await OnSignedInAsync(client);
    }

    /// <summary>
    /// Shows a startup failure instead of letting it kill the app.
    /// </summary>
    /// <remarks>
    /// A stored token that can't be used is the likely cause, and signing in again fixes it, so
    /// the sign-in screen is the useful place to land.
    /// </remarks>
    /// <param name="failure">What went wrong during startup</param>
    public void ReportStartupFailure(Exception failure)
    {
        State = _repository is null ? AppState.NeedsClientId : AppState.SignedOut;
        Status = $"Couldn't restore the last session: {failure.Message} Sign in again to continue.";
    }

    /// <summary>Saves the entered Client ID and moves on to sign-in.</summary>
    [RelayCommand]
    private async Task SaveClientIdAsync()
    {
        var clientId = ClientIdInput.Trim();

        if (clientId.Length == 0)
        {
            ClientIdError = "Paste the Client ID from your dashboard app.";
            return;
        }

        // A Spotify Client ID is 32 hex characters. Catching an obviously wrong paste here beats
        // sending the user to a browser that fails with an opaque "invalid client" page — the
        // Client Secret and the app's own name are the two things people paste by mistake.
        if (clientId.Length != 32 || !clientId.All(char.IsAsciiHexDigit))
        {
            ClientIdError = "That doesn't look like a Client ID. Expected 32 letters and digits. " +
                            "Check you haven't pasted the Client Secret.";
            return;
        }

        ClientIdError = null;
        _settings.ClientId = clientId;
        await SettingsStore.SaveAsync(_settings);

        _auth = CreateAuthService(clientId);
        State = AppState.SignedOut;
        Status = "Sign in to load your library.";
    }

    /// <summary>Runs the interactive sign-in.</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (_auth is null) return;

        try
        {
            IsBusy = true;
            Status = "Waiting for Spotify in your browser…";

            var client = await _auth.AuthorizeAsync();
            await OnSignedInAsync(client);
        }
        catch (Exception e)
        {
            Status = $"Sign-in failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Syncs newly added items only.</summary>
    [RelayCommand]
    private Task QuickSyncAsync() => RunSyncAsync(SyncMode.Quick);

    /// <summary>Rebuilds the whole index, which is what notices removals.</summary>
    [RelayCommand]
    private Task FullSyncAsync() => RunSyncAsync(SyncMode.Full);

    /// <summary>Adds another column to the right of the browser.</summary>
    [RelayCommand]
    private async Task AddColumnAsync()
    {
        // Offer a criterion that isn't already in use, so the new column shows something new.
        var used = Columns.Select(c => c.Criterion.Criterion).ToHashSet();
        var next = CriterionRegistry.All.FirstOrDefault(d => !used.Contains(d.Criterion))
                   ?? CriterionRegistry.All[0];

        AttachColumn(new ColumnViewModel(next.Criterion));
        await PersistColumnsAsync();
        await RefreshFromAsync(Columns.Count - 1);
    }

    /// <summary>Removes the rightmost column, keeping at least one.</summary>
    [RelayCommand]
    private async Task RemoveColumnAsync()
    {
        if (Columns.Count <= 1) return;

        Columns.RemoveAt(Columns.Count - 1);
        await PersistColumnsAsync();
        await RefreshFromAsync(Columns.Count - 1);
    }

    /// <summary>Plays a track, in its album's context so the rest of the record follows on.</summary>
    /// <param name="track">The track to play</param>
    [RelayCommand]
    private async Task PlayTrackAsync(TrackViewModel? track)
    {
        if (track is null || _playback is null) return;

        var outcome = await _playback.PlayContextAsync(
            track.AlbumUri,
            Math.Max(0, track.TrackNumber - 1),
            SelectedDevice?.Id);

        Status = outcome switch
        {
            PlaybackOutcome.Started => $"Playing {track.Name}",
            PlaybackOutcome.HandedOff => "No Connect device — handed off to the Spotify app.",
            _ => "Couldn't start playback. Is Spotify running?"
        };
    }

    /// <summary>Plays everything currently listed.</summary>
    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (_playback is null || Tracks.Count == 0) return;

        var uris = Tracks.Select(t => t.Uri).ToList();
        var outcome = await _playback.PlayTracksAsync(uris, SelectedDevice?.Id);

        Status = outcome == PlaybackOutcome.Started
            ? $"Playing {uris.Count} tracks"
            : "Couldn't start playback on a Connect device.";
    }

    /// <summary>
    /// Adds or removes the selected tracks' albums, whichever way round the selection currently is.
    /// </summary>
    /// <remarks>
    /// Saving matters as much as removing because of the saved-albums filter: with it off the
    /// browser shows albums that only turned up through a liked song or a playlist, and those are
    /// exactly the records worth adding.
    /// </remarks>
    [RelayCommand]
    private async Task ToggleSelectedAlbumsSavedAsync()
    {
        if (_library is null) return;

        var albumIds = SelectedTracks.Select(t => t.AlbumId).Distinct().ToList();
        if (albumIds.Count == 0) return;

        var save = !SelectedAlbumsAreSaved;

        // Every row from those albums moves, not just the selected ones — an album is saved or it
        // isn't, and leaving its other tracks showing the old state would be a lie.
        var affected = Tracks.Where(t => albumIds.Contains(t.AlbumId)).ToList();

        foreach (var track in affected)
        {
            track.AlbumIsSaved = save;
        }

        // Only the write is rolled back on failure. Reloading afterwards is a separate concern:
        // if a query failed once the album was already saved, undoing the flags here would leave
        // the list claiming the opposite of what Spotify and the index both hold.
        try
        {
            await _library.SetAlbumsSavedAsync(albumIds, save);
        }
        catch (Exception e)
        {
            foreach (var track in affected)
            {
                track.AlbumIsSaved = !save;
            }

            UpdateSelectionState();
            Status = $"Couldn't update your library: {e.Message}";
            return;
        }

        var noun = albumIds.Count == 1 ? "album" : $"{albumIds.Count} albums";
        Status = save ? $"Saved {noun} to your library." : $"Removed {noun} from your library.";

        UpdateSelectionState();

        // Saved state is what the Album and Album Artist columns browse, so the columns themselves
        // have changed, not just these rows.
        await RefreshAllAsync();
    }

    /// <summary>Queues the selected tracks to play after whatever's on now.</summary>
    [RelayCommand]
    private async Task QueueSelectionAsync()
    {
        if (_playback is null) return;

        var uris = SelectedTracks.Select(t => t.Uri).ToList();
        if (uris.Count == 0) return;

        var result = await _playback.QueueAsync(uris, SelectedDevice?.Id);

        // Say what Spotify actually said. Guessing at "no device" would be wrong whenever the real
        // reason was the account, and would send someone looking in the wrong place.
        Status = result switch
        {
            { Queued: 0, Failure: { } why } => $"Couldn't queue: {why}",
            { Queued: 0 } => "Couldn't queue: nothing was accepted.",
            { Failure: { } why } => $"Queued {result.Queued} of {uris.Count} tracks, then stopped: {why}",
            _ when result.Queued < uris.Count =>
                $"Queued {result.Queued} tracks, the most one request adds.",
            { Queued: 1 } => "Queued 1 track.",
            _ => $"Queued {result.Queued} tracks."
        };
    }

    /// <summary>Flips one track's liked state.</summary>
    /// <param name="track">The track to like or unlike</param>
    [RelayCommand]
    private Task ToggleLikeAsync(TrackViewModel? track) =>
        track is null ? Task.CompletedTask : ApplyLikeAsync([track], !track.IsLiked);

    /// <summary>Likes every highlighted track.</summary>
    [RelayCommand]
    private Task LikeSelectionAsync() => ApplyLikeAsync(SelectedTracks.ToList(), true);

    /// <summary>Unlikes every highlighted track.</summary>
    [RelayCommand]
    private Task UnlikeSelectionAsync() => ApplyLikeAsync(SelectedTracks.ToList(), false);

    /// <summary>Puts back whatever the last like or unlike changed.</summary>
    [RelayCommand]
    private async Task UndoLikeAsync()
    {
        if (_library is null || _lastLikeChange.Count == 0) return;

        var ids = _lastLikeChange.ToList();
        var restore = !_lastLikeWasLiking;

        // Restore the whole set by ID rather than whatever happens to be on screen: an unlike
        // with the liked filter on removes those very rows from the list, so filtering by the
        // visible tracks would restore nothing while still claiming success.
        var visible = Tracks.Where(t => ids.Contains(t.Id)).ToList();

        foreach (var track in visible)
        {
            track.IsLiked = restore;
        }

        try
        {
            await _library.SetTracksLikedAsync(ids, restore);

            // Only forget the undo once it's actually been applied, so a failure can be retried.
            ClearUndo();
            Status = $"Restored {ids.Count} track{(ids.Count == 1 ? "" : "s")}.";
        }
        catch (Exception e)
        {
            foreach (var track in visible)
            {
                track.IsLiked = !restore;
            }

            Status = $"Couldn't undo: {e.Message}";
        }
    }

    /// <summary>Refreshes the browser from a given column rightwards.</summary>
    /// <param name="columnIndex">The column whose selection changed</param>
    public async Task RefreshFromAsync(int columnIndex)
    {
        if (_browse is null || _suppressRefresh) return;

        _refresh?.Cancel();
        var cts = new CancellationTokenSource();
        _refresh = cts;

        try
        {
            // A short debounce keeps arrow-key walks down a column from firing a query per row.
            await Task.Delay(80, cts.Token);

            // Anything to the right of the changed column is downstream, so its selection is stale.
            for (var i = columnIndex + 1; i < Columns.Count; i++)
            {
                Columns[i].ClearSelection();
            }

            // Repopulating a column empties its list box, which raises a selection change of its
            // own. Without this guard that echo re-enters here and cancels the very refresh
            // that caused it, leaving the columns to its right stale.
            _suppressRefresh = true;

            try
            {
                for (var i = Math.Max(0, columnIndex + 1); i < Columns.Count; i++)
                {
                    var values = await _browse.GetColumnValuesAsync(BuildRequest(), i, cts.Token);
                    Columns[i].SetValues(values);
                }
            }
            finally
            {
                _suppressRefresh = false;
                RefreshCompleted?.Invoke();
            }

            await RefreshTracksAsync(cts.Token);
            UpdateDiscography();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection — the newer refresh will finish the job.
        }
    }

    /// <summary>Reloads every column and the track list, after a sync or a filter change.</summary>
    /// <param name="debounce">Wait briefly first, so typing in the search box doesn't query per keystroke</param>
    public async Task RefreshAllAsync(bool debounce = false)
    {
        if (_browse is null) return;

        _refresh?.Cancel();
        var cts = new CancellationTokenSource();
        _refresh = cts;

        try
        {
            if (debounce) await Task.Delay(200, cts.Token);

            _suppressRefresh = true;

            try
            {
                for (var i = 0; i < Columns.Count; i++)
                {
                    var values = await _browse.GetColumnValuesAsync(BuildRequest(), i, cts.Token);
                    Columns[i].SetValues(values);
                }
            }
            finally
            {
                _suppressRefresh = false;
                RefreshCompleted?.Invoke();
            }

            await RefreshTracksAsync(cts.Token);
            UpdateDiscography();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Re-checks liked state for the listed tracks against Spotify.
    /// </summary>
    /// <remarks>
    /// Called when the window regains focus, which is the cheap way to notice a like made on
    /// another device without polling.
    /// </remarks>
    public async Task ReconcileLikesAsync()
    {
        if (_library is null || Tracks.Count == 0) return;

        // One request's worth is plenty — it's the rows in front of the user that matter.
        var visible = Tracks.Take(50).ToList();

        try
        {
            var corrections = await _library.ReconcileTracksAsync(visible.Select(t => t.Id).ToList());

            foreach (var track in visible)
            {
                if (corrections.TryGetValue(track.Id, out var liked)) track.IsLiked = liked;
            }
        }
        catch (APIException)
        {
            // Reconciliation is a nicety — a failure here shouldn't disturb the user.
        }
    }

    /// <summary>Refreshes the device list and now-playing line.</summary>
    public async Task RefreshPlaybackAsync()
    {
        if (_playback is null) return;

        var devices = await _playback.GetDevicesAsync();

        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(new DeviceRow(device));
        }

        SelectedDevice ??= Devices.FirstOrDefault(d => d.IsActive) ?? Devices.FirstOrDefault();

        var current = await _playback.GetCurrentAsync();

        NowPlaying = current?.Item is FullTrack playing
            ? $"{playing.Name} — {string.Join(", ", playing.Artists.Select(a => a.Name))}"
            : string.Empty;
    }

    /// <summary>Records the current window shape and column layout.</summary>
    /// <param name="width">The window's width</param>
    /// <param name="height">The window's height</param>
    /// <param name="browserHeight">How tall the column browser was left</param>
    public async Task SaveLayoutAsync(double width, double height, double browserHeight)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.BrowserHeight = browserHeight;
        _settings.LikedOnly = LikedOnly;
        _settings.SavedAlbumsOnly = SavedAlbumsOnly;
        _settings.CompactRows = CompactRows;
        _settings.DiscographySort = Discography.Sort;
        _settings.DiscographyGroups = Discography.Groups;
        _settings.Columns = Columns.Select(c => c.Criterion.Criterion).ToList();

        await SettingsStore.SaveAsync(_settings);
    }

    /// <summary>The window's remembered width.</summary>
    public double SavedWidth => _settings.WindowWidth;

    /// <summary>The window's remembered height.</summary>
    public double SavedHeight => _settings.WindowHeight;

    /// <summary>How tall the column browser was left last time.</summary>
    public double SavedBrowserHeight => _settings.BrowserHeight;

    /// <summary>Applies a like or unlike optimistically, rolling back if Spotify refuses.</summary>
    /// <param name="tracks">The tracks to change</param>
    /// <param name="liked">Whether they should end up liked</param>
    /// <param name="recordUndo">Whether this change should be undoable</param>
    private async Task ApplyLikeAsync(
        IReadOnlyList<TrackViewModel> tracks,
        bool liked,
        bool recordUndo = true)
    {
        if (_library is null || tracks.Count == 0) return;

        // Only the tracks actually changing are sent, so an undo restores exactly that set.
        var changing = tracks.Where(t => t.IsLiked != liked).ToList();
        if (changing.Count == 0) return;

        foreach (var track in changing)
        {
            track.IsLiked = liked;
        }

        try
        {
            await _library.SetTracksLikedAsync(changing.Select(t => t.Id).ToList(), liked);

            if (recordUndo && !liked) SetUndo(changing.Select(t => t.Id), liked);

            Status = liked
                ? $"Liked {changing.Count} track{(changing.Count == 1 ? "" : "s")}."
                : $"Unliked {changing.Count} track{(changing.Count == 1 ? "" : "s")}.";
        }
        catch (Exception e)
        {
            foreach (var track in changing)
            {
                track.IsLiked = !liked;
            }

            Status = $"Couldn't update Liked Songs: {e.Message}";
        }
    }

    /// <summary>
    /// Points the discography panel at whichever artist is selected, or hides it.
    /// </summary>
    /// <remarks>
    /// One artist, exactly: with several selected there's no single discography to show, and with
    /// none there's nothing to ask about. Either way the panel gets out of the way rather than
    /// showing something arbitrary.
    /// </remarks>
    private void UpdateDiscography()
    {
        var artist = Columns
            .Where(c => c.Criterion.Criterion is ColumnCriterion.Artist or ColumnCriterion.AlbumArtist)
            .Where(c => c.SelectedKeys.Count == 1)
            .Select(c => c.Values.FirstOrDefault(v => v.Key == c.SelectedKeys[0]))
            .FirstOrDefault(v => v is not null);

        if (artist is null)
        {
            Discography.Hide();
            return;
        }

        // Fire and forget: it reaches the network, and browsing shouldn't wait on it.
        _ = Discography.ShowAsync(artist.Key, artist.Display);
    }

    /// <summary>
    /// Shows a discography release's tracks in the main list.
    /// </summary>
    /// <remarks>
    /// These rows come from Spotify rather than from a browse query, so the list stops reflecting
    /// the columns for as long as they're up. Changing anything in the browser puts it back, and
    /// the status line says what's showing meanwhile.
    /// </remarks>
    /// <param name="album">The release picked</param>
    /// <param name="tracks">Its tracks, as fetched</param>
    private void ShowDiscographyTracks(
        DiscographyAlbumViewModel album,
        IReadOnlyList<DiscographyTrack> tracks)
    {
        _showingDiscographyAlbum = true;

        SelectedTracks.Clear();
        Tracks = new ObservableCollection<TrackViewModel>(
            tracks.Select(t => new TrackViewModel(new TrackRow(
                Id: t.Id,
                Uri: t.Uri,
                Name: t.Name,
                ArtistNames: t.ArtistNames,
                AlbumId: t.AlbumId,
                AlbumName: t.AlbumName,
                Year: null,
                DiscNumber: t.DiscNumber,
                TrackNumber: t.TrackNumber,
                DurationMs: t.DurationMs,
                Explicit: t.Explicit,
                IsLiked: t.IsLiked,
                AlbumIsSaved: album.IsSaved))));

        TrackCount = tracks.Count;
        Status = $"Showing {album.Name} from Spotify. Change a column to go back to your library.";
    }

    /// <summary>Recomputes what the album action should offer for the current selection.</summary>
    private void UpdateSelectionState()
    {
        HasSelection = SelectedTracks.Count > 0;

        var albumIds = SelectedTracks.Select(t => t.AlbumId).Distinct().ToList();

        // A mixed selection counts as not-saved, so the offer is to save. That's the direction
        // that adds rather than removes, which is the safer way for an ambiguous click to go.
        SelectedAlbumsAreSaved = albumIds.Count > 0 && SelectedTracks.All(t => t.AlbumIsSaved);

        var noun = albumIds.Count > 1 ? $"{albumIds.Count} albums" : "album";

        AlbumActionText = SelectedAlbumsAreSaved
            ? $"Remove {noun} from library"
            : $"Save {noun} to library";
    }

    /// <summary>Remembers a change so it can be taken back.</summary>
    /// <param name="ids">The tracks that changed</param>
    /// <param name="liked">What they were changed to</param>
    private void SetUndo(IEnumerable<string> ids, bool liked)
    {
        _lastLikeChange.Clear();
        _lastLikeChange.AddRange(ids);
        _lastLikeWasLiking = liked;

        UndoMessage = $"Unliked {_lastLikeChange.Count} track{(_lastLikeChange.Count == 1 ? "" : "s")}.";
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Forgets the pending undo.</summary>
    private void ClearUndo()
    {
        _lastLikeChange.Clear();
        UndoMessage = null;
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Runs a sync and reloads the browser from the result.</summary>
    /// <param name="mode">Which kind of sync to run</param>
    private async Task RunSyncAsync(SyncMode mode)
    {
        if (_sync is null) return;

        try
        {
            IsBusy = true;
            var progress = new Progress<SyncProgress>(p =>
                Status = p.Total is { } total
                    ? $"{p.Stage}: {p.Completed} of {total}"
                    : $"{p.Stage}: {p.Completed}");

            var counts = await _sync.SyncAsync(mode, progress);
            var playlistDetail = await _repository!.GetSyncStateAsync("playlists_detail");

            Status = $"{counts.Tracks} tracks · {counts.SavedAlbums} saved albums · " +
                     $"{counts.LikedTracks} liked · " +
                     $"{counts.ReadablePlaylists} of {counts.Playlists} playlists readable " +
                     $"({counts.PlaylistTracks} entries) · {counts.FollowedArtists} followed artists" +
                     (playlistDetail is null ? string.Empty : $" · Playlists: {playlistDetail}");

            await RefreshAllAsync();
        }
        catch (Exception e)
        {
            Status = $"Sync failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Wires up the services that need an authenticated client.</summary>
    /// <param name="client">The freshly authenticated client</param>
    private async Task OnSignedInAsync(SpotifyClient client)
    {
        _sync = new LibrarySyncService(client, _repository!, _throttle);
        _library = new LibraryWriteService(client.Library, _repository!, _throttle);
        _playback = new PlaybackController(client.Player, _throttle);

        Discography.Connect(
            new DiscographyService(client.Artists, client.Albums, _discographyStore!, _throttle),
            _library,
            _playback,
            client.Library);

        State = AppState.Ready;

        await RefreshAllAsync();
        await RefreshPlaybackAsync();

        var counts = await _repository!.GetCountsAsync();

        Status = counts.Tracks == 0
            ? "Signed in. Run a sync to load your library."
            : $"{counts.Tracks} tracks indexed.";
    }

    /// <summary>Builds the auth service and keeps settings in step with token changes.</summary>
    /// <param name="clientId">The registered Client ID</param>
    /// <returns>The configured auth service</returns>
    private PkceAuthService CreateAuthService(string clientId) =>
        new(clientId, new TokenStore(AppPaths.TokenFile));

    /// <summary>Replaces the column set, wiring each column's events.</summary>
    /// <param name="criteria">The criteria to build columns for</param>
    private void BuildColumns(IEnumerable<ColumnCriterion> criteria)
    {
        Columns.Clear();

        foreach (var criterion in criteria)
        {
            AttachColumn(new ColumnViewModel(criterion));
        }

        if (Columns.Count == 0) AttachColumn(new ColumnViewModel(ColumnCriterion.AlbumArtist));
    }

    /// <summary>Adds a column and subscribes to its criterion changes.</summary>
    /// <param name="column">The column to add</param>
    private void AttachColumn(ColumnViewModel column)
    {
        column.CriterionChanged += async changed =>
        {
            var index = Columns.IndexOf(changed);
            if (index < 0) return;

            // This runs from an event, so nothing above it would catch a throw.
            try
            {
                await PersistColumnsAsync();

                // The column's own values change too, so this rebuild starts at the column itself.
                if (_browse is not null)
                {
                    var values = await _browse.GetColumnValuesAsync(BuildRequest(), index);
                    changed.SetValues(values);
                }

                await RefreshFromAsync(index);
            }
            catch (Exception e)
            {
                Status = $"Couldn't change that column: {e.Message}";
            }
        };

        Columns.Add(column);
    }

    /// <summary>Saves the column layout.</summary>
    private async Task PersistColumnsAsync()
    {
        _settings.Columns = Columns.Select(c => c.Criterion.Criterion).ToList();
        await SettingsStore.SaveAsync(_settings);
    }

    /// <summary>Snapshots the browser state for a query.</summary>
    /// <returns>The current browse request</returns>
    private BrowseRequest BuildRequest() =>
        new(Columns.Select(c => c.ToSelection()).ToList(), LikedOnly, SavedAlbumsOnly,
            string.IsNullOrWhiteSpace(Search) ? null : Search);

    /// <summary>Reloads the track list for the current state.</summary>
    /// <param name="cancel">Cancels the query</param>
    private async Task RefreshTracksAsync(CancellationToken cancel)
    {
        if (_browse is null) return;

        var rows = await _browse.GetTracksAsync(BuildRequest(), cancel);

        // The list is going back to reflecting the browser, so the panel's opened release stops
        // being what's on show and shouldn't stay highlighted.
        if (_showingDiscographyAlbum)
        {
            _showingDiscographyAlbum = false;
            Discography.SelectedAlbum = null;
        }

        SelectedTracks.Clear();
        Tracks = new ObservableCollection<TrackViewModel>(rows.Select(r => new TrackViewModel(r)));
        TrackCount = rows.Count;
    }

    /// <summary>Keeps the screen-selecting properties in step with the state.</summary>
    /// <param name="value">The new state</param>
    partial void OnStateChanged(AppState value)
    {
        OnPropertyChanged(nameof(IsNeedsClientId));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(IsReady));
    }

    /// <summary>Re-queries when the search text changes.</summary>
    /// <param name="value">The new search text</param>
    partial void OnSearchChanged(string value) => _ = RefreshAllAsync(debounce: true);

    /// <summary>Re-queries when the liked filter is toggled.</summary>
    /// <param name="value">Whether the filter is on</param>
    partial void OnLikedOnlyChanged(bool value) => _ = RefreshAllAsync();

    /// <summary>
    /// Remembers the row density as soon as it's changed.
    /// </summary>
    /// <remarks>
    /// Written straight away rather than only on close, so a crash or a force-quit doesn't lose a
    /// preference the user set deliberately. Nothing needs requerying: it's purely how rows look.
    /// </remarks>
    /// <param name="value">Whether rows are compact</param>
    partial void OnCompactRowsChanged(bool value)
    {
        _settings.CompactRows = value;
        _ = PersistSettingsAsync();
    }

    /// <summary>Re-queries and remembers the choice when the saved-albums filter is toggled.</summary>
    /// <param name="value">Whether browsing is restricted to saved albums</param>
    partial void OnSavedAlbumsOnlyChanged(bool value)
    {
        _settings.SavedAlbumsOnly = value;
        _ = PersistSettingsAsync();
        _ = RefreshAllAsync();
    }

    /// <summary>Writes settings without disturbing the caller.</summary>
    private async Task PersistSettingsAsync()
    {
        try
        {
            await SettingsStore.SaveAsync(_settings);
        }
        catch (IOException)
        {
            // Losing a preference isn't worth interrupting browsing for.
        }
    }
}
