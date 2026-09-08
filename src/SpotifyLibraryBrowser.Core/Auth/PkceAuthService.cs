using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

namespace SpotifyLibraryBrowser.Core.Auth;

/// <summary>
/// Runs the Authorization Code + PKCE flow and keeps the resulting client authenticated.
/// </summary>
/// <remarks>
/// PKCE rather than the client-secret flow because a desktop app can't keep a secret. The
/// callback lands on an explicit loopback IP — Spotify no longer accepts <c>localhost</c> in a
/// registered redirect URI.
/// </remarks>
/// <param name="clientId">The Client ID registered in the Spotify Developer Dashboard</param>
/// <param name="store">Where the refresh token is persisted between runs</param>
/// <param name="callbackPort">The loopback port the callback listens on</param>
public sealed class PkceAuthService(string clientId, TokenStore store, int callbackPort = PkceAuthService.DefaultCallbackPort)
{
    /// <summary>The loopback port the callback listens on unless told otherwise.</summary>
    public const int DefaultCallbackPort = 5543;

    /// <summary>
    /// Builds the redirect URI for a port.
    /// </summary>
    /// <remarks>
    /// Onboarding shows the user this exact string to paste into the dashboard, so it's built
    /// here rather than written out twice — a mismatch would surface as a baffling auth failure.
    /// </remarks>
    /// <param name="port">The loopback port</param>
    /// <returns>The redirect URI to register</returns>
    public static Uri CallbackUriFor(int port = DefaultCallbackPort) =>
        new($"http://127.0.0.1:{port}/callback");

    /// <summary>The Spotify Developer Dashboard, where the app registration is made.</summary>
    public const string DashboardUrl = "https://developer.spotify.com/dashboard";

    /// <summary>
    /// Everything the app needs: the library and playlists to index, follows to read, liking to
    /// write, and player scopes for Connect control.
    /// </summary>
    public static readonly string[] RequiredScopes =
    [
        Scopes.UserLibraryRead,
        Scopes.UserLibraryModify,
        Scopes.PlaylistReadPrivate,
        Scopes.PlaylistReadCollaborative,
        Scopes.UserFollowRead,
        Scopes.UserReadPlaybackState,
        Scopes.UserModifyPlaybackState
    ];

    /// <summary>The callback the Developer Dashboard has to have registered.</summary>
    public Uri CallbackUri { get; } = CallbackUriFor(callbackPort);

    /// <summary>Raised whenever a token is issued or refreshed, so it can be persisted.</summary>
    public event Action? TokenChanged;

    /// <summary>
    /// The refresh token currently in play.
    /// </summary>
    /// <remarks>
    /// A refresh response usually omits refresh_token, meaning "carry on using the one you have".
    /// Persisting the response verbatim would therefore blank it, and the next launch would have
    /// nothing to sign in with, so the live value is held here and written back when that happens.
    /// </remarks>
    private string? _refreshToken;

    /// <summary>
    /// Builds a client from the stored refresh token, without showing a browser.
    /// </summary>
    /// <param name="cancel">Cancels the restore</param>
    /// <returns>An authenticated client, or null when there's no usable stored token</returns>
    public async Task<SpotifyClient?> TryRestoreAsync(CancellationToken cancel = default)
    {
        var stored = await store.LoadAsync(cancel).ConfigureAwait(false);
        if (stored is null) return null;

        // Without a refresh token there's nothing to restore from, and handing an empty one to the
        // library throws rather than failing softly.
        if (string.IsNullOrEmpty(stored.RefreshToken))
        {
            store.Clear();
            return null;
        }

        var response = ToResponse(stored);
        _refreshToken = stored.RefreshToken;

        try
        {
            // A refresh here both proves the token still works and gets a fresh access token.
            var refreshed = await new OAuthClient()
                .RequestToken(new PKCETokenRefreshRequest(clientId, response.RefreshToken), cancel)
                .ConfigureAwait(false);

            await PersistAsync(refreshed, cancel).ConfigureAwait(false);
            return BuildClient(refreshed);
        }
        catch (APIException)
        {
            // Revoked or expired beyond recovery — fall back to a fresh interactive sign-in.
            store.Clear();
            return null;
        }
    }

    /// <summary>
    /// Runs the interactive flow: opens the browser, waits for the callback, exchanges the code.
    /// </summary>
    /// <param name="cancel">Cancels the wait for the callback</param>
    /// <returns>An authenticated client</returns>
    public async Task<SpotifyClient> AuthorizeAsync(CancellationToken cancel = default)
    {
        var (verifier, challenge) = PKCEUtil.GenerateCodes();
        var completion = new TaskCompletionSource<AuthorizationCodeResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = new EmbedIOAuthServer(CallbackUri, callbackPort);

        Task OnCode(object sender, AuthorizationCodeResponse response)
        {
            completion.TrySetResult(response);
            return Task.CompletedTask;
        }

        Task OnError(object sender, string error, string? state)
        {
            completion.TrySetException(new InvalidOperationException($"Spotify returned '{error}'."));
            return Task.CompletedTask;
        }

        server.AuthorizationCodeReceived += OnCode;
        server.ErrorReceived += OnError;

        await server.Start().ConfigureAwait(false);

        try
        {
            var request = new LoginRequest(CallbackUri, clientId, LoginRequest.ResponseType.Code)
            {
                CodeChallengeMethod = "S256",
                CodeChallenge = challenge,
                Scope = RequiredScopes
            };

            BrowserUtil.Open(request.ToUri());

            await using var registration = cancel.Register(() => completion.TrySetCanceled(cancel));
            var code = await completion.Task.ConfigureAwait(false);

            var token = await new OAuthClient()
                .RequestToken(new PKCETokenRequest(clientId, code.Code, CallbackUri, verifier), cancel)
                .ConfigureAwait(false);

            await PersistAsync(token, cancel).ConfigureAwait(false);
            return BuildClient(token);
        }
        finally
        {
            server.AuthorizationCodeReceived -= OnCode;
            server.ErrorReceived -= OnError;
            await server.Stop().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wraps a token in a client that refreshes itself, persisting each refreshed token.
    /// </summary>
    /// <param name="token">The token to authenticate with</param>
    /// <returns>The configured client</returns>
    private SpotifyClient BuildClient(PKCETokenResponse token)
    {
        var authenticator = new PKCEAuthenticator(clientId, token);

        authenticator.TokenRefreshed += (_, refreshed) =>
        {
            // Fire and forget: the refresh already succeeded, and losing the write only costs
            // one extra sign-in later.
            _ = PersistAsync(refreshed, CancellationToken.None);
        };

        var config = SpotifyClientConfig
            .CreateDefault()
            .WithAuthenticator(authenticator)
            .WithRetryHandler(new SimpleRetryHandler
            {
                RetryAfter = TimeSpan.FromSeconds(1),
                RetryTimes = 5,
                TooManyRequestsConsumesARetry = false
            });

        return new SpotifyClient(config);
    }

    /// <summary>Saves a token and lets listeners know.</summary>
    /// <param name="token">The token to save</param>
    /// <param name="cancel">Cancels the write</param>
    private async Task PersistAsync(PKCETokenResponse token, CancellationToken cancel)
    {
        // Keep the token we already hold when the response doesn't carry a replacement.
        if (!string.IsNullOrEmpty(token.RefreshToken)) _refreshToken = token.RefreshToken;

        if (string.IsNullOrEmpty(_refreshToken)) return;

        await store.SaveAsync(new StoredToken(
            token.AccessToken,
            token.TokenType,
            token.ExpiresIn,
            _refreshToken,
            token.Scope ?? string.Empty,
            token.CreatedAt), cancel).ConfigureAwait(false);

        TokenChanged?.Invoke();
    }

    /// <summary>Rebuilds the library's token type from what was persisted.</summary>
    /// <param name="stored">The persisted token</param>
    /// <returns>The token in the shape the client expects</returns>
    private static PKCETokenResponse ToResponse(StoredToken stored) => new()
    {
        AccessToken = stored.AccessToken,
        TokenType = stored.TokenType,
        ExpiresIn = stored.ExpiresIn,
        RefreshToken = stored.RefreshToken,
        Scope = stored.Scope,
        CreatedAt = stored.CreatedAt
    };
}
