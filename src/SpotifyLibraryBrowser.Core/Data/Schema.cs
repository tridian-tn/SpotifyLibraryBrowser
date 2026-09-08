namespace SpotifyLibraryBrowser.Core.Data;

/// <summary>The local index's schema, applied on every open so a fresh database self-creates.</summary>
internal static class Schema
{
    /// <summary>
    /// Every DDL statement, idempotent. Track rows carry both why they're indexed
    /// (<c>source_mask</c>) and whether they're liked (<c>is_liked</c>) — the two are separate
    /// because liking is mutable from inside the app.
    /// </summary>
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS artists (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            image_url   TEXT
        );

        -- is_saved marks albums the user actually saved, as opposed to the albums that merely
        -- turn up because a liked song or playlist track belongs to them. The Album and Album
        -- Artist columns browse the former only.
        CREATE TABLE IF NOT EXISTS albums (
            id                TEXT PRIMARY KEY,
            name              TEXT NOT NULL,
            release_date      TEXT,
            release_precision INTEGER NOT NULL DEFAULT 0,
            image_url         TEXT,
            total_tracks      INTEGER NOT NULL DEFAULT 0,
            added_at          TEXT,
            is_saved          INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS album_artists (
            album_id  TEXT NOT NULL REFERENCES albums(id) ON DELETE CASCADE,
            artist_id TEXT NOT NULL REFERENCES artists(id) ON DELETE CASCADE,
            position  INTEGER NOT NULL,
            PRIMARY KEY (album_id, artist_id)
        );

        CREATE TABLE IF NOT EXISTS tracks (
            id           TEXT PRIMARY KEY,
            name         TEXT NOT NULL,
            album_id     TEXT NOT NULL REFERENCES albums(id) ON DELETE CASCADE,
            disc_number  INTEGER NOT NULL DEFAULT 1,
            track_number INTEGER NOT NULL DEFAULT 0,
            duration_ms  INTEGER NOT NULL DEFAULT 0,
            explicit     INTEGER NOT NULL DEFAULT 0,
            uri          TEXT NOT NULL,
            added_at     TEXT,
            source_mask  INTEGER NOT NULL DEFAULT 0,
            is_liked     INTEGER NOT NULL DEFAULT 0,
            liked_at     TEXT
        );

        CREATE TABLE IF NOT EXISTS track_artists (
            track_id  TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            artist_id TEXT NOT NULL REFERENCES artists(id) ON DELETE CASCADE,
            position  INTEGER NOT NULL,
            PRIMARY KEY (track_id, artist_id)
        );

        CREATE TABLE IF NOT EXISTS playlists (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            owner_id    TEXT NOT NULL,
            snapshot_id TEXT NOT NULL,
            is_owned    INTEGER NOT NULL DEFAULT 0
        );

        -- Position is part of the key: a track can legitimately appear twice in one playlist.
        CREATE TABLE IF NOT EXISTS playlist_tracks (
            playlist_id TEXT NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            track_id    TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            position    INTEGER NOT NULL,
            added_at    TEXT,
            PRIMARY KEY (playlist_id, track_id, position)
        );

        CREATE TABLE IF NOT EXISTS followed_artists (
            artist_id TEXT PRIMARY KEY REFERENCES artists(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS sync_state (
            key   TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_tracks_album          ON tracks(album_id);
        CREATE INDEX IF NOT EXISTS ix_tracks_liked          ON tracks(is_liked);
        CREATE INDEX IF NOT EXISTS ix_tracks_name           ON tracks(name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_track_artists_artist  ON track_artists(artist_id);
        CREATE INDEX IF NOT EXISTS ix_track_artists_track   ON track_artists(track_id);
        CREATE INDEX IF NOT EXISTS ix_album_artists_artist  ON album_artists(artist_id);
        CREATE INDEX IF NOT EXISTS ix_album_artists_album   ON album_artists(album_id);
        CREATE INDEX IF NOT EXISTS ix_playlist_tracks_track ON playlist_tracks(track_id);
        CREATE INDEX IF NOT EXISTS ix_playlist_tracks_list  ON playlist_tracks(playlist_id);
        CREATE INDEX IF NOT EXISTS ix_albums_release        ON albums(release_date);
        CREATE INDEX IF NOT EXISTS ix_albums_name           ON albums(name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_albums_saved          ON albums(is_saved);
        CREATE INDEX IF NOT EXISTS ix_artists_name          ON artists(name COLLATE NOCASE);
        """;

    /// <summary>
    /// Adds <c>is_saved</c> to an index created before the column existed, and backfills it.
    /// </summary>
    /// <remarks>
    /// <c>CREATE TABLE IF NOT EXISTS</c> silently leaves an existing table alone, so a database
    /// from an earlier build needs the column added explicitly. Only albums the saved-albums crawl
    /// wrote ever carry an <c>added_at</c>, so that's a reliable backfill.
    /// </remarks>
    public const string AddIsSavedToAlbums = """
        ALTER TABLE albums ADD COLUMN is_saved INTEGER NOT NULL DEFAULT 0;
        UPDATE albums SET is_saved = 1 WHERE added_at IS NOT NULL;
        """;
}
