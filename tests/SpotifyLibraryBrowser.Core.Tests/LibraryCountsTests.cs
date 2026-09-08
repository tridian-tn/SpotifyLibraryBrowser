namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// Covers the counts shown after a sync. They're the only signal that a stage did anything, so a
/// count that quietly means something other than it says sends you hunting the wrong bug.
/// </summary>
public sealed class LibraryCountsTests
{
    [Fact]
    public async Task A_readable_playlist_with_no_tracks_still_counts_as_readable()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var counts = await fixture.Library.GetCountsAsync();

        // Three playlists: one with tracks, one served but empty, one Spotify refused.
        Assert.Equal(3, counts.Playlists);

        // Counting from stored entries would call this 1, making a refusal and an empty playlist
        // look identical.
        Assert.Equal(2, counts.ReadablePlaylists);
    }

    [Fact]
    public async Task Saved_albums_are_counted_apart_from_albums_reached_through_a_track()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var counts = await fixture.Library.GetCountsAsync();

        // Six albums are held, but only the three actually saved drive the Album column.
        Assert.Equal(6, counts.Albums);
        Assert.Equal(3, counts.SavedAlbums);
    }
}
