namespace SpotifyLibraryBrowser.Core.Model;

/// <summary>
/// How precise an album's release date is. Spotify returns year-only precision for
/// plenty of older records, so you can't assume a full parseable date.
/// </summary>
public enum ReleasePrecision
{
    Unknown = 0,
    Year = 1,
    Month = 2,
    Day = 3
}

/// <summary>
/// Why a track is present in the local index. Liked status isn't part of this — it's
/// mutable from inside the app, so it lives in its own column.
/// </summary>
[Flags]
public enum TrackSource
{
    None = 0,
    SavedAlbum = 1,
    Playlist = 2
}

/// <summary>An artist, as it arrives embedded in album, track and followed-artist responses.</summary>
/// <param name="Id">The Spotify artist ID</param>
/// <param name="Name">The artist's display name</param>
/// <param name="ImageUrl">Cover image, only populated where it arrives without a dedicated fetch</param>
public sealed record Artist(string Id, string Name, string? ImageUrl = null);

/// <summary>An album, carrying the release data that drives the Year and Decade columns.</summary>
/// <param name="Id">The Spotify album ID</param>
/// <param name="Name">The album title</param>
/// <param name="ReleaseDate">The raw release date string as Spotify returned it</param>
/// <param name="ReleasePrecision">How much of <paramref name="ReleaseDate"/> is meaningful</param>
/// <param name="ImageUrl">The album artwork URL</param>
/// <param name="TotalTracks">Total track count, used to spot albums needing a follow-up track fetch</param>
/// <param name="AddedAt">When the album was saved to the library, if it was</param>
/// <param name="IsSaved">
/// Whether the album itself is in the library, as opposed to merely being the album a liked song
/// or playlist track happens to belong to
/// </param>
public sealed record Album(
    string Id,
    string Name,
    string? ReleaseDate,
    ReleasePrecision ReleasePrecision,
    string? ImageUrl,
    int TotalTracks,
    DateTimeOffset? AddedAt,
    bool IsSaved = false)
{
    /// <summary>The four-digit release year, or null when the date is missing or unparseable.</summary>
    public int? Year =>
        ReleaseDate is { Length: >= 4 } && int.TryParse(ReleaseDate.AsSpan(0, 4), out var year)
            ? year
            : null;
}

/// <summary>A track in the local index.</summary>
/// <param name="Id">The Spotify track ID</param>
/// <param name="Name">The track title</param>
/// <param name="AlbumId">The album this track belongs to</param>
/// <param name="DiscNumber">The disc number within the album</param>
/// <param name="TrackNumber">The track's position on its disc</param>
/// <param name="DurationMs">Track length in milliseconds</param>
/// <param name="Explicit">Whether the track is flagged explicit</param>
/// <param name="Uri">The Spotify URI, used for playback and library writes</param>
/// <param name="AddedAt">When the track entered the library, if known</param>
/// <param name="Source">Why the track is indexed</param>
/// <param name="IsLiked">Whether the track is in Liked Songs</param>
public sealed record Track(
    string Id,
    string Name,
    string AlbumId,
    int DiscNumber,
    int TrackNumber,
    int DurationMs,
    bool Explicit,
    string Uri,
    DateTimeOffset? AddedAt,
    TrackSource Source,
    bool IsLiked);

/// <summary>A playlist reference. Contents are only readable when owned or collaborative.</summary>
/// <param name="Id">The Spotify playlist ID</param>
/// <param name="Name">The playlist name</param>
/// <param name="OwnerId">The owning user's ID</param>
/// <param name="SnapshotId">Spotify's version marker, used to skip unchanged playlists on re-sync</param>
/// <param name="IsOwned">Whether the current user owns or collaborates on it</param>
public sealed record PlaylistRef(
    string Id,
    string Name,
    string OwnerId,
    string SnapshotId,
    bool IsOwned);
