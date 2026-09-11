using SpotifyLibraryBrowser.Core.Data;
using SpotifyLibraryBrowser.Core.Discography;
using SpotifyLibraryBrowser.Core.Model;

namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// Covers the discography panel's ordering and its cache rules. The sort is what the user picks
/// between, so each option has to mean what its label says.
/// </summary>
public sealed class DiscographyTests
{
    /// <summary>Builds a release for sorting.</summary>
    /// <param name="name">The album title</param>
    /// <param name="release">The release date, or null for an undated one</param>
    /// <param name="tracks">How many tracks it holds</param>
    /// <param name="saved">Whether the library has it</param>
    /// <param name="held">How many of its tracks the library holds</param>
    /// <returns>The release</returns>
    private static DiscographyAlbum Album(
        string name,
        string? release,
        int tracks = 10,
        bool saved = false,
        int held = 0) =>
        new($"al-{name}", name, release, ReleasePrecision.Day, null, tracks, saved, held);

    private static readonly DiscographyAlbum[] Catalogue =
    [
        Album("Debut", "1993-07-05"),
        Album("Follow Up", "1997-02-11", tracks: 14),
        Album("Undated Odds and Ends", null, tracks: 3),
        Album("Comeback", "2015-10-30", tracks: 8, saved: true),
        Album("Early Singles", "1991-01-01", tracks: 2, held: 1)
    ];

    [Fact]
    public void Newest_first_orders_by_release_and_puts_undated_last()
    {
        var sorted = DiscographyRepository.Sort(Catalogue, DiscographySort.NewestFirst);

        // An undated release sorts to the end rather than pretending to be the oldest thing here.
        Assert.Equal(
            ["Comeback", "Follow Up", "Debut", "Early Singles", "Undated Odds and Ends"],
            sorted.Select(a => a.Name));
    }

    [Fact]
    public void Oldest_first_reverses_it_but_still_leaves_undated_last()
    {
        var sorted = DiscographyRepository.Sort(Catalogue, DiscographySort.OldestFirst);

        Assert.Equal(
            ["Early Singles", "Debut", "Follow Up", "Comeback", "Undated Odds and Ends"],
            sorted.Select(a => a.Name));

        // Undated stays at the end in both directions, rather than flipping to the front.
        Assert.Equal("Undated Odds and Ends", sorted[^1].Name);
    }

    [Fact]
    public void Title_sorts_alphabetically_ignoring_case()
    {
        var sorted = DiscographyRepository.Sort(Catalogue, DiscographySort.Name);

        Assert.Equal(
            ["Comeback", "Debut", "Early Singles", "Follow Up", "Undated Odds and Ends"],
            sorted.Select(a => a.Name));
    }

    [Fact]
    public void Longest_first_floats_the_long_players_above_the_singles()
    {
        var sorted = DiscographyRepository.Sort(Catalogue, DiscographySort.TrackCount);

        Assert.Equal(["Follow Up", "Debut", "Comeback", "Undated Odds and Ends", "Early Singles"],
            sorted.Select(a => a.Name));
    }

    [Fact]
    public void In_library_first_leads_with_what_you_own_then_what_you_partly_own()
    {
        var sorted = DiscographyRepository.Sort(Catalogue, DiscographySort.InLibraryFirst);

        Assert.Equal("Comeback", sorted[0].Name);

        // Then the record a track was liked from, ahead of ones the library has nothing of.
        Assert.Equal("Early Singles", sorted[1].Name);
    }

    [Fact]
    public void A_release_with_tracks_but_no_save_reads_as_partly_held()
    {
        var partly = Album("Early Singles", "1991-01-01", tracks: 2, held: 1);
        var owned = Album("Comeback", "2015-10-30", saved: true, held: 8);
        var absent = Album("Debut", "1993-07-05");

        // This is the prompt the panel exists to surface: liked tracks off an unsaved record.
        Assert.True(partly.IsPartiallyHeld);

        // Already saved, so there's nothing to suggest.
        Assert.False(owned.IsPartiallyHeld);
        Assert.False(absent.IsPartiallyHeld);
    }

    [Fact]
    public async Task A_cache_only_answers_for_exactly_the_groups_it_was_built_with()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var repository = fixture.Discography;
        var both = DiscographyGroups.Albums | DiscographyGroups.Singles;

        await repository.SaveAsync("ar-1", DiscographyGroups.Albums, [Album("Debut", "1993-07-05")]);

        // Albums alone was what was fetched, so albums alone is what it can answer for.
        Assert.True(await repository.IsCachedAsync("ar-1", DiscographyGroups.Albums, TimeSpan.FromDays(7)));

        // Asking for singles too can't be served from a listing that never had any.
        Assert.False(await repository.IsCachedAsync("ar-1", both, TimeSpan.FromDays(7)));

        await repository.SaveAsync("ar-2", both, [Album("Debut", "1993-07-05")]);

        // Nor can narrowing be served from a wider listing: nothing on the rows says which group
        // each came from, so the singles couldn't be dropped even if we wanted to.
        Assert.False(await repository.IsCachedAsync("ar-2", DiscographyGroups.Albums, TimeSpan.FromDays(7)));
        Assert.True(await repository.IsCachedAsync("ar-2", both, TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task A_cache_older_than_its_lifetime_is_not_used()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var repository = fixture.Discography;

        await repository.SaveAsync("ar-1", DiscographyGroups.Albums, [Album("Debut", "1993-07-05")]);

        Assert.False(await repository.IsCachedAsync("ar-1", DiscographyGroups.Albums, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_stored_discography_comes_back_marked_with_what_the_library_holds()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        // Kind of Blue is saved in the fixture; Selected Ambient Works only has a liked track.
        await fixture.Discography.SaveAsync("ar-miles", DiscographyGroups.Albums,
        [
            new DiscographyAlbum("al-kob", "Kind of Blue", "1959-08-17", ReleasePrecision.Day,
                null, 2, IsSaved: false, TracksHeld: 0),
            new DiscographyAlbum("al-saw", "Selected Ambient Works", "1992", ReleasePrecision.Year,
                null, 1, IsSaved: false, TracksHeld: 0)
        ]);

        var read = await fixture.Discography.GetAsync("ar-miles", DiscographySort.Name);

        var saved = read.Single(a => a.Id == "al-kob");
        var partly = read.Single(a => a.Id == "al-saw");

        // The stored rows carry no library state of their own; it's joined on at read time.
        Assert.True(saved.IsSaved);
        Assert.Equal(2, saved.TracksHeld);

        Assert.False(partly.IsSaved);
        Assert.True(partly.IsPartiallyHeld);
    }
}
