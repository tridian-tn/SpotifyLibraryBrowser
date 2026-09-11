using SpotifyAPI.Web;
using SpotifyLibraryBrowser.Core.Api;
using SpotifyLibraryBrowser.Core.Data;
using SpotifyLibraryBrowser.Core.Model;

namespace SpotifyLibraryBrowser.Core.Discography;

/// <summary>
/// Fetches an artist's releases from Spotify, on demand and cached.
/// </summary>
/// <remarks>
/// Deliberately not part of the sync. Pulling every artist's discography up front would turn a
/// first run into thousands of requests for data almost none of which gets looked at, so this
/// fetches one artist at a time, when the panel asks, and keeps the answer.
/// </remarks>
/// <param name="artists">The artist endpoints, taken narrowly so this is testable with a stub</param>
/// <param name="repository">Where fetched discographies are kept</param>
/// <param name="throttle">The shared rate gate</param>
public sealed class DiscographyService(
    IArtistsClient artists,
    DiscographyRepository repository,
    RequestThrottle throttle)
{
    /// <summary>How long a cached discography is trusted before it's fetched again.</summary>
    /// <remarks>An artist releasing something is the only thing that changes it, so this is days.</remarks>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// How many releases to ask for per page.
    /// </summary>
    /// <remarks>
    /// Ten, not the fifty both the library's doc comment and the reference page still claim. The
    /// API answers anything above ten with "Invalid limit" — the same reduction that took search
    /// down to ten. A prolific artist is therefore several requests, which is affordable precisely
    /// because this runs on demand for one artist rather than across the whole library.
    /// </remarks>
    private const int PageSize = 10;

    /// <summary>
    /// Gets an artist's releases, fetching them if they aren't cached.
    /// </summary>
    /// <param name="artistId">The artist to look up</param>
    /// <param name="groups">Which kinds of release to include</param>
    /// <param name="sort">The order to return them in</param>
    /// <param name="refresh">Fetch again even when a usable cache exists</param>
    /// <param name="cancel">Cancels the work</param>
    /// <returns>The artist's releases, sorted and marked with what the library holds</returns>
    public async Task<IReadOnlyList<DiscographyAlbum>> GetAsync(
        string artistId,
        DiscographyGroups groups,
        DiscographySort sort,
        bool refresh = false,
        CancellationToken cancel = default)
    {
        var cached = !refresh && await repository
            .IsCachedAsync(artistId, groups, CacheLifetime, cancel)
            .ConfigureAwait(false);

        if (!cached)
        {
            var fetched = await FetchAsync(artistId, groups, cancel).ConfigureAwait(false);
            await repository.SaveAsync(artistId, groups, fetched, cancel).ConfigureAwait(false);
        }

        // Read back rather than returning what was fetched: the stored rows come back joined to
        // the library, which is the part that makes the list worth looking at.
        return await repository.GetAsync(artistId, sort, cancel).ConfigureAwait(false);
    }

    /// <summary>Walks the artist's albums from Spotify.</summary>
    /// <param name="artistId">The artist to fetch</param>
    /// <param name="groups">Which kinds of release to ask for</param>
    /// <param name="cancel">Cancels the fetch</param>
    /// <returns>The releases Spotify listed</returns>
    private async Task<IReadOnlyList<DiscographyAlbum>> FetchAsync(
        string artistId,
        DiscographyGroups groups,
        CancellationToken cancel)
    {
        var albums = new List<DiscographyAlbum>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;

        while (true)
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            var request = new ArtistsAlbumsRequest
            {
                Limit = PageSize,
                Offset = offset,
                IncludeGroupsParam = ToIncludeGroups(groups)
            };

            var page = await artists.GetAlbums(artistId, request, cancel).ConfigureAwait(false);
            if (page.Items is not { Count: > 0 }) break;

            foreach (var album in page.Items)
            {
                // Spotify lists regional variants of the same record separately often enough that
                // a discography reads as duplicates without this.
                if (string.IsNullOrEmpty(album.Id) || !seen.Add(album.Id)) continue;

                albums.Add(new DiscographyAlbum(
                    album.Id,
                    album.Name,
                    album.ReleaseDate,
                    ParsePrecision(album.ReleaseDatePrecision),
                    album.Images?.FirstOrDefault()?.Url,
                    album.TotalTracks,
                    IsSaved: false,
                    TracksHeld: 0));
            }

            offset += page.Items.Count;
            if (offset >= page.Total) break;
        }

        return albums;
    }

    /// <summary>Maps the app's group flags onto the request's.</summary>
    /// <param name="groups">The groups wanted</param>
    /// <returns>The library's equivalent flags</returns>
    private static ArtistsAlbumsRequest.IncludeGroups ToIncludeGroups(DiscographyGroups groups)
    {
        var mapped = default(ArtistsAlbumsRequest.IncludeGroups);

        if (groups.HasFlag(DiscographyGroups.Albums)) mapped |= ArtistsAlbumsRequest.IncludeGroups.Album;
        if (groups.HasFlag(DiscographyGroups.Singles)) mapped |= ArtistsAlbumsRequest.IncludeGroups.Single;
        if (groups.HasFlag(DiscographyGroups.Compilations)) mapped |= ArtistsAlbumsRequest.IncludeGroups.Compilation;

        // Asking for nothing would return everything, including the appears-on records that bury
        // an artist's own work under every compilation they were ever sampled on.
        return mapped == default ? ArtistsAlbumsRequest.IncludeGroups.Album : mapped;
    }

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
}
