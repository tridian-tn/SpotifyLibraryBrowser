using System.Net;
using NSubstitute;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using SpotifyLibraryBrowser.Core.Api;

namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// Covers the library client that sends save and remove URIs in the query string. Spotify ignores
/// them in the body, which is where the package puts them, and answers "Missing required field: uris".
/// </summary>
public sealed class QueryStringLibraryClientTests
{
    private static readonly List<string> Uris = ["spotify:track:tr-blue", "spotify:album:al-saw"];

    [Fact]
    public async Task Saving_sends_the_uris_in_the_query_string()
    {
        var api = Substitute.For<IAPIConnector>();
        api.Put(Arg.Any<Uri>(), Arg.Any<IDictionary<string, string>>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(HttpStatusCode.OK);

        // Called through the interface, as the app does, since that's the path the fix has to take over.
        ILibraryClient library = new QueryStringLibraryClient(api);

        Assert.True(await library.SaveItems(new LibrarySaveItemsRequest(Uris)));

        await api.Received(1).Put(
            Arg.Is<Uri>(u => u.ToString() == "me/library"),
            Arg.Is<IDictionary<string, string>>(p => p["uris"] == "spotify:track:tr-blue,spotify:album:al-saw"),
            Arg.Is<object?>(body => body == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_sends_the_uris_in_the_query_string()
    {
        var api = Substitute.For<IAPIConnector>();
        api.Delete(Arg.Any<Uri>(), Arg.Any<IDictionary<string, string>>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(HttpStatusCode.OK);

        ILibraryClient library = new QueryStringLibraryClient(api);

        Assert.True(await library.RemoveItems(new LibraryRemoveItemsRequest(Uris)));

        await api.Received(1).Delete(
            Arg.Is<Uri>(u => u.ToString() == "me/library"),
            Arg.Is<IDictionary<string, string>>(p => p["uris"] == "spotify:track:tr-blue,spotify:album:al-saw"),
            Arg.Is<object?>(body => body == null),
            Arg.Any<CancellationToken>());
    }
}
