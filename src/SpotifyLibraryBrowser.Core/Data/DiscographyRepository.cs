using Microsoft.Data.Sqlite;
using SpotifyLibraryBrowser.Core.Discography;
using SpotifyLibraryBrowser.Core.Model;

namespace SpotifyLibraryBrowser.Core.Data;

/// <summary>Stores and reads the discographies fetched for the panel.</summary>
/// <param name="database">The local index</param>
public sealed class DiscographyRepository(LibraryDatabase database)
{
    /// <summary>
    /// Says whether a usable cached discography exists for an artist.
    /// </summary>
    /// <remarks>
    /// A cache built with a narrower set of groups than is being asked for doesn't count: it would
    /// answer a question about singles with a list that never had any in it.
    /// </remarks>
    /// <param name="artistId">The artist to check</param>
    /// <param name="groups">The groups being asked for</param>
    /// <param name="maxAge">How old a cache may be and still be used</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>True when the stored discography can answer without refetching</returns>
    public async Task<bool> IsCachedAsync(
        string artistId,
        DiscographyGroups groups,
        TimeSpan maxAge,
        CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fetched_at, groups FROM discography_fetches WHERE artist_id = @id;";
        command.Parameters.AddWithValue("@id", artistId);

        await using var reader = await command.ExecuteReaderAsync(cancel).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancel).ConfigureAwait(false)) return false;

        if (!DateTimeOffset.TryParse(reader.GetString(0), out var fetchedAt)) return false;
        if (DateTimeOffset.UtcNow - fetchedAt > maxAge) return false;

        // Exactly the same groups, not merely a set that covers them. A stored listing is the
        // answer to one particular question, and nothing on the rows records which group each came
        // from — album_type is deprecated — so a wider listing can't be narrowed after the fact.
        // Treating it as usable made changing the filter downwards appear to do nothing at all.
        var cached = (DiscographyGroups)reader.GetInt32(1);
        return groups == cached;
    }

    /// <summary>Replaces an artist's stored discography.</summary>
    /// <param name="artistId">The artist the releases belong to</param>
    /// <param name="groups">The groups they were fetched with</param>
    /// <param name="albums">The releases to store</param>
    /// <param name="cancel">Cancels the write</param>
    public async Task SaveAsync(
        string artistId,
        DiscographyGroups groups,
        IReadOnlyList<DiscographyAlbum> albums,
        CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancel).ConfigureAwait(false);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM discography_albums WHERE artist_id = @id;";
            clear.Parameters.AddWithValue("@id", artistId);
            await clear.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO discography_albums
                    (artist_id, album_id, name, release_date, release_precision, image_url, total_tracks)
                VALUES (@artist, @album, @name, @release, @precision, @image, @total)
                ON CONFLICT(artist_id, album_id) DO UPDATE SET
                    name = excluded.name,
                    release_date = excluded.release_date,
                    release_precision = excluded.release_precision,
                    image_url = excluded.image_url,
                    total_tracks = excluded.total_tracks;
                """;

            foreach (var album in albums)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("@artist", artistId);
                insert.Parameters.AddWithValue("@album", album.Id);
                insert.Parameters.AddWithValue("@name", album.Name);
                insert.Parameters.AddWithValue("@release", (object?)album.ReleaseDate ?? DBNull.Value);
                insert.Parameters.AddWithValue("@precision", (int)album.ReleasePrecision);
                insert.Parameters.AddWithValue("@image", (object?)album.ImageUrl ?? DBNull.Value);
                insert.Parameters.AddWithValue("@total", album.TotalTracks);

                await insert.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
            }
        }

        await using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = """
                INSERT INTO discography_fetches (artist_id, fetched_at, groups)
                VALUES (@id, @at, @groups)
                ON CONFLICT(artist_id) DO UPDATE SET
                    fetched_at = excluded.fetched_at,
                    groups = excluded.groups;
                """;

            mark.Parameters.AddWithValue("@id", artistId);
            mark.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            mark.Parameters.AddWithValue("@groups", (int)groups);

            await mark.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an artist's stored discography, marked up with what the library already holds.
    /// </summary>
    /// <param name="artistId">The artist whose releases to read</param>
    /// <param name="sort">The order to return them in</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>The artist's releases, sorted</returns>
    public async Task<IReadOnlyList<DiscographyAlbum>> GetAsync(
        string artistId,
        DiscographySort sort,
        CancellationToken cancel = default)
    {
        await using var connection = await database.ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // The left join is what turns a bare listing into something useful: which of these you
        // already own, and which you merely have a track or two from.
        command.CommandText = """
            SELECT d.album_id, d.name, d.release_date, d.release_precision, d.image_url,
                   d.total_tracks,
                   COALESCE(al.is_saved, 0) AS is_saved,
                   (SELECT COUNT(*) FROM tracks t WHERE t.album_id = d.album_id) AS tracks_held
            FROM discography_albums d
            LEFT JOIN albums al ON al.id = d.album_id
            WHERE d.artist_id = @id;
            """;

        command.Parameters.AddWithValue("@id", artistId);

        var albums = new List<DiscographyAlbum>();
        await using var reader = await command.ExecuteReaderAsync(cancel).ConfigureAwait(false);

        while (await reader.ReadAsync(cancel).ConfigureAwait(false))
        {
            albums.Add(new DiscographyAlbum(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                ReleaseDate: reader.IsDBNull(2) ? null : reader.GetString(2),
                ReleasePrecision: (ReleasePrecision)reader.GetInt32(3),
                ImageUrl: reader.IsDBNull(4) ? null : reader.GetString(4),
                TotalTracks: reader.GetInt32(5),
                IsSaved: reader.GetBoolean(6),
                TracksHeld: reader.GetInt32(7)));
        }

        return Sort(albums, sort);
    }

    /// <summary>
    /// Orders a discography.
    /// </summary>
    /// <remarks>
    /// Done here rather than in SQL: a discography is tens of rows, and a fixed set of comparers
    /// is easier to read and to test than an ORDER BY assembled from an enum.
    /// </remarks>
    /// <param name="albums">The releases to order</param>
    /// <param name="sort">The order wanted</param>
    /// <returns>The releases, sorted</returns>
    public static IReadOnlyList<DiscographyAlbum> Sort(
        IEnumerable<DiscographyAlbum> albums,
        DiscographySort sort) => sort switch
    {
        // Undated releases sort to the end either way round, rather than pretending to be ancient.
        DiscographySort.NewestFirst => albums
            .OrderBy(a => a.ReleaseDate is null)
            .ThenByDescending(a => a.ReleaseDate, StringComparer.Ordinal)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),

        DiscographySort.OldestFirst => albums
            .OrderBy(a => a.ReleaseDate is null)
            .ThenBy(a => a.ReleaseDate, StringComparer.Ordinal)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),

        DiscographySort.Name => albums
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),

        DiscographySort.TrackCount => albums
            .OrderByDescending(a => a.TotalTracks)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),

        DiscographySort.InLibraryFirst => albums
            .OrderByDescending(a => a.IsSaved)
            .ThenByDescending(a => a.TracksHeld)
            .ThenBy(a => a.ReleaseDate is null)
            .ThenByDescending(a => a.ReleaseDate, StringComparer.Ordinal)
            .ToList(),

        _ => albums.ToList()
    };
}
