using SpotifyAPI.Web;
using SpotifyLibraryBrowser.Core.Api;
using SpotifyLibraryBrowser.Core.Data;

namespace SpotifyLibraryBrowser.Core.Library;

/// <summary>
/// The app's only write path into the user's Spotify account: liking and unliking tracks.
/// </summary>
/// <remarks>
/// February 2026 replaced the per-entity save endpoints with URI-based ones, so a whole
/// multi-selection goes in a single call rather than one per track. Local state is written first
/// and rolled back if the API refuses, because a list that silently disagrees with Spotify is
/// worse than an error.
/// </remarks>
/// <param name="library">The library endpoints, taken narrowly so this is testable with a stub</param>
/// <param name="repository">The local index</param>
/// <param name="throttle">The shared rate gate</param>
public sealed class LikeService(
    ILibraryClient library,
    LibraryRepository repository,
    RequestThrottle throttle)
{
    /// <summary>The API caps a library write at this many URIs, so bigger selections are chunked.</summary>
    private const int MaxUrisPerRequest = 50;

    /// <summary>
    /// Likes or unlikes a set of tracks, keeping the local index and Spotify in step.
    /// </summary>
    /// <param name="trackIds">The tracks to change</param>
    /// <param name="liked">Whether they should end up liked</param>
    /// <param name="cancel">Cancels the change</param>
    /// <exception cref="APIException">The change was rejected; local state has been rolled back</exception>
    public async Task SetLikedAsync(
        IReadOnlyCollection<string> trackIds,
        bool liked,
        CancellationToken cancel = default)
    {
        if (trackIds.Count == 0) return;

        await repository.SetLikedAsync(trackIds, liked, cancel).ConfigureAwait(false);

        try
        {
            foreach (var chunk in Chunk(trackIds))
            {
                var uris = chunk.Select(ToUri).ToList();
                await throttle.WaitAsync(cancel).ConfigureAwait(false);

                if (liked)
                {
                    await library.SaveItems(new LibrarySaveItemsRequest(uris), cancel)
                        .ConfigureAwait(false);
                }
                else
                {
                    await library.RemoveItems(new LibraryRemoveItemsRequest(uris), cancel)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Put the index back the way it was, then let the caller undo the UI.
            await repository.SetLikedAsync(trackIds, !liked, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Re-reads liked state for a handful of tracks and corrects the index.
    /// </summary>
    /// <remarks>
    /// Liked state is otherwise only as fresh as the last sync, so liking something on another
    /// device would leave this stale. Callers pass just the rows on screen to keep it to one call.
    /// </remarks>
    /// <param name="trackIds">The tracks to check, ideally no more than one request's worth</param>
    /// <param name="cancel">Cancels the check</param>
    /// <returns>
    /// Spotify's liked state for every track checked, not only the ones that had drifted. Callers
    /// apply the lot rather than having to work out which rows changed
    /// </returns>
    public async Task<IReadOnlyDictionary<string, bool>> ReconcileAsync(
        IReadOnlyList<string> trackIds,
        CancellationToken cancel = default)
    {
        var states = new Dictionary<string, bool>();
        if (trackIds.Count == 0) return states;

        var actual = new Dictionary<string, bool>();

        foreach (var chunk in Chunk(trackIds))
        {
            var ids = chunk.ToList();
            var uris = ids.Select(ToUri).ToList();

            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            var results = await library
                .CheckItems(new LibraryCheckItemsRequest(uris), cancel)
                .ConfigureAwait(false);

            for (var i = 0; i < ids.Count && i < results.Count; i++)
            {
                actual[ids[i]] = results[i];
            }
        }

        var nowLiked = actual.Where(p => p.Value).Select(p => p.Key).ToList();
        var nowUnliked = actual.Where(p => !p.Value).Select(p => p.Key).ToList();

        if (nowLiked.Count > 0) await repository.SetLikedAsync(nowLiked, true, cancel).ConfigureAwait(false);
        if (nowUnliked.Count > 0) await repository.SetLikedAsync(nowUnliked, false, cancel).ConfigureAwait(false);

        foreach (var (id, isLiked) in actual)
        {
            states[id] = isLiked;
        }

        return states;
    }

    /// <summary>Splits an ID set into request-sized chunks.</summary>
    /// <param name="ids">The IDs to split</param>
    /// <returns>Chunks no larger than the API allows</returns>
    private static IEnumerable<IEnumerable<string>> Chunk(IEnumerable<string> ids) =>
        ids.Chunk(MaxUrisPerRequest);

    /// <summary>Turns a track ID into the URI the library endpoints expect.</summary>
    /// <param name="trackId">The track ID</param>
    /// <returns>The track's Spotify URI</returns>
    private static string ToUri(string trackId) =>
        trackId.StartsWith("spotify:", StringComparison.Ordinal) ? trackId : $"spotify:track:{trackId}";
}
