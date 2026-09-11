using SpotifyLibraryBrowser.Core.Data;
using SpotifyLibraryBrowser.Core.Model;

namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// A small hand-built library covering the cases the browse engine has to get right: a normal
/// album, a compilation whose track artists differ from its album artist, a collaboration credited
/// to two artists, year-only and missing release dates, playlist membership, and liked tracks.
/// </summary>
public sealed class LibraryFixture : IAsyncDisposable
{
    private LibraryDatabase _database = null!;

    public BrowseRepository Browse { get; private set; } = null!;

    public LibraryRepository Library { get; private set; } = null!;

    public DiscographyRepository Discography { get; private set; } = null!;

    public static readonly Artist Miles = new("ar-miles", "Miles Davis");
    public static readonly Artist Coltrane = new("ar-coltrane", "John Coltrane");
    public static readonly Artist Various = new("ar-various", "Various Artists");
    public static readonly Artist Bowie = new("ar-bowie", "David Bowie");
    public static readonly Artist Eno = new("ar-eno", "Brian Eno");

    /// <summary>Known only through a liked song, never through a saved album.</summary>
    public static readonly Artist Aphex = new("ar-aphex", "Aphex Twin");

    /// <summary>Known only through a playlist track.</summary>
    public static readonly Artist Boards = new("ar-boards", "Boards of Canada");

    /// <summary>Builds and seeds the fixture.</summary>
    /// <returns>A ready fixture</returns>
    public static async Task<LibraryFixture> CreateAsync()
    {
        var fixture = new LibraryFixture();
        fixture._database = await LibraryDatabase.OpenInMemoryAsync();
        fixture.Browse = new BrowseRepository(fixture._database);
        fixture.Library = new LibraryRepository(fixture._database);
        fixture.Discography = new DiscographyRepository(fixture._database);

        await fixture.SeedAsync();
        return fixture;
    }

    /// <summary>Writes the fixture data.</summary>
    private async Task SeedAsync()
    {
        await using var writer = await Library.BeginWriteAsync();

        // A straightforward saved album: album artist and track artist agree, full release date.
        var kindOfBlue = new Album("al-kob", "Kind of Blue", "1959-08-17", ReleasePrecision.Day,
            null, 2, DateTimeOffset.Parse("2024-01-10T00:00:00Z"), IsSaved: true);

        await writer.UpsertAlbumAsync(kindOfBlue, [Miles]);
        await writer.UpsertTrackAsync(
            Track("tr-sowhat", "So What", kindOfBlue.Id, 1, TrackSource.SavedAlbum, liked: true), [Miles]);
        await writer.UpsertTrackAsync(
            Track("tr-blue", "Blue in Green", kindOfBlue.Id, 2, TrackSource.SavedAlbum), [Miles, Coltrane]);

        // A compilation: the album artist is "Various Artists" but the tracks are by other people.
        // Grouping by Album Artist must not scatter these across the real artists.
        var compilation = new Album("al-comp", "Jazz Collected", "1998", ReleasePrecision.Year,
            null, 2, DateTimeOffset.Parse("2024-02-01T00:00:00Z"), IsSaved: true);

        await writer.UpsertAlbumAsync(compilation, [Various]);
        await writer.UpsertTrackAsync(
            Track("tr-comp1", "Naima", compilation.Id, 1, TrackSource.SavedAlbum), [Coltrane]);
        await writer.UpsertTrackAsync(
            Track("tr-comp2", "Milestones", compilation.Id, 2, TrackSource.SavedAlbum), [Miles]);

        // Year-only precision, and a collaboration credited to two artists.
        var lowAlbum = new Album("al-low", "Low", "1977", ReleasePrecision.Year,
            null, 1, DateTimeOffset.Parse("2024-03-01T00:00:00Z"), IsSaved: true);

        await writer.UpsertAlbumAsync(lowAlbum, [Bowie]);
        await writer.UpsertTrackAsync(
            Track("tr-warsaw", "Warszawa", lowAlbum.Id, 1, TrackSource.SavedAlbum, liked: true), [Bowie, Eno]);

        // No release date at all — this must land in the "unknown" bucket, not vanish.
        var undated = new Album("al-undated", "Unmarked Tape", null, ReleasePrecision.Unknown,
            null, 1, null);

        await writer.UpsertAlbumAsync(undated, [Bowie]);
        await writer.UpsertTrackAsync(
            Track("tr-undated", "Untitled", undated.Id, 1, TrackSource.None, liked: true), [Bowie]);

        // A liked song whose album was never saved. Its album and album artist must stay out of
        // the Album and Album Artist columns, while remaining reachable by track artist.
        var ambient = new Album("al-saw", "Selected Ambient Works", "1992", ReleasePrecision.Year,
            null, 1, null, IsSaved: false);

        await writer.UpsertAlbumAsync(ambient, [Aphex]);
        await writer.UpsertTrackAsync(
            Track("tr-xtal", "Xtal", ambient.Id, 1, TrackSource.None, liked: true), [Aphex]);

        // Likewise for a track that only exists here because a playlist contains it.
        var geogaddi = new Album("al-geo", "Geogaddi", "2002", ReleasePrecision.Year,
            null, 1, null, IsSaved: false);

        await writer.UpsertAlbumAsync(geogaddi, [Boards]);
        await writer.UpsertTrackAsync(
            Track("tr-roygbiv", "Roygbiv", geogaddi.Id, 1, TrackSource.Playlist), [Boards]);

        // A playlist drawing tracks from across those albums.
        var playlist = new PlaylistRef("pl-1", "Late Night", "user-1", "snap-1", true);
        await writer.UpsertPlaylistAsync(playlist);
        await writer.AddPlaylistTrackAsync(playlist.Id, "tr-sowhat", 0, null);
        await writer.AddPlaylistTrackAsync(playlist.Id, "tr-warsaw", 1, null);
        await writer.AddPlaylistTrackAsync(playlist.Id, "tr-roygbiv", 2, null);

        // A playlist Spotify served that simply has nothing in it, and one whose read was refused
        // (recorded by the sync as an empty snapshot). Only the first counts as readable.
        await writer.UpsertPlaylistAsync(new PlaylistRef("pl-empty", "Nothing Here", "user-1", "snap-2", true));
        await writer.UpsertPlaylistAsync(new PlaylistRef("pl-refused", "Someone Else's", "other", string.Empty, false));

        await writer.AddFollowedArtistAsync(Miles.Id);

        await writer.CommitAsync();
    }

    /// <summary>Builds a track with the fixture's defaults.</summary>
    /// <param name="id">The track ID</param>
    /// <param name="name">The track title</param>
    /// <param name="albumId">The owning album</param>
    /// <param name="number">The track number</param>
    /// <param name="source">Why the track is indexed</param>
    /// <param name="liked">Whether it's in Liked Songs</param>
    /// <returns>The track</returns>
    private static Track Track(
        string id,
        string name,
        string albumId,
        int number,
        TrackSource source,
        bool liked = false) =>
        new(id, name, albumId, 1, number, 300_000, false, $"spotify:track:{id}",
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"), source, liked);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }
}
