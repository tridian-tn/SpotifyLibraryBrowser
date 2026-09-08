using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpotifyLibraryBrowser.Core.Auth;

/// <summary>The persisted OAuth token, mirroring what the PKCE flow hands back.</summary>
/// <param name="AccessToken">The current access token</param>
/// <param name="TokenType">The token type, normally "Bearer"</param>
/// <param name="ExpiresIn">Lifetime in seconds from <paramref name="CreatedAt"/></param>
/// <param name="RefreshToken">The refresh token, which is the part worth protecting</param>
/// <param name="Scope">The granted scopes</param>
/// <param name="CreatedAt">When the token was issued</param>
public sealed record StoredToken(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken,
    string Scope,
    DateTime CreatedAt);

/// <summary>
/// Persists the refresh token between runs so you only authorise once.
/// </summary>
/// <remarks>
/// On Windows the file is wrapped with DPAPI under the current user. Elsewhere DPAPI doesn't
/// exist, so it falls back to a file readable only by its owner — weaker, but it keeps the
/// cross-platform promise without pretending the two are equivalent. The first byte records
/// which form was used, so a file never gets read the wrong way round.
/// </remarks>
/// <param name="filePath">Where the token file lives</param>
public sealed class TokenStore(string filePath)
{
    private const byte PlainMarker = 0x00;
    private const byte ProtectedMarker = 0x01;

    /// <summary>Extra entropy mixed into the DPAPI blob so it isn't interchangeable with other apps'.</summary>
    private static readonly byte[] Entropy = "SpotifyLibraryBrowser"u8.ToArray();

    /// <summary>Reads the stored token.</summary>
    /// <param name="cancel">Cancels the read</param>
    /// <returns>The stored token, or null when there isn't one or it can't be read</returns>
    public async Task<StoredToken?> LoadAsync(CancellationToken cancel = default)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            var raw = await File.ReadAllBytesAsync(filePath, cancel).ConfigureAwait(false);
            if (raw.Length < 2) return null;

            var payload = raw[0] == ProtectedMarker
                ? Unprotect(raw.AsSpan(1).ToArray())
                : raw.AsSpan(1).ToArray();

            return JsonSerializer.Deserialize<StoredToken>(payload);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            // A token we can't read is the same as not having one — the user just signs in again.
            return null;
        }
    }

    /// <summary>Writes the token, protecting it where the platform allows.</summary>
    /// <param name="token">The token to persist</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task SaveAsync(StoredToken token, CancellationToken cancel = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var payload = JsonSerializer.SerializeToUtf8Bytes(token);
        var canProtect = OperatingSystem.IsWindows();

        var body = canProtect ? Protect(payload) : payload;
        var output = new byte[body.Length + 1];
        output[0] = canProtect ? ProtectedMarker : PlainMarker;
        body.CopyTo(output, 1);

        await File.WriteAllBytesAsync(filePath, output, cancel).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Deletes the stored token, for signing out.</summary>
    public void Clear()
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    /// <summary>Wraps bytes with DPAPI under the current user.</summary>
    /// <param name="payload">The bytes to protect</param>
    /// <returns>The protected blob</returns>
    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] payload) =>
        ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);

    /// <summary>Unwraps a DPAPI blob.</summary>
    /// <param name="payload">The protected blob</param>
    /// <returns>The original bytes</returns>
    private static byte[] Unprotect(byte[] payload)
    {
        if (!OperatingSystem.IsWindows()) throw new CryptographicException("Protected token file needs Windows.");

        return ProtectedData.Unprotect(payload, Entropy, DataProtectionScope.CurrentUser);
    }
}
