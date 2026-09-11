using Microsoft.Data.Sqlite;

namespace SpotifyLibraryBrowser.Core.Data;

/// <summary>Row counts across the index, used to sanity-check a sync against the official client.</summary>
/// <param name="Artists">Distinct artists known</param>
/// <param name="Albums">Albums held, including ones reached only through a track</param>
/// <param name="SavedAlbums">Albums actually in the library, which is what the Album column browses</param>
/// <param name="Tracks">Tracks held</param>
/// <param name="LikedTracks">Tracks currently in Liked Songs</param>
/// <param name="Playlists">Playlists held</param>
/// <param name="ReadablePlaylists">
/// Playlists whose contents Spotify let us read. Counted from the stored snapshot rather than
/// from stored entries, since a readable playlist that happens to be empty is still readable
/// </param>
/// <param name="PlaylistTracks">Playlist entries stored</param>
/// <param name="FollowedArtists">Artists the user follows</param>
public sealed record LibraryCounts(
    int Artists,
    int Albums,
    int SavedAlbums,
    int Tracks,
    int LikedTracks,
    int Playlists,
    int ReadablePlaylists,
    int PlaylistTracks,
    int FollowedArtists);

/// <summary>
/// Index-wide operations that sit outside a single sync batch: starting a write, reading sync
/// bookkeeping, changing liked state, and the housekeeping a full rebuild needs.
/// </summary>
/// <param name="database">The local index</param>
public sealed class LibraryRepository(LibraryDatabase database)
{
    /// <summary>Opens a transactional writer for a batch of sync results.</summary>
    /// <param name="cancel">Cancels the open</param>
    /// <returns>A writer the caller must commit and dispose</returns>
    public async Task<SyncWriter> BeginWriteAsync(CancellationToken cancel = default)
    {
        var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancel).ConfigureAwait(false);
        return new SyncWriter(connection, transaction);
    }

    /// <summary>
    /// Reads the stored snapshot ID of every known playlist, so a re-sync can skip the ones
    /// Spotify says haven't changed.
    /// </summary>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>Playlist ID to snapshot ID</returns>
    public async Task<Dictionary<string, string>> GetPlaylistSnapshotsAsync(CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, snapshot_id FROM playlists;";

        var snapshots = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync(cancel).ConfigureAwait(false);

        while (await reader.ReadAsync(cancel).ConfigureAwait(false))
        {
            snapshots[reader.GetString(0)] = reader.GetString(1);
        }

        return snapshots;
    }

    /// <summary>
    /// Reads the newest <c>added_at</c> already held for a given track source, which is where a
    /// quick sync stops walking.
    /// </summary>
    /// <param name="likedOnly">Look at liked tracks rather than all tracks</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>The newest timestamp held, or null when nothing's been synced yet</returns>
    public async Task<DateTimeOffset?> GetNewestAddedAtAsync(
        bool likedOnly,
        CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = likedOnly
            ? "SELECT MAX(liked_at) FROM tracks WHERE is_liked = 1;"
            : "SELECT MAX(added_at) FROM albums;";

        var result = await command.ExecuteScalarAsync(cancel).ConfigureAwait(false);

        if (result is null or DBNull) return null;

        return DateTimeOffset.TryParse((string)result, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Sets liked state on a set of tracks, which is what the like/unlike toggle writes locally
    /// before and after calling the API.
    /// </summary>
    /// <param name="trackIds">The tracks to change</param>
    /// <param name="liked">Whether they should be liked</param>
    /// <param name="cancel">Cancels the write</param>
    /// <returns>How many rows changed</returns>
    public async Task<int> SetLikedAsync(
        IReadOnlyCollection<string> trackIds,
        bool liked,
        CancellationToken cancel = default)
    {
        if (trackIds.Count == 0) return 0;

        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancel).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tracks SET is_liked = @liked, liked_at = @at WHERE id = @id;
            """;

        var changed = 0;

        foreach (var id in trackIds)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@liked", liked ? 1 : 0);
            command.Parameters.AddWithValue("@at", liked ? DateTimeOffset.UtcNow.ToString("O") : (object)DBNull.Value);
            command.Parameters.AddWithValue("@id", id);

            changed += await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancel).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Marks albums as being in the library or not, which is what the save and remove actions
    /// write locally before and after calling the API.
    /// </summary>
    /// <remarks>
    /// Removing only clears the flag; the album row stays because liked songs and playlist
    /// entries may still point at it. A full rebuild is what eventually prunes it.
    /// </remarks>
    /// <param name="albumIds">The albums to change</param>
    /// <param name="saved">Whether they should count as being in the library</param>
    /// <param name="cancel">Cancels the write</param>
    /// <returns>How many rows changed</returns>
    public async Task<int> SetAlbumsSavedAsync(
        IReadOnlyCollection<string> albumIds,
        bool saved,
        CancellationToken cancel = default)
    {
        if (albumIds.Count == 0) return 0;

        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancel).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE albums SET is_saved = @saved, added_at = @at WHERE id = @id;";

        var changed = 0;

        foreach (var id in albumIds)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@saved", saved ? 1 : 0);
            command.Parameters.AddWithValue("@at", saved ? DateTimeOffset.UtcNow.ToString("O") : (object)DBNull.Value);
            command.Parameters.AddWithValue("@id", id);

            changed += await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancel).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Makes sure an album has a row, so a saved flag has somewhere to live.
    /// </summary>
    /// <remarks>
    /// Saving a release found through the discography panel would otherwise update nothing: the
    /// index only holds albums it has a track from, so <see cref="SetAlbumsSavedAsync"/> would
    /// match no rows and Spotify would report success while the panel reverted on reload. The
    /// album's tracks still aren't indexed — the next sync brings those.
    /// </remarks>
    /// <param name="album">The album to make sure exists</param>
    /// <param name="artists">Its credited artists</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task EnsureAlbumAsync(
        Model.Album album,
        IReadOnlyList<Model.Artist> artists,
        CancellationToken cancel = default)
    {
        await using var writer = await BeginWriteAsync(cancel).ConfigureAwait(false);

        await writer.UpsertAlbumAsync(album, artists, cancel).ConfigureAwait(false);
        await writer.CommitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the state a full rebuild re-establishes, so removals actually disappear.
    /// </summary>
    /// <remarks>
    /// Source flags and liked state are zeroed rather than the rows dropped — the crawls set them
    /// again, and <see cref="PruneOrphansAsync"/> then removes whatever nothing claimed.
    /// </remarks>
    /// <param name="cancel">Cancels the write</param>
    public async Task ResetForFullRebuildAsync(CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // is_saved is cleared too, so an album removed from the library stops being browsable
        // through the Album and Album Artist columns once the crawl doesn't re-mark it.
        command.CommandText = """
            UPDATE tracks SET source_mask = 0, is_liked = 0, liked_at = NULL;
            UPDATE albums SET is_saved = 0, added_at = NULL;
            DELETE FROM playlist_tracks;
            DELETE FROM followed_artists;
            """;

        await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Drops tracks and albums nothing refers to any more, after a full rebuild.</summary>
    /// <param name="cancel">Cancels the write</param>
    public async Task PruneOrphansAsync(CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM tracks WHERE source_mask = 0 AND is_liked = 0;

            DELETE FROM albums WHERE id NOT IN (SELECT DISTINCT album_id FROM tracks);

            DELETE FROM artists
            WHERE id NOT IN (SELECT artist_id FROM track_artists)
              AND id NOT IN (SELECT artist_id FROM album_artists)
              AND id NOT IN (SELECT artist_id FROM followed_artists);
            """;

        await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Counts what's in the index.</summary>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>Row counts across the index</returns>
    public async Task<LibraryCounts> GetCountsAsync(CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM artists),
                   (SELECT COUNT(*) FROM albums),
                   (SELECT COUNT(*) FROM albums WHERE is_saved = 1),
                   (SELECT COUNT(*) FROM tracks),
                   (SELECT COUNT(*) FROM tracks WHERE is_liked = 1),
                   (SELECT COUNT(*) FROM playlists),
                   (SELECT COUNT(*) FROM playlists WHERE snapshot_id <> ''),
                   (SELECT COUNT(*) FROM playlist_tracks),
                   (SELECT COUNT(*) FROM followed_artists);
            """;

        await using var reader = await command.ExecuteReaderAsync(cancel).ConfigureAwait(false);
        await reader.ReadAsync(cancel).ConfigureAwait(false);

        return new LibraryCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8));
    }

    /// <summary>Reads a value from the sync bookkeeping table.</summary>
    /// <param name="key">The bookkeeping key</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>The stored value, or null when it's unset</returns>
    public async Task<string?> GetSyncStateAsync(string key, CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM sync_state WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);

        var result = await command.ExecuteScalarAsync(cancel).ConfigureAwait(false);
        return result is string value ? value : null;
    }

    /// <summary>Writes a value into the sync bookkeeping table.</summary>
    /// <param name="key">The bookkeeping key</param>
    /// <param name="value">The value to store</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task SetSyncStateAsync(string key, string value, CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_state (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);

        await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }
}
