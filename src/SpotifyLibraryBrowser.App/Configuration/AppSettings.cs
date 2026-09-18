using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpotifyLibraryBrowser.Core.Browsing;
using SpotifyLibraryBrowser.Core.Discography;

namespace SpotifyLibraryBrowser.App.Configuration;

/// <summary>Where the app keeps its data.</summary>
public static class AppPaths
{
    /// <summary>The per-user folder holding the index, token and settings.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotifyLibraryBrowser");

    /// <summary>The local library index.</summary>
    public static string DatabaseFile => Path.Combine(DataDirectory, "library.db");

    /// <summary>The persisted OAuth token.</summary>
    public static string TokenFile => Path.Combine(DataDirectory, "token.bin");

    /// <summary>The user's settings.</summary>
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}

/// <summary>
/// What the app remembers between runs: the registered Client ID, the column layout, and the
/// window's shape.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The Client ID from the user's Spotify Developer Dashboard app.</summary>
    public string? ClientId { get; set; }

    /// <summary>The browser's columns, left to right.</summary>
    public List<ColumnCriterion> Columns { get; set; } =
        [ColumnCriterion.AlbumArtist, ColumnCriterion.Album];

    /// <summary>Whether the "Liked only" filter is on.</summary>
    public bool LikedOnly { get; set; }

    /// <summary>Whether browsing is restricted to albums that are in the library.</summary>
    public bool SavedAlbumsOnly { get; set; } = true;

    /// <summary>Whether list rows are tightened up to fit more on screen.</summary>
    public bool CompactRows { get; set; }

    /// <summary>
    /// How the track list is ordered.
    /// </summary>
    /// <remarks>
    /// Playlist order by default. It changes nothing unless a playlist is being browsed, so
    /// defaulting to it means selecting a playlist shows it in its own running order without anyone
    /// having to go looking for the setting.
    /// </remarks>
    public TrackSort TrackOrder { get; set; } = TrackSort.PlaylistOrder;

    /// <summary>How the discography panel orders an artist's releases.</summary>
    public DiscographySort DiscographySort { get; set; } = DiscographySort.NewestFirst;

    /// <summary>Which kinds of release the discography panel asks for.</summary>
    public DiscographyGroups DiscographyGroups { get; set; } =
        DiscographyGroups.Albums | DiscographyGroups.Singles;

    /// <summary>The window's last width.</summary>
    public double WindowWidth { get; set; } = 1280;

    /// <summary>The window's last height.</summary>
    public double WindowHeight { get; set; } = 800;

    /// <summary>How tall the column browser was, in pixels.</summary>
    public double BrowserHeight { get; set; } = 300;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Serialises writes, so two of them can't be in the file at once.
    /// </summary>
    /// <remarks>
    /// Toggles write immediately and don't wait for each other, and closing the window writes too.
    /// Left to race, one caller loses to a sharing violation and the preference it was saving
    /// silently goes with it.
    /// </remarks>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Reads the settings, falling back to defaults when there's nothing usable.</summary>
    /// <param name="cancel">Cancels the read</param>
    /// <returns>The stored settings, or fresh defaults</returns>
    public static async Task<AppSettings> LoadAsync(CancellationToken cancel = default)
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();

            await using var stream = File.OpenRead(AppPaths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancel)
                   ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings.</summary>
    /// <param name="settings">The settings to persist</param>
    /// <param name="cancel">Cancels the write</param>
    public static async Task SaveAsync(AppSettings settings, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);

        await Gate.WaitAsync(cancel).ConfigureAwait(false);

        try
        {
            // Written beside the real file and moved over it, rather than truncating the real one
            // and writing into it. A crash or a force-quit partway through the second leaves
            // settings that won't parse; the move either happens or it doesn't.
            var temporary = AppPaths.SettingsFile + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancel)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
        }
        finally
        {
            Gate.Release();
        }
    }
}
