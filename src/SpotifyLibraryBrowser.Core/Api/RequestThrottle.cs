namespace SpotifyLibraryBrowser.Core.Api;

/// <summary>
/// A sliding-window gate over outbound API calls.
/// </summary>
/// <remarks>
/// Spotify rate-limits on a rolling 30-second window and doesn't publish the development-mode
/// figure, so this defaults to a deliberately conservative rate. The retry handler still catches
/// a 429 if the guess is too generous — this just makes hitting one unlikely, because a full
/// first sync of a large library is exactly the workload that would trip it.
/// </remarks>
/// <param name="maxRequests">How many requests may start within the window</param>
/// <param name="window">The rolling window, defaulting to Spotify's 30 seconds</param>
public sealed class RequestThrottle(int maxRequests = 90, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(30);
    private readonly Queue<DateTimeOffset> _starts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Waits until another request may start, then records it.
    /// </summary>
    /// <remarks>The gate is held across the wait, so callers queue rather than all waking at once.</remarks>
    /// <param name="cancel">Cancels the wait</param>
    public async Task WaitAsync(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);

        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;

                while (_starts.Count > 0 && now - _starts.Peek() >= _window) _starts.Dequeue();

                if (_starts.Count < maxRequests) break;

                var wait = _window - (now - _starts.Peek());
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel).ConfigureAwait(false);
            }

            _starts.Enqueue(DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }
}
