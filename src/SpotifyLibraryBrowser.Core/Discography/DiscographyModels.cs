using SpotifyLibraryBrowser.Core.Model;

namespace SpotifyLibraryBrowser.Core.Discography;

/// <summary>
/// Which kinds of release a discography covers.
/// </summary>
/// <remarks>
/// This is a request filter rather than something read back off each album. Spotify deprecated
/// both <c>album_type</c> and <c>album_group</c>, so what an album *is* can no longer be read from
/// the response — but what to *ask for* still can, which is the durable way to offer the choice.
/// </remarks>
[Flags]
public enum DiscographyGroups
{
    /// <summary>Full-length releases.</summary>
    Albums = 1,

    /// <summary>Singles and EPs.</summary>
    Singles = 2,

    /// <summary>Compilations.</summary>
    Compilations = 4
}

/// <summary>How a discography is ordered.</summary>
/// <remarks>Every option sorts on data that's current in the API, so none of them can rot.</remarks>
public enum DiscographySort
{
    /// <summary>Newest release first.</summary>
    NewestFirst = 0,

    /// <summary>Oldest release first, which reads as a career in order.</summary>
    OldestFirst = 1,

    /// <summary>Alphabetical by title.</summary>
    Name = 2,

    /// <summary>Most tracks first, which floats the long players above the singles.</summary>
    TrackCount = 3,

    /// <summary>What's already in the library first, then newest.</summary>
    InLibraryFirst = 4
}

/// <summary>One release in an artist's discography, and whether the library already has it.</summary>
/// <param name="Id">The Spotify album ID</param>
/// <param name="Name">The album title</param>
/// <param name="ReleaseDate">The raw release date string as Spotify returned it</param>
/// <param name="ReleasePrecision">How much of the release date is meaningful</param>
/// <param name="ImageUrl">The album artwork</param>
/// <param name="TotalTracks">How many tracks it holds</param>
/// <param name="IsSaved">Whether the album is in the user's library</param>
/// <param name="TracksHeld">How many of its tracks the local index already has</param>
public sealed record DiscographyAlbum(
    string Id,
    string Name,
    string? ReleaseDate,
    ReleasePrecision ReleasePrecision,
    string? ImageUrl,
    int TotalTracks,
    bool IsSaved,
    int TracksHeld)
{
    /// <summary>The Spotify URI, for playing it or saving it.</summary>
    public string Uri => $"spotify:album:{Id}";

    /// <summary>The four-digit release year, or null when there's no usable date.</summary>
    public int? Year =>
        ReleaseDate is { Length: >= 4 } && int.TryParse(ReleaseDate.AsSpan(0, 4), out var year)
            ? year
            : null;

    /// <summary>
    /// Whether the library holds anything off this album without the album itself being saved.
    /// </summary>
    /// <remarks>This is the "you've liked three tracks off this, want the record?" case.</remarks>
    public bool IsPartiallyHeld => !IsSaved && TracksHeld > 0;
}
