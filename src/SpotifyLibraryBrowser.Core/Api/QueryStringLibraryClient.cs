using System.Net;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace SpotifyLibraryBrowser.Core.Api;

/// <summary>
/// The package's library client, with the URI-based save and remove sending their URIs where
/// Spotify actually reads them.
/// </summary>
/// <remarks>
/// SpotifyAPI.Web 7.4.2 sends <c>uris</c> in the JSON body for <c>PUT</c> and <c>DELETE
/// /me/library</c>, but Spotify only reads it from the query string, so every save and remove
/// comes back "Missing required field: uris". The contains check already uses the query string
/// and is left to the base class. The base methods aren't virtual, so this re-implements
/// <see cref="ILibraryClient"/> to make calls through the interface land here instead.
/// </remarks>
/// <param name="api">The connector the rest of the client uses, so auth and retries are shared</param>
public sealed class QueryStringLibraryClient(IAPIConnector api) : LibraryClient(api), ILibraryClient
{
    /// <summary>Saves a list of Spotify URIs to the current user's library.</summary>
    /// <param name="request">The URIs to save</param>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether Spotify accepted it</returns>
    public new async Task<bool> SaveItems(LibrarySaveItemsRequest request, CancellationToken cancel = default)
    {
        var status = await API.Put(SpotifyUrls.Library(), ToQuery(request.Uris), null, cancel).ConfigureAwait(false);
        return status == HttpStatusCode.OK;
    }

    /// <summary>Removes a list of Spotify URIs from the current user's library.</summary>
    /// <param name="request">The URIs to remove</param>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether Spotify accepted it</returns>
    public new async Task<bool> RemoveItems(LibraryRemoveItemsRequest request, CancellationToken cancel = default)
    {
        var status = await API.Delete(SpotifyUrls.Library(), ToQuery(request.Uris), null, cancel).ConfigureAwait(false);
        return status == HttpStatusCode.OK;
    }

    /// <summary>Puts a set of URIs into the comma-separated form the endpoint expects.</summary>
    /// <param name="uris">The URIs to send</param>
    /// <returns>The query parameters</returns>
    private static Dictionary<string, string> ToQuery(IEnumerable<string> uris) =>
        new() { ["uris"] = string.Join(',', uris) };
}
