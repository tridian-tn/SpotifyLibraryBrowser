using Microsoft.Data.Sqlite;
using SpotifyLibraryBrowser.Core.Browsing;

namespace SpotifyLibraryBrowser.Core.Data;

/// <summary>
/// The read side of the index. Everything the browser displays comes from here — no browsing
/// operation ever touches the Web API, which is what keeps the columns instant.
/// </summary>
/// <param name="database">The local index</param>
public sealed class BrowseRepository(LibraryDatabase database)
{
    /// <summary>Shown for values with no usable data, such as an album with no release date.</summary>
    public const string UnknownDisplay = "Unknown";

    /// <summary>Lists the distinct values for one browser column.</summary>
    /// <param name="request">The current browser state</param>
    /// <param name="columnIndex">Index of the column to enumerate</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>The column's values, ordered by the criterion's own sort</returns>
    public async Task<IReadOnlyList<BrowseValue>> GetColumnValuesAsync(
        BrowseRequest request,
        int columnIndex,
        CancellationToken cancel = default)
    {
        var query = BrowseQueryBuilder.BuildColumnValues(request, columnIndex);

        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = CreateCommand(connection, query);
        await using var reader = await command.ExecuteReaderAsync(cancel).ConfigureAwait(false);

        var values = new List<BrowseValue>();

        while (await reader.ReadAsync(cancel).ConfigureAwait(false))
        {
            var key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var display = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

            if (string.IsNullOrEmpty(display)) display = UnknownDisplay;

            values.Add(new BrowseValue(key, display, reader.GetInt32(2)));
        }

        return values;
    }

    /// <summary>Lists the tracks matching every column selection and toolbar filter.</summary>
    /// <param name="request">The current browser state</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>The matching tracks, in whichever order the request asked for</returns>
    public async Task<IReadOnlyList<TrackRow>> GetTracksAsync(
        BrowseRequest request,
        CancellationToken cancel = default)
    {
        var query = BrowseQueryBuilder.BuildTrackList(request);

        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = CreateCommand(connection, query);
        await using var reader = await command.ExecuteReaderAsync(cancel).ConfigureAwait(false);

        var tracks = new List<TrackRow>();

        while (await reader.ReadAsync(cancel).ConfigureAwait(false))
        {
            tracks.Add(new TrackRow(
                Id: reader.GetString(0),
                Uri: reader.GetString(1),
                Name: reader.GetString(2),
                DiscNumber: reader.GetInt32(3),
                TrackNumber: reader.GetInt32(4),
                DurationMs: reader.GetInt32(5),
                Explicit: reader.GetBoolean(6),
                IsLiked: reader.GetBoolean(7),
                AlbumId: reader.GetString(8),
                AlbumName: reader.GetString(9),
                AlbumIsSaved: reader.GetBoolean(10),
                Year: reader.IsDBNull(11) ? null : reader.GetString(11),
                ArtistNames: reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                PlaylistPosition: reader.IsDBNull(13) ? null : reader.GetInt32(13)));
        }

        return tracks;
    }

    /// <summary>Counts the tracks matching the current state, for the "All" row and status line.</summary>
    /// <param name="request">The current browser state</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>The number of distinct matching tracks</returns>
    public async Task<int> CountTracksAsync(BrowseRequest request, CancellationToken cancel = default)
    {
        var inner = BrowseQueryBuilder.BuildTrackList(request);
        var sql = $"SELECT COUNT(*) FROM ({inner.Sql})";

        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = CreateCommand(connection, new SqlQuery(sql, inner.Parameters));

        var result = await command.ExecuteScalarAsync(cancel).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    /// <summary>Builds a command from a composed query, binding its parameters.</summary>
    /// <param name="connection">The open connection</param>
    /// <param name="query">The composed SQL and its parameters</param>
    /// <returns>A command ready to execute</returns>
    private static SqliteCommand CreateCommand(SqliteConnection connection, SqlQuery query)
    {
        var command = connection.CreateCommand();
        command.CommandText = query.Sql;

        foreach (var (name, value) in query.Parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
