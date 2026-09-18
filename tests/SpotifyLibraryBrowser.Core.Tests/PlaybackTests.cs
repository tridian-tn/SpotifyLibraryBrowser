using NSubstitute;
using SpotifyAPI.Web;
using SpotifyLibraryBrowser.Core.Api;
using SpotifyLibraryBrowser.Core.Playback;

namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// Covers the shape of the play request. Getting the context or the starting point wrong doesn't
/// throw - it just plays the wrong thing, which is the kind of bug only a listener notices.
/// </summary>
public sealed class PlaybackTests
{
    /// <summary>A throttle with a generous allowance, so tests don't sit waiting on the rate gate.</summary>
    private static RequestThrottle Throttle() => new(maxRequests: 10_000);

    /// <summary>Captures the request a play call would send.</summary>
    /// <param name="player">The stubbed player</param>
    /// <returns>The single request it was given</returns>
    private static PlayerResumePlaybackRequest Sent(IPlayerClient player) =>
        (PlayerResumePlaybackRequest)player.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPlayerClient.ResumePlayback))
            .GetArguments()[0]!;

    [Fact]
    public async Task Playing_within_a_context_starts_on_the_track_asked_for()
    {
        var player = Substitute.For<IPlayerClient>();
        var controller = new PlaybackController(player, Throttle());

        var outcome = await controller.PlayContextAsync(
            "spotify:playlist:pl-1", "spotify:track:tr-warsaw", "dev-1");

        Assert.Equal(PlaybackOutcome.Started, outcome);

        var request = Sent(player);
        Assert.Equal("spotify:playlist:pl-1", request.ContextUri);
        Assert.Equal("dev-1", request.DeviceId);

        // By URI, not by index. An index has to agree with the context's own numbering, and neither
        // an album's track numbers nor a synced playlist position reliably does.
        Assert.Equal("spotify:track:tr-warsaw", request.OffsetParam?.Uri);
        Assert.Null(request.OffsetParam?.Position);
    }

    [Fact]
    public async Task Playing_a_context_with_no_starting_track_sends_no_offset()
    {
        var player = Substitute.For<IPlayerClient>();
        var controller = new PlaybackController(player, Throttle());

        await controller.PlayContextAsync("spotify:album:al-kob");

        var request = Sent(player);
        Assert.Equal("spotify:album:al-kob", request.ContextUri);

        // "Start at the beginning" means leaving the offset out altogether, rather than sending an
        // offset object with nothing in it for Spotify to make sense of.
        Assert.Null(request.OffsetParam);
        Assert.Null(request.DeviceId);
    }

    [Fact]
    public async Task A_refused_play_falls_back_rather_than_throwing()
    {
        var player = Substitute.For<IPlayerClient>();
        var controller = new PlaybackController(player, Throttle());

        player.ResumePlayback(Arg.Any<PlayerResumePlaybackRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new APIException("No active device found"));

        // No Connect device and a non-Premium account both land here. Whether the hand-off to the
        // desktop client works depends on the machine, so the one thing worth asserting is that
        // the refusal was caught and turned into an outcome rather than escaping.
        var outcome = await controller.PlayContextAsync("spotify:album:al-kob", "spotify:track:tr-sowhat");

        Assert.NotEqual(PlaybackOutcome.Started, outcome);
    }

    [Fact]
    public async Task A_refused_play_hands_off_the_track_rather_than_the_context()
    {
        var player = Substitute.For<IPlayerClient>();
        var handed = new List<string>();
        var controller = new PlaybackController(player, Throttle()) { HandOff = uri => { handed.Add(uri); return true; } };

        player.ResumePlayback(Arg.Any<PlayerResumePlaybackRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new APIException("No active device found"));

        await controller.PlayContextAsync("spotify:playlist:pl-1", "spotify:track:tr-warsaw");

        // One URI is all the hand-off carries, so the context can't come with it. The clicked track
        // is the half worth keeping - playing the playlist from the top would be the wrong song.
        Assert.Equal(["spotify:track:tr-warsaw"], handed);

        // With no starting track asked for, the context is all there is to hand over.
        await controller.PlayContextAsync("spotify:album:al-kob");
        Assert.Equal(["spotify:track:tr-warsaw", "spotify:album:al-kob"], handed);
    }
}
