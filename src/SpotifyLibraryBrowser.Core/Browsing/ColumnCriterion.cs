namespace SpotifyLibraryBrowser.Core.Browsing;

/// <summary>
/// A way of grouping the library into a browser column. Every criterion here is built on
/// data that's current in the Web API — nothing deprecated.
/// </summary>
public enum ColumnCriterion
{
    /// <summary>The artists credited on the track itself.</summary>
    Artist = 0,

    /// <summary>The artists credited on the album, which is what keeps compilations sane.</summary>
    AlbumArtist = 1,

    /// <summary>The album.</summary>
    Album = 2,

    /// <summary>The album's release year.</summary>
    Year = 3,

    /// <summary>The decade the album was released in.</summary>
    Decade = 4,

    /// <summary>The playlists a track belongs to.</summary>
    Playlist = 5
}

/// <summary>
/// Describes how one criterion projects and joins in SQL. Every expression here is a
/// compile-time constant from <see cref="CriterionRegistry"/> — user input only ever
/// arrives as a bound parameter, never as SQL text.
/// </summary>
/// <param name="Criterion">The criterion being described</param>
/// <param name="DisplayName">The label shown in the column's criterion picker</param>
/// <param name="KeyExpr">SQL expression yielding the stable grouping key</param>
/// <param name="DisplayExpr">SQL expression yielding the text shown in the column</param>
/// <param name="SortExpr">SQL ORDER BY fragment for the column's values</param>
/// <param name="JoinSql">Joins this criterion needs, or empty when it reads straight off the base tables</param>
public sealed record CriterionDescriptor(
    ColumnCriterion Criterion,
    string DisplayName,
    string KeyExpr,
    string DisplayExpr,
    string SortExpr,
    string JoinSql)
{
    /// <summary>Whether this criterion brings extra joins, and so can multiply track rows.</summary>
    public bool IsMultiValued => JoinSql.Length > 0;
}

/// <summary>The fixed set of criterion descriptors, keyed by criterion.</summary>
public static class CriterionRegistry
{
    /// <summary>SQL yielding the four-digit release year, or NULL when there's no usable date.</summary>
    private const string YearRaw = "substr(al.release_date, 1, 4)";

    /// <summary>Empty-string-normalised year key, so a missing date groups rather than vanishing.</summary>
    private const string YearKey = $"COALESCE({YearRaw}, '')";

    private const string DecadeKey =
        $"CASE WHEN {YearRaw} IS NULL OR {YearRaw} = '' THEN '' " +
        $"ELSE CAST((CAST({YearRaw} AS INTEGER) / 10) * 10 AS TEXT) END";

    private static readonly Dictionary<ColumnCriterion, CriterionDescriptor> Descriptors = new()
    {
        [ColumnCriterion.Artist] = new CriterionDescriptor(
            ColumnCriterion.Artist,
            "Artist",
            KeyExpr: "tar.id",
            DisplayExpr: "tar.name",
            SortExpr: "tar.name COLLATE NOCASE",
            JoinSql: "JOIN track_artists ta ON ta.track_id = t.id " +
                     "JOIN artists tar ON tar.id = ta.artist_id"),

        [ColumnCriterion.AlbumArtist] = new CriterionDescriptor(
            ColumnCriterion.AlbumArtist,
            "Album Artist",
            KeyExpr: "aar.id",
            DisplayExpr: "aar.name",
            SortExpr: "aar.name COLLATE NOCASE",
            JoinSql: "JOIN album_artists aa ON aa.album_id = t.album_id " +
                     "JOIN artists aar ON aar.id = aa.artist_id"),

        [ColumnCriterion.Album] = new CriterionDescriptor(
            ColumnCriterion.Album,
            "Album",
            KeyExpr: "al.id",
            DisplayExpr: "al.name",
            SortExpr: "al.name COLLATE NOCASE",
            JoinSql: ""),

        [ColumnCriterion.Year] = new CriterionDescriptor(
            ColumnCriterion.Year,
            "Year",
            KeyExpr: YearKey,
            DisplayExpr: YearKey,
            // Undated albums sort to the end rather than leading the column.
            SortExpr: $"CASE WHEN {YearKey} = '' THEN 1 ELSE 0 END, {YearKey}",
            JoinSql: ""),

        [ColumnCriterion.Decade] = new CriterionDescriptor(
            ColumnCriterion.Decade,
            "Decade",
            KeyExpr: DecadeKey,
            DisplayExpr: $"CASE WHEN ({DecadeKey}) = '' THEN '' ELSE ({DecadeKey}) || 's' END",
            SortExpr: $"CASE WHEN ({DecadeKey}) = '' THEN 1 ELSE 0 END, ({DecadeKey})",
            JoinSql: ""),

        [ColumnCriterion.Playlist] = new CriterionDescriptor(
            ColumnCriterion.Playlist,
            "Playlist",
            KeyExpr: "pl.id",
            DisplayExpr: "pl.name",
            SortExpr: "pl.name COLLATE NOCASE",
            JoinSql: "JOIN playlist_tracks pt ON pt.track_id = t.id " +
                     "JOIN playlists pl ON pl.id = pt.playlist_id")
    };

    /// <summary>Looks up the descriptor for a criterion.</summary>
    /// <param name="criterion">The criterion to describe</param>
    /// <returns>The descriptor holding its SQL projections and joins</returns>
    public static CriterionDescriptor For(ColumnCriterion criterion) => Descriptors[criterion];

    /// <summary>Every criterion, in the order they're offered in the picker.</summary>
    public static IReadOnlyList<CriterionDescriptor> All { get; } =
        Enum.GetValues<ColumnCriterion>().Select(For).ToList();
}
