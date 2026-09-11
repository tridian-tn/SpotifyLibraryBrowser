using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SpotifyAPI.Web;
using SpotifyLibraryBrowser.Core.Api;
using SpotifyLibraryBrowser.Core.Browsing;
using SpotifyLibraryBrowser.Core.Library;

namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// Covers the app's write path into the library. The optimistic update is the risky part: if a rollback ever
/// fails to happen, the track list quietly disagrees with Spotify, which is worse than an error.
/// </summary>
public sealed class LibraryWriteServiceTests
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
        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        await service.SetTracksLikedAsync(["tr-blue"], true);

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
        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        await service.SetTracksLikedAsync(["tr-sowhat"], false);

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

        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        await Assert.ThrowsAsync<APIException>(() => service.SetTracksLikedAsync(["tr-blue"], true));

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

        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        await Assert.ThrowsAsync<APIException>(() => service.SetTracksLikedAsync(["tr-sowhat"], false));

        Assert.True(await IsLiked(fixture, "tr-sowhat"));
    }

    [Fact]
    public async Task Selections_larger_than_one_request_are_chunked()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();
        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        // 120 IDs is three requests at the API's 50-URI cap.
        var ids = Enumerable.Range(0, 120).Select(i => $"tr-bulk-{i}").ToList();

        await service.SetTracksLikedAsync(ids, true);

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
        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        await service.SetTracksLikedAsync([], true);

        await library.DidNotReceive().SaveItems(
            Arg.Any<LibrarySaveItemsRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Reads an album's saved state as the track list would see it.</summary>
    /// <param name="fixture">The seeded fixture</param>
    /// <param name="albumId">The album to look at</param>
    /// <returns>Whether the index has it as being in the library</returns>
    private static async Task<bool> IsAlbumSaved(LibraryFixture fixture, string albumId)
    {
        var request = new BrowseRequest([new ColumnSelection(ColumnCriterion.Album, [])]);
        var tracks = await fixture.Browse.GetTracksAsync(request);
        return tracks.First(t => t.AlbumId == albumId).AlbumIsSaved;
    }

    [Fact]
    public async Task Saving_an_album_marks_it_and_sends_an_album_uri()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();
        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        // Selected Ambient Works is in the index only because a track on it was liked.
        Assert.False(await IsAlbumSaved(fixture, "al-saw"));

        await service.SetAlbumsSavedAsync(["al-saw"], true);

        Assert.True(await IsAlbumSaved(fixture, "al-saw"));

        // The URI has to say album, not track, or Spotify saves the wrong thing entirely.
        await library.Received(1).SaveItems(
            Arg.Is<LibrarySaveItemsRequest>(r => r.Uris.Single() == "spotify:album:al-saw"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_an_album_clears_the_flag_without_dropping_the_row()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();
        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        await service.SetAlbumsSavedAsync(["al-kob"], false);

        Assert.False(await IsAlbumSaved(fixture, "al-kob"));

        // The album keeps its row: liked songs and playlist entries may still point at it.
        var counts = await fixture.Library.GetCountsAsync();
        Assert.Equal(6, counts.Albums);
        Assert.Equal(2, counts.SavedAlbums);
    }

    [Fact]
    public async Task A_rejected_album_save_rolls_the_index_back()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();

        library.SaveItems(Arg.Any<LibrarySaveItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new APIException("nope"));

        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        await Assert.ThrowsAsync<APIException>(() => service.SetAlbumsSavedAsync(["al-saw"], true));

        Assert.False(await IsAlbumSaved(fixture, "al-saw"));
    }

    [Fact]
    public async Task Albums_and_tracks_share_the_chunking()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var library = Substitute.For<ILibraryClient>();
        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        var ids = Enumerable.Range(0, 120).Select(i => $"al-bulk-{i}").ToList();

        await service.SetAlbumsSavedAsync(ids, true);

        await library.Received(3).SaveItems(
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

        var service = new LibraryWriteService(library, fixture.Library, Throttle());

        var corrections = await service.ReconcileTracksAsync(["tr-sowhat", "tr-blue"]);

        Assert.False(corrections["tr-sowhat"]);
        Assert.True(corrections["tr-blue"]);
        Assert.False(await IsLiked(fixture, "tr-sowhat"));
        Assert.True(await IsLiked(fixture, "tr-blue"));
    }
}
