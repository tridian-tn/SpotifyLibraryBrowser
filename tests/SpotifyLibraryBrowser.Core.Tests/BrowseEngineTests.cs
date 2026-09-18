using SpotifyLibraryBrowser.Core.Browsing;
using SpotifyLibraryBrowser.Core.Data;

namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// Covers the configurable column engine — the part where a wrong join silently gives plausible
/// but incorrect results, so the assertions are on exact contents rather than just counts.
/// </summary>
public sealed class BrowseEngineTests
{
    /// <summary>Builds a request from a list of criteria with no selections made.</summary>
    /// <param name="criteria">The columns, left to right</param>
    /// <returns>A request selecting nothing</returns>
    private static BrowseRequest Columns(params ColumnCriterion[] criteria) =>
        new(criteria.Select(c => new ColumnSelection(c, [])).ToList());

    /// <summary>Builds a request with the saved-albums-only toggle on.</summary>
    /// <param name="criteria">The columns, left to right</param>
    /// <returns>A request restricted to albums in the library</returns>
    private static BrowseRequest SavedOnly(params ColumnCriterion[] criteria) =>
        Columns(criteria) with { SavedAlbumsOnly = true };

    /// <summary>Replaces one column's selection.</summary>
    /// <param name="request">The request to change</param>
    /// <param name="index">The column to select in</param>
    /// <param name="keys">The keys to select</param>
    /// <returns>A request with that column's selection applied</returns>
    private static BrowseRequest Select(BrowseRequest request, int index, params string[] keys)
    {
        var columns = request.Columns.ToList();
        columns[index] = columns[index] with { SelectedKeys = keys };
        return request with { Columns = columns };
    }

    [Fact]
    public async Task Album_artist_column_lists_only_artists_of_saved_albums()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var values = await fixture.Browse.GetColumnValuesAsync(SavedOnly(ColumnCriterion.AlbumArtist), 0);

        // Aphex Twin is only a liked song and Boards of Canada only a playlist track, so neither
        // belongs here however many of their tracks the index holds.
        Assert.Equal(
            ["David Bowie", "Miles Davis", "Various Artists"],
            values.Select(v => v.Display));

        // Bowie counts Warszawa alone: his other track is on an album that was never saved.
        Assert.Equal([1, 2, 2], values.Select(v => v.TrackCount));
    }

    [Fact]
    public async Task Album_column_lists_only_saved_albums()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var values = await fixture.Browse.GetColumnValuesAsync(SavedOnly(ColumnCriterion.Album), 0);

        Assert.Equal(["Jazz Collected", "Kind of Blue", "Low"], values.Select(v => v.Display));
    }

    [Fact]
    public async Task Artist_column_still_covers_songs_whose_album_was_never_saved()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var values = await fixture.Browse.GetColumnValuesAsync(Columns(ColumnCriterion.Artist), 0);

        // Track artist is deliberately unscoped: it's how a loose liked song stays reachable.
        Assert.Equal(
            ["Aphex Twin", "Boards of Canada", "Brian Eno", "David Bowie", "John Coltrane", "Miles Davis"],
            values.Select(v => v.Display));

        Assert.Equal([1, 1, 1, 2, 2, 3], values.Select(v => v.TrackCount));
    }

    [Fact]
    public async Task Compilation_groups_under_its_album_artist_not_its_track_artists()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var request = Select(Columns(ColumnCriterion.AlbumArtist), 0, LibraryFixture.Various.Id);
        var tracks = await fixture.Browse.GetTracksAsync(request);

        // Naima and Milestones are by Coltrane and Miles, but the compilation must keep them together.
        Assert.Equal(["Milestones", "Naima"], tracks.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Collaboration_appears_under_every_credited_artist()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var request = Columns(ColumnCriterion.Artist);

        var eno = await fixture.Browse.GetTracksAsync(Select(request, 0, LibraryFixture.Eno.Id));
        var bowie = await fixture.Browse.GetTracksAsync(Select(request, 0, LibraryFixture.Bowie.Id));

        Assert.Equal(["Warszawa"], eno.Select(t => t.Name));
        Assert.Equal(["Untitled", "Warszawa"], bowie.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Year_column_buckets_undated_albums_last()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var values = await fixture.Browse.GetColumnValuesAsync(Columns(ColumnCriterion.Year), 0);

        // Year isn't scoped to saved albums: it's about when the music came out, so it covers
        // liked songs and playlist tracks as well.
        Assert.Equal(
            ["1959", "1977", "1992", "1998", "2002", BrowseRepository.UnknownDisplay],
            values.Select(v => v.Display));

        Assert.Equal(string.Empty, values[^1].Key);
    }

    [Fact]
    public async Task Decade_column_rounds_down_to_ten_years()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var values = await fixture.Browse.GetColumnValuesAsync(Columns(ColumnCriterion.Decade), 0);

        Assert.Equal(
            ["1950s", "1970s", "1990s", "2000s", BrowseRepository.UnknownDisplay],
            values.Select(v => v.Display));
    }

    [Fact]
    public async Task Year_only_release_precision_still_lands_in_the_right_bucket()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        // Low is stored as "1977" with year precision — no month or day to parse.
        var byYear = await fixture.Browse.GetTracksAsync(Select(Columns(ColumnCriterion.Year), 0, "1977"));
        var byDecade = await fixture.Browse.GetTracksAsync(Select(Columns(ColumnCriterion.Decade), 0, "1970"));

        Assert.Equal(["Warszawa"], byYear.Select(t => t.Name));
        Assert.Equal(["Warszawa"], byDecade.Select(t => t.Name));
    }

    [Fact]
    public async Task Undated_album_is_selectable_through_the_unknown_bucket()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var tracks = await fixture.Browse.GetTracksAsync(Select(Columns(ColumnCriterion.Year), 0, ""));

        Assert.Equal(["Untitled"], tracks.Select(t => t.Name));
    }

    [Fact]
    public async Task Multi_select_widens_the_filter()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var request = Select(SavedOnly(ColumnCriterion.AlbumArtist), 0,
            LibraryFixture.Miles.Id, LibraryFixture.Bowie.Id);

        var tracks = await fixture.Browse.GetTracksAsync(request);

        // "Untitled" is Bowie's too, but its album was never saved, so browsing by album artist
        // doesn't reach it.
        Assert.Equal(
            ["Blue in Green", "So What", "Warszawa"],
            tracks.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Selections_across_columns_narrow_cumulatively()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var request = Columns(ColumnCriterion.AlbumArtist, ColumnCriterion.Album);
        request = Select(request, 0, LibraryFixture.Bowie.Id);
        request = Select(request, 1, "al-low");

        var tracks = await fixture.Browse.GetTracksAsync(request);

        Assert.Equal(["Warszawa"], tracks.Select(t => t.Name));
    }

    [Fact]
    public async Task Empty_selection_means_all_within_the_active_filters()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        // With the saved-albums filter on, "All" means all of the saved albums, so the track
        // list matches what the columns can actually offer.
        var scoped = await fixture.Browse.CountTracksAsync(SavedOnly(ColumnCriterion.AlbumArtist));
        Assert.Equal(5, scoped);

        // With it off, the whole index is in scope.
        var all = await fixture.Browse.CountTracksAsync(Columns(ColumnCriterion.Artist));
        Assert.Equal(8, all);
    }

    [Fact]
    public async Task A_column_is_filtered_by_upstream_columns_only()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var request = SavedOnly(ColumnCriterion.AlbumArtist, ColumnCriterion.Album);
        request = Select(request, 0, LibraryFixture.Bowie.Id);

        var albums = await fixture.Browse.GetColumnValuesAsync(request, 1);
        var albumArtists = await fixture.Browse.GetColumnValuesAsync(request, 0);

        // The Album column narrows to Bowie's saved albums...
        Assert.Equal(["Low"], albums.Select(v => v.Display));

        // ...but the Album Artist column doesn't narrow to its own selection.
        Assert.Equal(3, albumArtists.Count);
    }

    [Fact]
    public async Task Playlist_column_lists_playlists_that_have_tracks()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var values = await fixture.Browse.GetColumnValuesAsync(Columns(ColumnCriterion.Playlist), 0);

        Assert.Equal(["Late Night"], values.Select(v => v.Display));
        Assert.Equal(3, values[0].TrackCount);
    }

    [Fact]
    public async Task Saved_albums_only_governs_playlists_as_well_as_albums()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var playlist = Select(Columns(ColumnCriterion.Playlist), 0, "pl-1");

        // Off, a playlist shows everything in it, including tracks whose album was never saved.
        var everything = await fixture.Browse.GetTracksAsync(playlist);
        Assert.Equal(["Roygbiv", "So What", "Warszawa"], everything.Select(t => t.Name).Order());

        // On, the same playlist narrows to the tracks that are on saved albums.
        var restricted = await fixture.Browse.GetTracksAsync(playlist with { SavedAlbumsOnly = true });
        Assert.Equal(["So What", "Warszawa"], restricted.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Saved_albums_only_is_off_by_default_in_a_request()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        // The toggle has to be asked for: nothing about a criterion turns it on implicitly.
        var request = Columns(ColumnCriterion.Album);
        Assert.False(request.SavedAlbumsOnly);

        var values = await fixture.Browse.GetColumnValuesAsync(request, 0);

        Assert.Contains("Selected Ambient Works", values.Select(v => v.Display));
        Assert.Contains("Geogaddi", values.Select(v => v.Display));
    }

    [Fact]
    public async Task Playlist_criterion_filters_to_membership()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var tracks = await fixture.Browse.GetTracksAsync(
            Select(Columns(ColumnCriterion.Playlist), 0, "pl-1"));

        // Playlists aren't scoped to saved albums, so a playlist-only track still shows.
        Assert.Equal(["Roygbiv", "So What", "Warszawa"], tracks.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Liked_only_filter_restricts_to_liked_songs()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        // With an Album Artist column active the scope still applies, so this is liked tracks
        // that are also on a saved album.
        var scoped = SavedOnly(ColumnCriterion.AlbumArtist) with { LikedOnly = true };
        var scopedTracks = await fixture.Browse.GetTracksAsync(scoped);

        Assert.Equal(["So What", "Warszawa"], scopedTracks.Select(t => t.Name).Order());

        // Browsing by an unscoped criterion reaches every liked song.
        var all = Columns(ColumnCriterion.Artist) with { LikedOnly = true };
        var allTracks = await fixture.Browse.GetTracksAsync(all);

        Assert.Equal(["So What", "Untitled", "Warszawa", "Xtal"], allTracks.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Search_matches_artist_names_without_duplicating_rows()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var request = Columns(ColumnCriterion.AlbumArtist) with { Search = "Coltrane" };
        var tracks = await fixture.Browse.GetTracksAsync(request);

        // Blue in Green credits Coltrane alongside Miles — it must appear once, not twice.
        Assert.Equal(["Blue in Green", "Naima"], tracks.Select(t => t.Name).Order());
        Assert.Equal(tracks.Count, tracks.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public async Task Search_matches_track_and_album_names()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var request = Columns(ColumnCriterion.AlbumArtist) with { Search = "blue" };
        var tracks = await fixture.Browse.GetTracksAsync(request);

        // "Kind of Blue" pulls in both its tracks; "Blue in Green" is one of them already.
        Assert.Equal(["Blue in Green", "So What"], tracks.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Track_rows_list_artists_in_credit_order()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var tracks = await fixture.Browse.GetTracksAsync(Columns(ColumnCriterion.Album));
        var blueInGreen = tracks.Single(t => t.Name == "Blue in Green");

        Assert.Equal("Miles Davis, John Coltrane", blueInGreen.ArtistNames);
    }

    [Fact]
    public async Task Playlist_order_follows_the_playlist_rather_than_the_albums()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var playlist = Select(Columns(ColumnCriterion.Playlist), 0, "pl-1");

        // Album order groups the same three tracks by record, which is the order that loses the
        // point of a playlist: Geogaddi, Kind of Blue, Low.
        var byAlbum = await fixture.Browse.GetTracksAsync(playlist);
        Assert.Equal(["Roygbiv", "So What", "Warszawa"], byAlbum.Select(t => t.Name));
        Assert.All(byAlbum, t => Assert.Null(t.PlaylistPosition));

        var byPlaylist = await fixture.Browse.GetTracksAsync(
            playlist with { Sort = TrackSort.PlaylistOrder });

        Assert.Equal(["So What", "Warszawa", "Roygbiv"], byPlaylist.Select(t => t.Name));
        Assert.Equal([0, 1, 2], byPlaylist.Select(t => t.PlaylistPosition));
    }

    [Fact]
    public async Task Playlist_order_still_honours_the_toolbar_filters()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        var request = Select(Columns(ColumnCriterion.Playlist), 0, "pl-1")
            with { Sort = TrackSort.PlaylistOrder, SavedAlbumsOnly = true };

        var tracks = await fixture.Browse.GetTracksAsync(request);

        // Roygbiv's album was never saved, so it goes — and what's left keeps the playlist's own
        // numbering rather than being renumbered from one.
        Assert.Equal(["So What", "Warszawa"], tracks.Select(t => t.Name));
        Assert.Equal([0, 1], tracks.Select(t => t.PlaylistPosition));
    }

    [Fact]
    public async Task Playlist_order_needs_exactly_one_playlist_to_mean_anything()
    {
        await using var fixture = await LibraryFixture.CreateAsync();
        var asked = Columns(ColumnCriterion.Playlist) with { Sort = TrackSort.PlaylistOrder };

        // Asked for but nothing selected: "All playlists" has no running order of its own.
        Assert.Null(asked.SinglePlaylistId);
        Assert.False(asked.OrdersByPlaylist);

        // Nor does a multi-selection, even though each playlist in it has one.
        var several = Select(asked, 0, "pl-1", "pl-empty");
        Assert.Null(several.SinglePlaylistId);
        Assert.False(several.OrdersByPlaylist);

        // And the query still runs, in album order, rather than failing on a dangling alias.
        var tracks = await fixture.Browse.GetTracksAsync(several);
        Assert.Equal(["Roygbiv", "So What", "Warszawa"], tracks.Select(t => t.Name));

        // Two playlist columns narrowing each other leave an intersection, which has no order either.
        var crossed = Columns(ColumnCriterion.Playlist, ColumnCriterion.Playlist)
            with { Sort = TrackSort.PlaylistOrder };

        Assert.Null(Select(Select(crossed, 0, "pl-1"), 1, "pl-1").SinglePlaylistId);
    }

    [Fact]
    public async Task A_track_a_playlist_holds_twice_is_still_one_row()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        // A playlist is allowed to hold the same track more than once. Listing it once per entry
        // would be faithful to the running order but would break everything that treats a row as a
        // track: the count, a like, an unlike, and starting playback on the row that was clicked.
        await using (var writer = await fixture.Library.BeginWriteAsync())
        {
            await writer.AddPlaylistTrackAsync("pl-1", "tr-sowhat", 3, null);
            await writer.CommitAsync();
        }

        var request = Select(Columns(ColumnCriterion.Playlist), 0, "pl-1")
            with { Sort = TrackSort.PlaylistOrder };

        var tracks = await fixture.Browse.GetTracksAsync(request);

        // One row, at the earlier of its two positions.
        Assert.Equal(["So What", "Warszawa", "Roygbiv"], tracks.Select(t => t.Name));
        Assert.Equal([0, 1, 2], tracks.Select(t => t.PlaylistPosition));

        // And the count agrees with the rows, rather than counting playlist entries.
        Assert.Equal(3, await fixture.Browse.CountTracksAsync(request));
    }

    [Fact]
    public async Task Multi_valued_joins_do_not_inflate_the_track_list()
    {
        await using var fixture = await LibraryFixture.CreateAsync();

        // Artist is a many-to-many join, so an unfiltered Artist column could duplicate tracks
        // credited to more than one person if DISTINCT weren't doing its job.
        var tracks = await fixture.Browse.GetTracksAsync(Columns(ColumnCriterion.Artist));

        Assert.Equal(8, tracks.Count);
        Assert.Equal(8, tracks.Select(t => t.Id).Distinct().Count());
    }
}
