using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SpotifyAPI.Web;
using SpotifyLibraryBrowser.Core.Api;
using SpotifyLibraryBrowser.Core.Browsing;
using SpotifyLibraryBrowser.Core.Library;

namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// Covers the app's only write path. The optimistic update is the risky part: if a rollback ever
/// fails to happen, the track list quietly disagrees with Spotify, which is worse than an error.
/// </summary>
public sealed class LikeServiceTests
{
    /// <summary>A throttle with a generous allowance, so tests don't sit waiting on the rate gate.</summary>
    private static RequestThrottle Throttle() => new(maxRequests: 10_000);

    /// <summary>Reads a track's liked state straight out of the index.</summary>
    /// <param name="fixture">The seeded fixture</param>
    /// <param name="trackId">The track to look at</param>
    /// <returns>Whether the index thinks it's liked</returns>
    private static async Task<bool> IsLiked(LibraryFixture fixture, string trackId)
    {
        var request = new BrowseRequest([new ColumnSelection(ColumnCriterion.Album, [])]);
        var tracks = await fixture.Browse.GetTracksAsync(request);
        return tracks.Single(t => t.Id == trackId).IsLiked;
    }

    [Fact]
    public async Task Liking_a_track_updates_the_index_and_calls_the_api()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();
        var service = new LikeService(library, fixture.Library, Throttle());

        await service.SetLikedAsync(["tr-blue"], true);

        Assert.True(await IsLiked(fixture, "tr-blue"));
        await library.Received(1).SaveItems(
            Arg.Is<LibrarySaveItemsRequest>(r => r.Uris.Single() == "spotify:track:tr-blue"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unliking_a_track_updates_the_index_and_calls_remove()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();
        var service = new LikeService(library, fixture.Library, Throttle());

        await service.SetLikedAsync(["tr-sowhat"], false);

        Assert.False(await IsLiked(fixture, "tr-sowhat"));
        await library.Received(1).RemoveItems(
            Arg.Any<LibraryRemoveItemsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_rejected_like_rolls_the_index_back()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();

        library.SaveItems(Arg.Any<LibrarySaveItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new APIException("nope"));

        var service = new LikeService(library, fixture.Library, Throttle());

        await Assert.ThrowsAsync<APIException>(() => service.SetLikedAsync(["tr-blue"], true));

        // The optimistic write must not survive the failure.
        Assert.False(await IsLiked(fixture, "tr-blue"));
    }

    [Fact]
    public async Task A_rejected_unlike_restores_the_liked_flag()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();

        library.RemoveItems(Arg.Any<LibraryRemoveItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new APIException("nope"));

        var service = new LikeService(library, fixture.Library, Throttle());

        await Assert.ThrowsAsync<APIException>(() => service.SetLikedAsync(["tr-sowhat"], false));

        Assert.True(await IsLiked(fixture, "tr-sowhat"));
    }

    [Fact]
    public async Task Selections_larger_than_one_request_are_chunked()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();
        var service = new LikeService(library, fixture.Library, Throttle());

        // 120 IDs is three requests at the API's 50-URI cap.
        var ids = Enumerable.Range(0, 120).Select(i => $"tr-bulk-{i}").ToList();

        await service.SetLikedAsync(ids, true);

        await library.Received(3).SaveItems(
            Arg.Any<LibrarySaveItemsRequest>(), Arg.Any<CancellationToken>());

        await library.Received(1).SaveItems(
            Arg.Is<LibrarySaveItemsRequest>(r => r.Uris.Count == 20), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_is_sent_for_an_empty_selection()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();
        var service = new LikeService(library, fixture.Library, Throttle());

        await service.SetLikedAsync([], true);

        await library.DidNotReceive().SaveItems(
            Arg.Any<LibrarySaveItemsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconciliation_corrects_state_changed_on_another_device()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();

        // Spotify says So What was unliked elsewhere, and Blue in Green was liked elsewhere.
        library.CheckItems(Arg.Any<LibraryCheckItemsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new List<bool> { false, true });

        var service = new LikeService(library, fixture.Library, Throttle());

        var corrections = await service.ReconcileAsync(["tr-sowhat", "tr-blue"]);

        Assert.False(corrections["tr-sowhat"]);
        Assert.True(corrections["tr-blue"]);
        Assert.False(await IsLiked(fixture, "tr-sowhat"));
        Assert.True(await IsLiked(fixture, "tr-blue"));
    }
}
