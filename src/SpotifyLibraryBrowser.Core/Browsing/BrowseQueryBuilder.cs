using System.Text;

namespace SpotifyLibraryBrowser.Core.Browsing;

/// <summary>One value in a browser column, with the number of tracks sitting under it.</summary>
/// <param name="Key">The stable grouping key, empty for the "unknown" bucket</param>
/// <param name="Display">The text shown in the column</param>
/// <param name="TrackCount">How many distinct tracks this value covers</param>
public sealed record BrowseValue(string Key, string Display, int TrackCount);

/// <summary>A track as shown in the track list, flattened for display.</summary>
/// <remarks>
/// <c>AlbumIsSaved</c> rides along so the track list can offer the right verb for the album
/// without a second query: an album already in the library is one to remove, and one that only
/// turned up because a track on it was liked is one to save.
/// </remarks>
public sealed record TrackRow(
    string Id,
    string Uri,
    string Name,
    string ArtistNames,
    string AlbumId,
    string AlbumName,
    string? Year,
    int DiscNumber,
    int TrackNumber,
    int DurationMs,
    bool Explicit,
    bool IsLiked,
    bool AlbumIsSaved);

/// <summary>The criterion a column groups by, plus whatever the user has selected in it.</summary>
/// <param name="Criterion">What this column groups by</param>
/// <param name="SelectedKeys">Selected keys; empty means "All", which filters nothing</param>
public sealed record ColumnSelection(ColumnCriterion Criterion, IReadOnlyList<string> SelectedKeys)
{
    /// <summary>Whether this column narrows the query at all.</summary>
    public bool Filters => SelectedKeys.Count > 0;
}

/// <summary>The whole state of the browser: its columns, their selections, and the toolbar filters.</summary>
/// <param name="Columns">The active columns, left to right</param>
/// <param name="LikedOnly">Restrict to tracks in Liked Songs</param>
/// <param name="SavedAlbumsOnly">
/// Restrict to tracks on albums that are in the library, as opposed to albums reached only
/// through a liked song or a playlist entry. Applies to every column and to the track list, so
/// it governs the whole browser rather than any one criterion
/// </param>
/// <param name="Search">Free-text match over track, album and artist names</param>
public sealed record BrowseRequest(
    IReadOnlyList<ColumnSelection> Columns,
    bool LikedOnly = false,
    bool SavedAlbumsOnly = false,
    string? Search = null);

/// <summary>A composed SQL statement and the parameters it expects.</summary>
public sealed record SqlQuery(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Composes the browser's SQL. Columns are user-configurable, so the queries are built from
/// <see cref="CriterionRegistry"/> descriptors rather than hardcoding an artist/album/track path.
/// </summary>
public static class BrowseQueryBuilder
{
    private const string BaseFrom = "FROM tracks t JOIN albums al ON al.id = t.album_id";

    /// <summary>
    /// Builds the query listing the distinct values for one column.
    /// </summary>
    /// <remarks>
    /// Only the columns to the left of <paramref name="columnIndex"/> filter it — a column's own
    /// selection doesn't narrow its own contents, and columns to its right are downstream.
    /// </remarks>
    /// <param name="request">The current browser state</param>
    /// <param name="columnIndex">Index of the column whose values are wanted</param>
    /// <returns>The SQL and parameters listing that column's values</returns>
    public static SqlQuery BuildColumnValues(BrowseRequest request, int columnIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(columnIndex, request.Columns.Count);

        var target = CriterionRegistry.For(request.Columns[columnIndex].Criterion);
        var upstream = request.Columns.Take(columnIndex).ToList();

        var parameters = new Dictionary<string, object?>();
        var sql = new StringBuilder();

        sql.Append("SELECT ").Append(target.KeyExpr).Append(" AS k, ")
           .Append(target.DisplayExpr).Append(" AS d, COUNT(DISTINCT t.id) AS n ")
           .Append(BaseFrom).Append(' ');

        AppendJoins(sql, CollectCriteria(upstream, target.Criterion));
        AppendWhere(sql, parameters, upstream, request);

        sql.Append(" GROUP BY k, d ORDER BY ").Append(target.SortExpr);

        return new SqlQuery(sql.ToString(), parameters);
    }

    /// <summary>
    /// Builds the query for the track list, filtered by every column's selection.
    /// </summary>
    /// <param name="request">The current browser state</param>
    /// <returns>The SQL and parameters listing the matching tracks</returns>
    public static SqlQuery BuildTrackList(BrowseRequest request)
    {
        var filtering = request.Columns.Where(c => c.Filters).ToList();

        var parameters = new Dictionary<string, object?>();
        var sql = new StringBuilder();

        // The artist names come from an ordered inner select — group_concat doesn't promise
        // ordering on its own, and credit order matters on collaborations.
        sql.Append("SELECT DISTINCT t.id, t.uri, t.name, t.disc_number, t.track_number, ")
           .Append("t.duration_ms, t.explicit, t.is_liked, al.id AS album_id, al.name AS album_name, ")
           .Append("al.is_saved AS album_is_saved, substr(al.release_date, 1, 4) AS year, ")
           .Append("(SELECT group_concat(n, ', ') FROM (")
           .Append("SELECT a2.name AS n FROM track_artists ta2 JOIN artists a2 ON a2.id = ta2.artist_id ")
           .Append("WHERE ta2.track_id = t.id ORDER BY ta2.position)) AS artist_names ")
           .Append(BaseFrom).Append(' ');

        AppendJoins(sql, CollectCriteria(request.Columns, null));
        AppendWhere(sql, parameters, filtering, request);

        sql.Append(" ORDER BY al.name COLLATE NOCASE, t.disc_number, t.track_number");

        return new SqlQuery(sql.ToString(), parameters);
    }

    /// <summary>
    /// Works out which criteria the query has to join in: those doing the filtering, plus the
    /// column being enumerated. Anything else is left out so the query stays lean.
    /// </summary>
    /// <param name="columns">Columns that contribute filters</param>
    /// <param name="target">The column being enumerated, if any</param>
    /// <returns>The distinct criteria needing joins, in a stable order</returns>
    private static IReadOnlyList<ColumnCriterion> CollectCriteria(
        IEnumerable<ColumnSelection> columns,
        ColumnCriterion? target)
    {
        var needed = new List<ColumnCriterion>();

        if (target is { } t) needed.Add(t);

        foreach (var column in columns)
        {
            if (column.Filters && !needed.Contains(column.Criterion)) needed.Add(column.Criterion);
        }

        return needed;
    }

    /// <summary>Appends the join clauses for the given criteria, skipping ones that need none.</summary>
    /// <param name="sql">The statement being built</param>
    /// <param name="criteria">The criteria whose joins are required</param>
    private static void AppendJoins(StringBuilder sql, IReadOnlyList<ColumnCriterion> criteria)
    {
        foreach (var criterion in criteria)
        {
            var descriptor = CriterionRegistry.For(criterion);
            if (!descriptor.IsMultiValued) continue;

            sql.Append(descriptor.JoinSql).Append(' ');
        }
    }

    /// <summary>
    /// Appends the WHERE clause: one IN list per filtering column, plus the liked and search filters.
    /// </summary>
    /// <param name="sql">The statement being built</param>
    /// <param name="parameters">Parameter bag to populate</param>
    /// <param name="filtering">Columns contributing an IN filter</param>
    /// <param name="request">The current browser state, for the toolbar filters</param>
    private static void AppendWhere(
        StringBuilder sql,
        Dictionary<string, object?> parameters,
        IReadOnlyList<ColumnSelection> filtering,
        BrowseRequest request)
    {
        sql.Append("WHERE 1 = 1");

        for (var i = 0; i < filtering.Count; i++)
        {
            var column = filtering[i];
            if (!column.Filters) continue;

            var descriptor = CriterionRegistry.For(column.Criterion);
            var names = new List<string>(column.SelectedKeys.Count);

            for (var j = 0; j < column.SelectedKeys.Count; j++)
            {
                var name = $"@c{i}_{j}";
                names.Add(name);
                parameters[name] = column.SelectedKeys[j];
            }

            sql.Append(" AND ").Append(descriptor.KeyExpr)
               .Append(" IN (").Append(string.Join(", ", names)).Append(')');
        }

        if (request.LikedOnly) sql.Append(" AND t.is_liked = 1");

        // One condition governing the whole browser, rather than something particular criteria
        // impose: it has to narrow a playlist's contents the same way it narrows an album list.
        if (request.SavedAlbumsOnly) sql.Append(" AND al.is_saved = 1");

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            // EXISTS rather than another join, so searching can't duplicate track rows.
            sql.Append(" AND (t.name LIKE @q OR al.name LIKE @q OR EXISTS (")
               .Append("SELECT 1 FROM track_artists sa JOIN artists sar ON sar.id = sa.artist_id ")
               .Append("WHERE sa.track_id = t.id AND sar.name LIKE @q))");

            parameters["@q"] = $"%{request.Search.Trim()}%";
        }
    }
}
