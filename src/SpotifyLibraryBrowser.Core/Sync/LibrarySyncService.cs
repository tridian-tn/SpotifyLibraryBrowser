using System.Net;
using SpotifyAPI.Web;
using SpotifyLibraryBrowser.Core.Api;
using SpotifyLibraryBrowser.Core.Data;
using SpotifyLibraryBrowser.Core.Model;

namespace SpotifyLibraryBrowser.Core.Sync;

/// <summary>How much of the library a sync walks.</summary>
public enum SyncMode
{
    /// <summary>Walk newest-first and stop at the first already-known item.</summary>
    Quick = 0,

    /// <summary>Walk everything, so removals are noticed too.</summary>
    Full = 1
}

/// <summary>Progress through a sync, for the status line.</summary>
/// <param name="Stage">What's being synced right now</param>
/// <param name="Completed">Items handled so far in this stage</param>
/// <param name="Total">Total expected, where Spotify reported one</param>
/// <param name="Detail">
/// A note about what actually happened, used to explain a stage that completed without storing
/// anything rather than leaving it looking silently empty
/// </param>
public sealed record SyncProgress(
    string Stage,
    int Completed,
    int? Total = null,
    string? Detail = null);

/// <summary>What came back when a playlist's contents were requested.</summary>
/// <param name="Stored">How many track entries were written</param>
/// <param name="Refusal">Why Spotify wouldn't serve it, or null when it did</param>
internal sealed record PlaylistReadResult(int Stored, string? Refusal)
{
    /// <summary>Whether Spotify served the contents at all.</summary>
    public bool Served => Refusal is null;
}

/// <summary>
/// Crawls the four parts of the library into the local index.
/// </summary>
/// <remarks>
/// Everything here reads only fields that are current in the Web API. Paging is done by hand
/// rather than through the library's paginator so each page can report progress, respect the
/// shared throttle, and let a quick sync stop early.
/// </remarks>
/// <param name="client">The authenticated Spotify client</param>
/// <param name="repository">The local index</param>
/// <param name="throttle">The shared rate gate</param>
public sealed class LibrarySyncService(
    SpotifyClient client,
    LibraryRepository repository,
    RequestThrottle throttle)
{
    private const int PageSize = 50;

    /// <summary>Runs a full pass over the library.</summary>
    /// <param name="mode">Whether to stop early at known items or walk everything</param>
    /// <param name="progress">Receives stage-by-stage progress</param>
    /// <param name="cancel">Cancels the sync</param>
    /// <returns>The index's row counts once the sync finishes</returns>
    public async Task<LibraryCounts> SyncAsync(
        SyncMode mode,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancel = default)
    {
        if (mode == SyncMode.Full) await repository.ResetForFullRebuildAsync(cancel).ConfigureAwait(false);

        var userId = await GetCurrentUserIdAsync(cancel).ConfigureAwait(false);

        await SyncSavedAlbumsAsync(mode, progress, cancel).ConfigureAwait(false);
        await SyncLikedSongsAsync(mode, progress, cancel).ConfigureAwait(false);
        await SyncPlaylistsAsync(userId, mode, progress, cancel).ConfigureAwait(false);
        await SyncFollowedArtistsAsync(progress, cancel).ConfigureAwait(false);

        if (mode == SyncMode.Full) await repository.PruneOrphansAsync(cancel).ConfigureAwait(false);

        await repository
            .SetSyncStateAsync("last_sync", DateTimeOffset.UtcNow.ToString("O"), cancel)
            .ConfigureAwait(false);

        return await repository.GetCountsAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Reads the signed-in user's ID, which is how playlist ownership is decided.</summary>
    /// <param name="cancel">Cancels the call</param>
    /// <returns>The current user's Spotify ID</returns>
    private async Task<string> GetCurrentUserIdAsync(CancellationToken cancel)
    {
        await throttle.WaitAsync(cancel).ConfigureAwait(false);
        var profile = await client.UserProfile.Current(cancel).ConfigureAwait(false);
        return profile.Id;
    }

    /// <summary>Walks saved albums, writing each album, its artists and its tracks.</summary>
    /// <param name="mode">Whether to stop at the first already-known album</param>
    /// <param name="progress">Receives progress</param>
    /// <param name="cancel">Cancels the crawl</param>
    private async Task SyncSavedAlbumsAsync(
        SyncMode mode,
        IProgress<SyncProgress>? progress,
        CancellationToken cancel)
    {
        var boundary = mode == SyncMode.Quick
            ? await repository.GetNewestAddedAtAsync(likedOnly: false, cancel).ConfigureAwait(false)
            : null;

        var offset = 0;
        var done = 0;
        int? total = null;

        while (true)
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            var page = await client.Library
                .GetAlbums(new LibraryAlbumsRequest { Limit = PageSize, Offset = offset }, cancel)
                .ConfigureAwait(false);

            total ??= page.Total;
            if (page.Items is not { Count: > 0 }) break;

            await using (var writer = await repository.BeginWriteAsync(cancel).ConfigureAwait(false))
            {
                foreach (var saved in page.Items)
                {
                    var addedAt = ToOffset(saved.AddedAt);

                    // Saved albums come back newest-first, so the first already-known one means
                    // everything past it is known too.
                    if (boundary is not null && addedAt is not null && addedAt <= boundary)
                    {
                        await writer.CommitAsync(cancel).ConfigureAwait(false);
                        progress?.Report(new SyncProgress("Saved albums", done, total));
                        return;
                    }

                    await WriteAlbumAsync(writer, saved.Album, addedAt, cancel).ConfigureAwait(false);
                    done++;
                }

                await writer.CommitAsync(cancel).ConfigureAwait(false);
            }

            progress?.Report(new SyncProgress("Saved albums", done, total));

            offset += page.Items.Count;
            if (offset >= page.Total) break;
        }
    }

    /// <summary>Writes one album, pulling any tracks beyond the first page.</summary>
    /// <param name="writer">The open batch writer</param>
    /// <param name="album">The album to write</param>
    /// <param name="addedAt">When it was saved</param>
    /// <param name="cancel">Cancels the work</param>
    private async Task WriteAlbumAsync(
        SyncWriter writer,
        FullAlbum album,
        DateTimeOffset? addedAt,
        CancellationToken cancel)
    {
        await writer.UpsertAlbumAsync(MapAlbum(album, addedAt), MapArtists(album.Artists), cancel)
            .ConfigureAwait(false);

        var tracks = album.Tracks?.Items ?? [];

        foreach (var track in tracks)
        {
            await WriteAlbumTrackAsync(writer, album.Id, track, cancel).ConfigureAwait(false);
        }

        // An album object only carries its first page of tracks, so long records need the rest.
        var fetched = tracks.Count;

        while (album.Tracks is not null && fetched < album.Tracks.Total)
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            var page = await client.Albums
                .GetTracks(album.Id, new AlbumTracksRequest { Limit = PageSize, Offset = fetched }, cancel)
                .ConfigureAwait(false);

            if (page.Items is not { Count: > 0 }) break;

            foreach (var track in page.Items)
            {
                await WriteAlbumTrackAsync(writer, album.Id, track, cancel).ConfigureAwait(false);
            }

            fetched += page.Items.Count;
        }
    }

    /// <summary>Writes one track from an album's track list.</summary>
    /// <param name="writer">The open batch writer</param>
    /// <param name="albumId">The album the track belongs to</param>
    /// <param name="track">The track to write</param>
    /// <param name="cancel">Cancels the work</param>
    private static async Task WriteAlbumTrackAsync(
        SyncWriter writer,
        string albumId,
        SimpleTrack track,
        CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(track.Id)) return;

        var mapped = new Track(
            track.Id,
            track.Name,
            albumId,
            track.DiscNumber,
            track.TrackNumber,
            track.DurationMs,
            track.Explicit,
            track.Uri,
            AddedAt: null,
            TrackSource.SavedAlbum,
            IsLiked: false);

        await writer.UpsertTrackAsync(mapped, MapArtists(track.Artists), cancel).ConfigureAwait(false);
    }

    /// <summary>Walks Liked Songs, marking each track liked.</summary>
    /// <param name="mode">Whether to stop at the first already-known track</param>
    /// <param name="progress">Receives progress</param>
    /// <param name="cancel">Cancels the crawl</param>
    private async Task SyncLikedSongsAsync(
        SyncMode mode,
        IProgress<SyncProgress>? progress,
        CancellationToken cancel)
    {
        var boundary = mode == SyncMode.Quick
            ? await repository.GetNewestAddedAtAsync(likedOnly: true, cancel).ConfigureAwait(false)
            : null;

        var offset = 0;
        var done = 0;
        int? total = null;

        while (true)
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            var page = await client.Library
                .GetTracks(new LibraryTracksRequest { Limit = PageSize, Offset = offset }, cancel)
                .ConfigureAwait(false);

            total ??= page.Total;
            if (page.Items is not { Count: > 0 }) break;

            await using (var writer = await repository.BeginWriteAsync(cancel).ConfigureAwait(false))
            {
                foreach (var saved in page.Items)
                {
                    var addedAt = ToOffset(saved.AddedAt);

                    if (boundary is not null && addedAt is not null && addedAt <= boundary)
                    {
                        await writer.CommitAsync(cancel).ConfigureAwait(false);
                        progress?.Report(new SyncProgress("Liked songs", done, total));
                        return;
                    }

                    await WriteFullTrackAsync(writer, saved.Track, addedAt, TrackSource.None, true, cancel)
                        .ConfigureAwait(false);

                    done++;
                }

                await writer.CommitAsync(cancel).ConfigureAwait(false);
            }

            progress?.Report(new SyncProgress("Liked songs", done, total));

            offset += page.Items.Count;
            if (offset >= page.Total) break;
        }
    }

    /// <summary>
    /// Walks the user's playlists, reading contents only where that's permitted.
    /// </summary>
    /// <remarks>
    /// Since February 2026 a playlist's items are only readable when you own or collaborate on it,
    /// so Spotify-curated playlists are recorded by name and skipped rather than attempted.
    /// Playlists whose snapshot ID hasn't moved are skipped too, which is the big re-sync saving.
    /// </remarks>
    /// <param name="userId">The signed-in user's ID</param>
    /// <param name="mode">Whether unchanged playlists may be skipped</param>
    /// <param name="progress">Receives progress</param>
    /// <param name="cancel">Cancels the crawl</param>
    private async Task SyncPlaylistsAsync(
        string userId,
        SyncMode mode,
        IProgress<SyncProgress>? progress,
        CancellationToken cancel)
    {
        var known = await repository.GetPlaylistSnapshotsAsync(cancel).ConfigureAwait(false);
        var offset = 0;
        var done = 0;
        var served = 0;
        var refused = 0;
        var skipped = 0;
        var stored = 0;
        string? firstRefusal = null;
        int? total = null;

        while (true)
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            var page = await client.Playlists
                .CurrentUsers(new PlaylistCurrentUsersRequest { Limit = PageSize, Offset = offset }, cancel)
                .ConfigureAwait(false);

            total ??= page.Total;
            if (page.Items is not { Count: > 0 }) break;

            foreach (var playlist in page.Items)
            {
                if (playlist.Id is null) continue;

                var owned = playlist.Owner?.Id == userId || playlist.Collaborative == true;

                var reference = new PlaylistRef(
                    playlist.Id,
                    playlist.Name ?? "Untitled",
                    playlist.Owner?.Id ?? string.Empty,
                    playlist.SnapshotId ?? string.Empty,
                    owned);

                // A full rebuild has just emptied playlist_tracks, so trusting the snapshot here
                // would skip the re-read and leave the playlist permanently empty. Only a quick
                // sync may take that shortcut.
                var unchanged = mode == SyncMode.Quick
                                && known.TryGetValue(reference.Id, out var snapshot)
                                && snapshot == reference.SnapshotId;

                // The contents are read for every playlist rather than only ones that look owned.
                // Working ownership out from the owner ID is guesswork, and getting it wrong means
                // a playlist silently never gets its tracks; letting Spotify refuse is definitive.
                var readable = true;

                if (unchanged)
                {
                    skipped++;
                }
                else
                {
                    var result = await TryReadPlaylistItemsAsync(reference.Id, cancel)
                        .ConfigureAwait(false);

                    readable = result.Served;

                    if (readable)
                    {
                        served++;
                        stored += result.Stored;
                    }
                    else
                    {
                        refused++;
                        firstRefusal ??= $"{reference.Name}: {result.Refusal}";
                    }
                }

                await using (var writer = await repository.BeginWriteAsync(cancel).ConfigureAwait(false))
                {
                    // A playlist whose read failed for a transient reason keeps no snapshot, so the
                    // next sync tries again rather than skipping it forever.
                    await writer.UpsertPlaylistAsync(
                        readable ? reference : reference with { SnapshotId = string.Empty },
                        cancel).ConfigureAwait(false);

                    await writer.CommitAsync(cancel).ConfigureAwait(false);
                }

                done++;
                progress?.Report(new SyncProgress("Playlists", done, total));
            }

            offset += page.Items.Count;
            if (offset >= page.Total) break;
        }

        // Say what happened, so "no playlists" can be told apart from "every read was refused"
        // and from "they were read but held nothing we can store".
        var detail = refused > 0
            ? $"{served} read, {skipped} unchanged, {refused} refused. First refusal: {firstRefusal}"
            : $"{served} read ({stored} entries), {skipped} unchanged";

        progress?.Report(new SyncProgress("Playlists", done, total, detail));
        await repository.SetSyncStateAsync("playlists_detail", detail, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a playlist's items, reporting whether Spotify allowed it.
    /// </summary>
    /// <remarks>
    /// Since February 2026 only playlists you own or collaborate on can be enumerated. A refusal
    /// is an expected answer for everything else, not an error worth failing the sync over.
    /// </remarks>
    /// <param name="playlistId">The playlist to read</param>
    /// <param name="cancel">Cancels the crawl</param>
    /// <returns>True when the contents were read, false when Spotify wouldn't serve them</returns>
    private async Task<PlaylistReadResult> TryReadPlaylistItemsAsync(
        string playlistId,
        CancellationToken cancel)
    {
        try
        {
            var stored = await SyncPlaylistItemsAsync(playlistId, cancel).ConfigureAwait(false);
            return new PlaylistReadResult(stored, null);
        }
        catch (APIException e) when (
            e.Response?.StatusCode is HttpStatusCode.Forbidden
                                   or HttpStatusCode.NotFound
                                   or HttpStatusCode.Unauthorized)
        {
            // Keep the reason: a stage that stores nothing needs to say why rather than looking
            // like the library is simply empty.
            return new PlaylistReadResult(0, $"{(int)e.Response!.StatusCode} {e.Message}".Trim());
        }
    }

    /// <summary>Reads one playlist's items, replacing whatever was held for it.</summary>
    /// <param name="playlistId">The playlist to read</param>
    /// <param name="cancel">Cancels the crawl</param>
    /// <returns>How many track entries were stored</returns>
    private async Task<int> SyncPlaylistItemsAsync(string playlistId, CancellationToken cancel)
    {
        var offset = 0;
        var stored = 0;

        await using var writer = await repository.BeginWriteAsync(cancel).ConfigureAwait(false);
        await writer.ClearPlaylistTracksAsync(playlistId, cancel).ConfigureAwait(false);

        while (true)
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            var page = await client.Playlists
                .GetPlaylistItems(playlistId, new PlaylistGetItemsRequest { Limit = 100, Offset = offset }, cancel)
                .ConfigureAwait(false);

            if (page.Items is not { Count: > 0 }) break;

            var position = offset;

            foreach (var item in page.Items)
            {
                // Read Item, not Track. February 2026 deprecated the per-entry "track" field in
                // favour of "item", and the old one now comes back empty — which made every
                // playlist look as though it held nothing at all.
                // A playlist can also hold episodes and local files, which aren't tracks.
                if (item.Item is FullTrack track && !string.IsNullOrEmpty(track.Id))
                {
                    await WriteFullTrackAsync(writer, track, null, TrackSource.Playlist, false, cancel)
                        .ConfigureAwait(false);

                    await writer
                        .AddPlaylistTrackAsync(playlistId, track.Id, position, ToOffset(item.AddedAt), cancel)
                        .ConfigureAwait(false);

                    stored++;
                }

                position++;
            }

            offset += page.Items.Count;
            if (offset >= page.Total) break;
        }

        await writer.CommitAsync(cancel).ConfigureAwait(false);
        return stored;
    }

    /// <summary>Records the artists the user follows, which brings their images along too.</summary>
    /// <param name="progress">Receives progress</param>
    /// <param name="cancel">Cancels the crawl</param>
    private async Task SyncFollowedArtistsAsync(IProgress<SyncProgress>? progress, CancellationToken cancel)
    {
        await throttle.WaitAsync(cancel).ConfigureAwait(false);

        var first = await client.Follow.OfCurrentUser(cancel).ConfigureAwait(false);
        var done = 0;

        await using var writer = await repository.BeginWriteAsync(cancel).ConfigureAwait(false);

        // Followed artists page by cursor rather than offset, so this uses the library's paginator.
        await foreach (var artist in client
            .Paginate(first.Artists, response => response.Artists, cancel: cancel)
            .WithCancellation(cancel)
            .ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(artist.Id)) continue;

            await writer.UpsertArtistAsync(MapArtist(artist), cancel).ConfigureAwait(false);
            await writer.AddFollowedArtistAsync(artist.Id, cancel).ConfigureAwait(false);

            done++;
            if (done % 25 == 0) progress?.Report(new SyncProgress("Followed artists", done));
        }

        await writer.CommitAsync(cancel).ConfigureAwait(false);
        progress?.Report(new SyncProgress("Followed artists", done));
    }

    /// <summary>Writes a track that arrived as a full object, along with its album and artists.</summary>
    /// <param name="writer">The open batch writer</param>
    /// <param name="track">The track to write</param>
    /// <param name="addedAt">When it entered the library, where that's known</param>
    /// <param name="source">Why it's being indexed</param>
    /// <param name="liked">Whether it's in Liked Songs</param>
    /// <param name="cancel">Cancels the work</param>
    private static async Task WriteFullTrackAsync(
        SyncWriter writer,
        FullTrack track,
        DateTimeOffset? addedAt,
        TrackSource source,
        bool liked,
        CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(track.Id) || track.Album?.Id is null) return;

        await writer
            .UpsertAlbumAsync(MapAlbum(track.Album, null), MapArtists(track.Album.Artists), cancel)
            .ConfigureAwait(false);

        var mapped = new Track(
            track.Id,
            track.Name,
            track.Album.Id,
            track.DiscNumber,
            track.TrackNumber,
            track.DurationMs,
            track.Explicit,
            track.Uri,
            addedAt,
            source,
            liked);

        await writer.UpsertTrackAsync(mapped, MapArtists(track.Artists), cancel).ConfigureAwait(false);
    }

    /// <summary>Maps an album, taking only fields that are current in the API.</summary>
    /// <param name="album">The album to map</param>
    /// <param name="addedAt">When it was saved, where that's known</param>
    /// <returns>The mapped album</returns>
    /// <remarks>
    /// This overload only ever sees albums reached through a track, so the album itself isn't in
    /// the library and mustn't be marked saved.
    /// </remarks>
    private static Model.Album MapAlbum(SimpleAlbum album, DateTimeOffset? addedAt) => new(
        album.Id,
        album.Name,
        album.ReleaseDate,
        ParsePrecision(album.ReleaseDatePrecision),
        album.Images?.FirstOrDefault()?.Url,
        album.TotalTracks,
        addedAt,
        IsSaved: false);

    /// <summary>Maps a full album, taking only fields that are current in the API.</summary>
    /// <param name="album">The album to map</param>
    /// <param name="addedAt">When it was saved</param>
    /// <returns>The mapped album</returns>
    /// <remarks>Only the saved-albums crawl uses this, so these are the albums genuinely in the library.</remarks>
    private static Model.Album MapAlbum(FullAlbum album, DateTimeOffset? addedAt) => new(
        album.Id,
        album.Name,
        album.ReleaseDate,
        ParsePrecision(album.ReleaseDatePrecision),
        album.Images?.FirstOrDefault()?.Url,
        album.TotalTracks,
        addedAt,
        IsSaved: true);

    /// <summary>Maps embedded artists, which carry no image of their own.</summary>
    /// <param name="artists">The artists to map</param>
    /// <returns>The mapped artists, in credit order</returns>
    private static List<Model.Artist> MapArtists(IEnumerable<SimpleArtist>? artists) =>
        artists?
            .Where(a => !string.IsNullOrEmpty(a.Id))
            .Select(a => new Model.Artist(a.Id, a.Name))
            .ToList() ?? [];

    /// <summary>Maps a full artist, which does carry an image.</summary>
    /// <param name="artist">The artist to map</param>
    /// <returns>The mapped artist</returns>
    private static Model.Artist MapArtist(FullArtist artist) =>
        new(artist.Id, artist.Name, artist.Images?.FirstOrDefault()?.Url);

    /// <summary>Reads Spotify's release-date precision string.</summary>
    /// <param name="precision">The precision as Spotify reported it</param>
    /// <returns>The matching precision, or unknown when it's missing or unrecognised</returns>
    private static ReleasePrecision ParsePrecision(string? precision) => precision switch
    {
        "day" => ReleasePrecision.Day,
        "month" => ReleasePrecision.Month,
        "year" => ReleasePrecision.Year,
        _ => ReleasePrecision.Unknown
    };

    /// <summary>Treats an API timestamp as UTC, which is what Spotify returns.</summary>
    /// <param name="value">The timestamp to convert</param>
    /// <returns>The timestamp with a UTC offset, or null</returns>
    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
