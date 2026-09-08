using Microsoft.Data.Sqlite;
using SpotifyLibraryBrowser.Core.Model;

namespace SpotifyLibraryBrowser.Core.Data;

/// <summary>
/// A batched, transactional writer for sync results.
/// </summary>
/// <remarks>
/// A full sync writes thousands of rows, so the commands are prepared once and re-executed with
/// fresh parameter values rather than rebuilt per row, and everything runs inside one transaction.
/// Nothing is persisted until <see cref="CommitAsync"/> — disposing without committing rolls back.
/// </remarks>
public sealed class SyncWriter : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqliteCommand _artist;
    private readonly SqliteCommand _album;
    private readonly SqliteCommand _albumArtist;
    private readonly SqliteCommand _track;
    private readonly SqliteCommand _trackArtist;
    private readonly SqliteCommand _playlist;
    private readonly SqliteCommand _playlistTrack;
    private readonly SqliteCommand _followed;
    private bool _committed;

    /// <summary>Creates a writer over an open connection and transaction.</summary>
    /// <param name="connection">The open connection</param>
    /// <param name="transaction">The transaction all writes join</param>
    internal SyncWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;

        // An embedded artist carries no image, so an existing one is kept rather than nulled.
        _artist = Prepare("""
            INSERT INTO artists (id, name, image_url) VALUES (@id, @name, @image)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                image_url = COALESCE(excluded.image_url, artists.image_url);
            """);

        // is_saved maxes rather than overwrites: a saved album is often seen again as the album of
        // a liked song, and that second sighting mustn't demote it.
        _album = Prepare("""
            INSERT INTO albums (id, name, release_date, release_precision, image_url, total_tracks,
                                added_at, is_saved)
            VALUES (@id, @name, @release, @precision, @image, @total, @added, @saved)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                release_date = excluded.release_date,
                release_precision = excluded.release_precision,
                image_url = COALESCE(excluded.image_url, albums.image_url),
                total_tracks = excluded.total_tracks,
                added_at = COALESCE(excluded.added_at, albums.added_at),
                is_saved = MAX(albums.is_saved, excluded.is_saved);
            """);

        _albumArtist = Prepare("""
            INSERT INTO album_artists (album_id, artist_id, position) VALUES (@album, @artist, @position)
            ON CONFLICT(album_id, artist_id) DO UPDATE SET position = excluded.position;
            """);

        // source_mask ORs and is_liked maxes, so a track found by several crawls accumulates
        // rather than the last crawl winning.
        _track = Prepare("""
            INSERT INTO tracks (id, name, album_id, disc_number, track_number, duration_ms,
                                explicit, uri, added_at, source_mask, is_liked, liked_at)
            VALUES (@id, @name, @album, @disc, @number, @duration, @explicit, @uri, @added,
                    @source, @liked, @likedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                album_id = excluded.album_id,
                disc_number = excluded.disc_number,
                track_number = excluded.track_number,
                duration_ms = excluded.duration_ms,
                explicit = excluded.explicit,
                uri = excluded.uri,
                added_at = COALESCE(excluded.added_at, tracks.added_at),
                source_mask = tracks.source_mask | excluded.source_mask,
                is_liked = MAX(tracks.is_liked, excluded.is_liked),
                liked_at = COALESCE(excluded.liked_at, tracks.liked_at);
            """);

        _trackArtist = Prepare("""
            INSERT INTO track_artists (track_id, artist_id, position) VALUES (@track, @artist, @position)
            ON CONFLICT(track_id, artist_id) DO UPDATE SET position = excluded.position;
            """);

        _playlist = Prepare("""
            INSERT INTO playlists (id, name, owner_id, snapshot_id, is_owned)
            VALUES (@id, @name, @owner, @snapshot, @owned)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                owner_id = excluded.owner_id,
                snapshot_id = excluded.snapshot_id,
                is_owned = excluded.is_owned;
            """);

        _playlistTrack = Prepare("""
            INSERT INTO playlist_tracks (playlist_id, track_id, position, added_at)
            VALUES (@playlist, @track, @position, @added)
            ON CONFLICT(playlist_id, track_id, position) DO UPDATE SET added_at = excluded.added_at;
            """);

        _followed = Prepare("INSERT OR IGNORE INTO followed_artists (artist_id) VALUES (@artist);");
    }

    /// <summary>Writes an artist, keeping any image already held.</summary>
    /// <param name="artist">The artist to write</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task UpsertArtistAsync(Artist artist, CancellationToken cancel = default)
    {
        Bind(_artist, ("@id", artist.Id), ("@name", artist.Name), ("@image", artist.ImageUrl));
        await _artist.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Writes an album and its credited album artists.</summary>
    /// <param name="album">The album to write</param>
    /// <param name="artists">The album's credited artists, in credit order</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task UpsertAlbumAsync(
        Album album,
        IReadOnlyList<Artist> artists,
        CancellationToken cancel = default)
    {
        Bind(_album,
            ("@id", album.Id),
            ("@name", album.Name),
            ("@release", album.ReleaseDate),
            ("@precision", (int)album.ReleasePrecision),
            ("@image", album.ImageUrl),
            ("@total", album.TotalTracks),
            ("@added", Format(album.AddedAt)),
            ("@saved", album.IsSaved ? 1 : 0));

        await _album.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);

        for (var i = 0; i < artists.Count; i++)
        {
            await UpsertArtistAsync(artists[i], cancel).ConfigureAwait(false);

            Bind(_albumArtist, ("@album", album.Id), ("@artist", artists[i].Id), ("@position", i));
            await _albumArtist.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }
    }

    /// <summary>Writes a track and its credited artists.</summary>
    /// <param name="track">The track to write</param>
    /// <param name="artists">The track's credited artists, in credit order</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task UpsertTrackAsync(
        Track track,
        IReadOnlyList<Artist> artists,
        CancellationToken cancel = default)
    {
        Bind(_track,
            ("@id", track.Id),
            ("@name", track.Name),
            ("@album", track.AlbumId),
            ("@disc", track.DiscNumber),
            ("@number", track.TrackNumber),
            ("@duration", track.DurationMs),
            ("@explicit", track.Explicit ? 1 : 0),
            ("@uri", track.Uri),
            ("@added", Format(track.AddedAt)),
            ("@source", (int)track.Source),
            ("@liked", track.IsLiked ? 1 : 0),
            ("@likedAt", track.IsLiked ? Format(track.AddedAt) : null));

        await _track.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);

        for (var i = 0; i < artists.Count; i++)
        {
            await UpsertArtistAsync(artists[i], cancel).ConfigureAwait(false);

            Bind(_trackArtist, ("@track", track.Id), ("@artist", artists[i].Id), ("@position", i));
            await _trackArtist.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }
    }

    /// <summary>Writes a playlist's own row, without touching its contents.</summary>
    /// <param name="playlist">The playlist to write</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task UpsertPlaylistAsync(PlaylistRef playlist, CancellationToken cancel = default)
    {
        Bind(_playlist,
            ("@id", playlist.Id),
            ("@name", playlist.Name),
            ("@owner", playlist.OwnerId),
            ("@snapshot", playlist.SnapshotId),
            ("@owned", playlist.IsOwned ? 1 : 0));

        await _playlist.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Places a track at a position within a playlist.</summary>
    /// <param name="playlistId">The playlist's ID</param>
    /// <param name="trackId">The track's ID</param>
    /// <param name="position">The track's position in the playlist</param>
    /// <param name="addedAt">When the track was added to the playlist</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task AddPlaylistTrackAsync(
        string playlistId,
        string trackId,
        int position,
        DateTimeOffset? addedAt,
        CancellationToken cancel = default)
    {
        Bind(_playlistTrack,
            ("@playlist", playlistId),
            ("@track", trackId),
            ("@position", position),
            ("@added", Format(addedAt)));

        await _playlistTrack.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Records that the user follows an artist.</summary>
    /// <param name="artistId">The followed artist's ID</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task AddFollowedArtistAsync(string artistId, CancellationToken cancel = default)
    {
        Bind(_followed, ("@artist", artistId));
        await _followed.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Clears a playlist's contents ahead of re-reading it.</summary>
    /// <param name="playlistId">The playlist whose contents to drop</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task ClearPlaylistTracksAsync(string playlistId, CancellationToken cancel = default)
    {
        await using var command = Prepare("DELETE FROM playlist_tracks WHERE playlist_id = @id;");
        Bind(command, ("@id", playlistId));
        await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Commits everything written through this writer.</summary>
    /// <param name="cancel">Cancels the commit</param>
    public async Task CommitAsync(CancellationToken cancel = default)
    {
        await _transaction.CommitAsync(cancel).ConfigureAwait(false);
        _committed = true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_committed) await _transaction.RollbackAsync().ConfigureAwait(false);

        _artist.Dispose();
        _album.Dispose();
        _albumArtist.Dispose();
        _track.Dispose();
        _trackArtist.Dispose();
        _playlist.Dispose();
        _playlistTrack.Dispose();
        _followed.Dispose();

        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Creates a command enlisted in this writer's transaction.</summary>
    /// <param name="sql">The statement text</param>
    /// <returns>The prepared command</returns>
    private SqliteCommand Prepare(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        return command;
    }

    /// <summary>Rebinds a prepared command's parameters for the next execution.</summary>
    /// <param name="command">The command to rebind</param>
    /// <param name="values">The parameter names and their values</param>
    private static void Bind(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        command.Parameters.Clear();

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    /// <summary>Formats a timestamp for storage, as round-trippable text.</summary>
    /// <param name="value">The timestamp to format</param>
    /// <returns>An ISO-8601 string, or null when there's no timestamp</returns>
    private static string? Format(DateTimeOffset? value) => value?.ToString("O");
}
