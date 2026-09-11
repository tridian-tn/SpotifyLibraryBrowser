using SpotifyAPI.Web;
using SpotifyLibraryBrowser.Core.Api;
using SpotifyLibraryBrowser.Core.Data;

namespace SpotifyLibraryBrowser.Core.Library;

/// <summary>
/// The app's write path into the user's Spotify library: liking tracks, and saving albums.
/// </summary>
/// <remarks>
/// February 2026 replaced the per-entity save endpoints with URI-based ones, so tracks and albums
/// go through the same call and a whole multi-selection fits in a single request. Local state is
/// written first and rolled back if the API refuses, because a list that silently disagrees with
/// Spotify is worse than an error.
/// </remarks>
/// <param name="library">The library endpoints, taken narrowly so this is testable with a stub</param>
/// <param name="repository">The local index</param>
/// <param name="throttle">The shared rate gate</param>
public sealed class LibraryWriteService(
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
    public async Task SetTracksLikedAsync(
        IReadOnlyCollection<string> trackIds,
        bool liked,
        CancellationToken cancel = default)
    {
        if (trackIds.Count == 0) return;

        await repository.SetLikedAsync(trackIds, liked, cancel).ConfigureAwait(false);

        try
        {
            await WriteAsync(trackIds.Select(id => ToUri("track", id)), liked, cancel).ConfigureAwait(false);
        }
        catch
        {
            // Put the index back the way it was, then let the caller undo the UI.
            await repository.SetLikedAsync(trackIds, !liked, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Adds albums to the library or removes them, keeping the local index and Spotify in step.
    /// </summary>
    /// <param name="albumIds">The albums to change</param>
    /// <param name="saved">Whether they should end up in the library</param>
    /// <param name="cancel">Cancels the change</param>
    /// <exception cref="APIException">The change was rejected; local state has been rolled back</exception>
    public async Task SetAlbumsSavedAsync(
        IReadOnlyCollection<string> albumIds,
        bool saved,
        CancellationToken cancel = default)
    {
        if (albumIds.Count == 0) return;

        await repository.SetAlbumsSavedAsync(albumIds, saved, cancel).ConfigureAwait(false);

        try
        {
            await WriteAsync(albumIds.Select(id => ToUri("album", id)), saved, cancel).ConfigureAwait(false);
        }
        catch
        {
            await repository.SetAlbumsSavedAsync(albumIds, !saved, CancellationToken.None).ConfigureAwait(false);
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
    public async Task<IReadOnlyDictionary<string, bool>> ReconcileTracksAsync(
        IReadOnlyList<string> trackIds,
        CancellationToken cancel = default)
    {
        var states = new Dictionary<string, bool>();
        if (trackIds.Count == 0) return states;

        foreach (var chunk in Chunk(trackIds))
        {
            var ids = chunk.ToList();
            var uris = ids.Select(id => ToUri("track", id)).ToList();

            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            var results = await library
                .CheckItems(new LibraryCheckItemsRequest(uris), cancel)
                .ConfigureAwait(false);

            for (var i = 0; i < ids.Count && i < results.Count; i++)
            {
                states[ids[i]] = results[i];
            }
        }

        var nowLiked = states.Where(p => p.Value).Select(p => p.Key).ToList();
        var nowUnliked = states.Where(p => !p.Value).Select(p => p.Key).ToList();

        if (nowLiked.Count > 0) await repository.SetLikedAsync(nowLiked, true, cancel).ConfigureAwait(false);
        if (nowUnliked.Count > 0) await repository.SetLikedAsync(nowUnliked, false, cancel).ConfigureAwait(false);

        return states;
    }

    /// <summary>Sends the add or remove to Spotify, a request's worth of URIs at a time.</summary>
    /// <param name="uris">The URIs to add or remove</param>
    /// <param name="add">Whether they're being added to the library</param>
    /// <param name="cancel">Cancels the write</param>
    private async Task WriteAsync(IEnumerable<string> uris, bool add, CancellationToken cancel)
    {
        foreach (var chunk in Chunk(uris))
        {
            var batch = chunk.ToList();
            await throttle.WaitAsync(cancel).ConfigureAwait(false);

            if (add)
            {
                await library.SaveItems(new LibrarySaveItemsRequest(batch), cancel).ConfigureAwait(false);
            }
            else
            {
                await library.RemoveItems(new LibraryRemoveItemsRequest(batch), cancel).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Splits a set into request-sized chunks.</summary>
    /// <param name="values">The values to split</param>
    /// <returns>Chunks no larger than the API allows</returns>
    private static IEnumerable<IEnumerable<string>> Chunk(IEnumerable<string> values) =>
        values.Chunk(MaxUrisPerRequest);

    /// <summary>Turns an ID into the URI the library endpoints expect.</summary>
    /// <param name="kind">The entity kind, "track" or "album"</param>
    /// <param name="id">The entity's ID</param>
    /// <returns>The Spotify URI</returns>
    private static string ToUri(string kind, string id) =>
        id.StartsWith("spotify:", StringComparison.Ordinal) ? id : $"spotify:{kind}:{id}";
}
