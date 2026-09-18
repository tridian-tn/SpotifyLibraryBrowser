using System.Diagnostics;
using SpotifyAPI.Web;
using SpotifyLibraryBrowser.Core.Api;

namespace SpotifyLibraryBrowser.Core.Playback;

/// <summary>What happened when playback was requested.</summary>
public enum PlaybackOutcome
{
    /// <summary>Spotify accepted it and is playing on a Connect device.</summary>
    Started = 0,

    /// <summary>No device was available, so the request was handed to the desktop client.</summary>
    HandedOff = 1,

    /// <summary>Neither route worked.</summary>
    Failed = 2
}

/// <summary>What came of queueing a selection.</summary>
/// <param name="Queued">How many tracks made it into the queue</param>
/// <param name="Failure">Why it stopped, or null when it didn't</param>
public sealed record QueueResult(int Queued, string? Failure);

/// <summary>
/// Drives playback on whatever Spotify client is already running.
/// </summary>
/// <remarks>
/// A native app can't be a playback device itself — the Web Playback SDK is browser-only — so
/// this is a Connect remote. When there's no live device, or the account turns out not to be
/// Premium, it falls back to handing the URI to the desktop client. That fallback is the only
/// way to discover the entitlement now that the user object no longer exposes it.
/// </remarks>
/// <param name="player">The player endpoints, taken narrowly so this is testable with a stub</param>
/// <param name="throttle">The shared rate gate</param>
public sealed class PlaybackController(IPlayerClient player, RequestThrottle throttle)
{
    /// <summary>How many tracks a single queue request will add before it stops.</summary>
    public const int MaxQueued = 50;

    /// <summary>
    /// How a URI reaches the desktop client when Connect won't take it.
    /// </summary>
    /// <remarks>
    /// Swappable so a test can see which URI was chosen without launching anything. Nothing but the
    /// tests replaces it.
    /// </remarks>
    public Func<string, bool> HandOff { get; init; } = OpenInSpotify;

    /// <summary>Lists the Connect devices available to play on.</summary>
    /// <param name="cancel">Cancels the call</param>
    /// <returns>The available devices, empty when none are live</returns>
    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancel = default)
    {
        try
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);
            var response = await player.GetAvailableDevices(cancel).ConfigureAwait(false);
            return response.Devices ?? [];
        }
        catch (APIException)
        {
            return [];
        }
    }

    /// <summary>Reads what's playing right now, for the now-playing bar.</summary>
    /// <param name="cancel">Cancels the call</param>
    /// <returns>The current playback state, or null when nothing is playing</returns>
    public async Task<CurrentlyPlayingContext?> GetCurrentAsync(CancellationToken cancel = default)
    {
        try
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);
            return await player.GetCurrentPlayback(cancel).ConfigureAwait(false);
        }
        catch (APIException)
        {
            return null;
        }
    }

    /// <summary>
    /// Plays an album or playlist, optionally starting on a particular track within it.
    /// </summary>
    /// <remarks>
    /// The starting point is given as a track URI rather than an index. Spotify accepts either, but
    /// an index has to agree with the context's own numbering: a multi-disc album restarts at track
    /// one on each disc, so a track number is the wrong index, and a playlist's stored positions are
    /// only as fresh as the last sync. A URI needs neither to be right.
    /// </remarks>
    /// <param name="contextUri">The album or playlist URI to play</param>
    /// <param name="offsetUri">The track URI to start on, or null to start at the beginning</param>
    /// <param name="deviceId">The device to play on, or null for the active one</param>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether playback started, was handed off, or failed</returns>
    public async Task<PlaybackOutcome> PlayContextAsync(
        string contextUri,
        string? offsetUri = null,
        string? deviceId = null,
        CancellationToken cancel = default)
    {
        var request = new PlayerResumePlaybackRequest { ContextUri = contextUri };

        if (!string.IsNullOrEmpty(offsetUri))
        {
            request.OffsetParam = new PlayerResumePlaybackRequest.Offset { Uri = offsetUri };
        }

        // The hand-off takes one URI, so a context and a starting track can't both survive it.
        // The track wins: someone who double-clicked a row wants to hear that row, and hearing it
        // without the rest of the playlist queued behind it beats hearing something else entirely.
        var fallback = string.IsNullOrEmpty(offsetUri) ? contextUri : offsetUri;

        return await ResumeAsync(request, fallback, deviceId, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Plays an explicit list of tracks, which is how a column selection gets played.
    /// </summary>
    /// <param name="trackUris">The track URIs to play, in order</param>
    /// <param name="deviceId">The device to play on, or null for the active one</param>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether playback started, was handed off, or failed</returns>
    public async Task<PlaybackOutcome> PlayTracksAsync(
        IReadOnlyList<string> trackUris,
        string? deviceId = null,
        CancellationToken cancel = default)
    {
        if (trackUris.Count == 0) return PlaybackOutcome.Failed;

        var request = new PlayerResumePlaybackRequest { Uris = trackUris.ToList() };
        return await ResumeAsync(request, trackUris[0], deviceId, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds tracks to the end of the play queue.
    /// </summary>
    /// <remarks>
    /// Spotify queues one URI per request, so a big selection is a lot of requests. This stops at
    /// <see cref="MaxQueued"/> rather than spending a rate-limit window queueing an entire
    /// discography, and tells the caller how many made it.
    /// </remarks>
    /// <param name="trackUris">The tracks to queue, in order</param>
    /// <param name="deviceId">The device to queue on, or null for the active one</param>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>How many were queued, and why it stopped if it did</returns>
    public async Task<QueueResult> QueueAsync(
        IReadOnlyList<string> trackUris,
        string? deviceId = null,
        CancellationToken cancel = default)
    {
        var queued = 0;

        foreach (var uri in trackUris.Take(MaxQueued))
        {
            var request = new PlayerAddToQueueRequest(uri);
            if (!string.IsNullOrEmpty(deviceId)) request.DeviceId = deviceId;

            try
            {
                await throttle.WaitAsync(cancel).ConfigureAwait(false);
                await player.AddToQueue(request, cancel).ConfigureAwait(false);
                queued++;
            }
            catch (APIException e)
            {
                // Carry the reason back rather than collapsing every refusal into "no device".
                // No active device and a non-Premium account both land here, and telling someone
                // the wrong one sends them looking in the wrong place.
                return new QueueResult(queued, e.Message);
            }
        }

        return new QueueResult(queued, null);
    }

    /// <summary>Pauses playback.</summary>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether the request was accepted</returns>
    public Task<bool> PauseAsync(CancellationToken cancel = default) =>
        TryAsync(() => player.PausePlayback(cancel));

    /// <summary>Resumes playback without changing what's queued.</summary>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether the request was accepted</returns>
    public Task<bool> ResumeAsync(CancellationToken cancel = default) =>
        TryAsync(() => player.ResumePlayback(cancel));

    /// <summary>Skips to the next track.</summary>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether the request was accepted</returns>
    public Task<bool> NextAsync(CancellationToken cancel = default) =>
        TryAsync(() => player.SkipNext(cancel));

    /// <summary>Skips to the previous track.</summary>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether the request was accepted</returns>
    public Task<bool> PreviousAsync(CancellationToken cancel = default) =>
        TryAsync(() => player.SkipPrevious(cancel));

    /// <summary>Moves playback to another Connect device.</summary>
    /// <param name="deviceId">The device to transfer to</param>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether the request was accepted</returns>
    public Task<bool> TransferAsync(string deviceId, CancellationToken cancel = default) =>
        TryAsync(() => player.TransferPlayback(new PlayerTransferPlaybackRequest([deviceId])));

    /// <summary>
    /// Hands a URI to the installed Spotify client.
    /// </summary>
    /// <remarks>This is what runs when there's no Connect device to talk to.</remarks>
    /// <param name="uri">The Spotify URI to open</param>
    /// <returns>Whether the handover was launched</returns>
    public static bool OpenInSpotify(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a play request, falling back to the desktop client when Connect can't serve it.
    /// </summary>
    /// <param name="request">The prepared play request</param>
    /// <param name="fallbackUri">The URI to hand off if Connect fails</param>
    /// <param name="deviceId">The device to target, or null for the active one</param>
    /// <param name="cancel">Cancels the request</param>
    /// <returns>Whether playback started, was handed off, or failed</returns>
    private async Task<PlaybackOutcome> ResumeAsync(
        PlayerResumePlaybackRequest request,
        string fallbackUri,
        string? deviceId,
        CancellationToken cancel)
    {
        if (!string.IsNullOrEmpty(deviceId)) request.DeviceId = deviceId;

        try
        {
            await throttle.WaitAsync(cancel).ConfigureAwait(false);
            await player.ResumePlayback(request, cancel).ConfigureAwait(false);
            return PlaybackOutcome.Started;
        }
        catch (APIException)
        {
            // No active device, or the account isn't Premium — either way the desktop client can
            // still take it.
            return HandOff(fallbackUri) ? PlaybackOutcome.HandedOff : PlaybackOutcome.Failed;
        }
    }

    /// <summary>Runs a transport command, treating an API refusal as a soft failure.</summary>
    /// <param name="action">The command to run</param>
    /// <returns>Whether the command was accepted</returns>
    private static async Task<bool> TryAsync(Func<Task<bool>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (APIException)
        {
            return false;
        }
    }
}
