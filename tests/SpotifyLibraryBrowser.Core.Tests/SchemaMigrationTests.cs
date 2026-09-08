using Microsoft.Data.Sqlite;
using SpotifyLibraryBrowser.Core.Data;
using SpotifyLibraryBrowser.Core.Model;

namespace SpotifyLibraryBrowser.Core.Tests;

/// <summary>
/// Covers upgrading an index that an earlier build created. <c>CREATE TABLE IF NOT EXISTS</c>
/// leaves an existing table untouched, so a new column only arrives if the migration puts it there.
/// </summary>
public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"slb-migrate-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Reopening_an_index_without_is_saved_adds_and_backfills_the_column()
    {
        // Build an index, then take the column away again to stand in for a database written
        // before is_saved existed. added_at is what the backfill has to work from.
        using (var database = await LibraryDatabase.OpenAsync(_path))
        {
            var repository = new LibraryRepository(database);
            await using var writer = await repository.BeginWriteAsync();

            await writer.UpsertAlbumAsync(
                new Album("al-saved", "Saved", "1990", ReleasePrecision.Year, null, 1,
                    DateTimeOffset.Parse("2024-01-01T00:00:00Z"), IsSaved: true),
                [new Artist("ar-1", "Someone")]);

            await writer.UpsertAlbumAsync(
                new Album("al-incidental", "Incidental", "1991", ReleasePrecision.Year, null, 1,
                    null, IsSaved: false),
                [new Artist("ar-2", "Someone Else")]);

            await writer.CommitAsync();
        }

        SqliteConnection.ClearAllPools();
        await DropIsSavedAsync();

        // Reopening has to restore the column and work out which albums were saved.
        using (var reopened = await LibraryDatabase.OpenAsync(_path))
        {
            var counts = await new LibraryRepository(reopened).GetCountsAsync();

            Assert.Equal(2, counts.Albums);
            Assert.Equal(1, counts.SavedAlbums);
        }
    }

    [Fact]
    public async Task Opening_an_up_to_date_index_twice_is_harmless()
    {
        using (var database = await LibraryDatabase.OpenAsync(_path))
        {
            Assert.NotNull(database);
        }

        SqliteConnection.ClearAllPools();

        // The migration must not run a second time and fail on the column already being there.
        using var again = await LibraryDatabase.OpenAsync(_path);
        var counts = await new LibraryRepository(again).GetCountsAsync();

        Assert.Equal(0, counts.Albums);
    }

    /// <summary>Removes the column so the next open has to migrate.</summary>
    private async Task DropIsSavedAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        // The index goes too: a database written before the column existed wouldn't have had it.
        command.CommandText = """
            DROP INDEX IF EXISTS ix_albums_saved;
            ALTER TABLE albums DROP COLUMN is_saved;
            """;

        await command.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
