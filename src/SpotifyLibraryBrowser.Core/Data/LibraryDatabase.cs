using Microsoft.Data.Sqlite;

namespace SpotifyLibraryBrowser.Core.Data;

/// <summary>
/// Owns the local SQLite index: where it lives, how it's opened, and its schema.
/// </summary>
/// <remarks>
/// Connections are opened per operation rather than shared — Microsoft.Data.Sqlite pools them,
/// and WAL lets a background sync write while the UI reads. An in-memory database is the
/// exception: it only exists while a connection is held, so one is kept open for its lifetime.
/// </remarks>
public sealed class LibraryDatabase : IDisposable
{
    private readonly SqliteConnection? _keepAlive;

    /// <summary>Creates a database over the given connection string.</summary>
    /// <param name="connectionString">The SQLite connection string</param>
    /// <param name="keepAlive">Hold a connection open, which shared in-memory databases need</param>
    private LibraryDatabase(string connectionString, bool keepAlive)
    {
        ConnectionString = connectionString;

        if (keepAlive)
        {
            _keepAlive = new SqliteConnection(connectionString);
            _keepAlive.Open();
        }
    }

    /// <summary>The connection string every operation opens against.</summary>
    public string ConnectionString { get; }

    /// <summary>Opens the index at a path on disk, creating the file and folder if needed.</summary>
    /// <param name="path">Full path to the database file</param>
    /// <param name="cancel">Cancels the schema application</param>
    /// <returns>An initialised database</returns>
    public static async Task<LibraryDatabase> OpenAsync(string path, CancellationToken cancel = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default
        }.ToString();

        var database = new LibraryDatabase(connectionString, keepAlive: false);
        await database.InitialiseAsync(cancel).ConfigureAwait(false);
        return database;
    }

    /// <summary>Opens a throwaway in-memory index, for tests.</summary>
    /// <param name="cancel">Cancels the schema application</param>
    /// <returns>An initialised in-memory database</returns>
    public static async Task<LibraryDatabase> OpenInMemoryAsync(CancellationToken cancel = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"lib-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        var database = new LibraryDatabase(connectionString, keepAlive: true);
        await database.InitialiseAsync(cancel).ConfigureAwait(false);
        return database;
    }

    /// <summary>Opens a connection with the per-connection pragmas already applied.</summary>
    /// <param name="cancel">Cancels the open</param>
    /// <returns>An open connection the caller owns and must dispose</returns>
    public async Task<SqliteConnection> ConnectAsync(CancellationToken cancel = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancel).ConfigureAwait(false);

        // Foreign keys are per-connection in SQLite, so they have to be switched on every time.
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);

        return connection;
    }

    /// <summary>Applies the schema and the durable pragmas.</summary>
    /// <param name="cancel">Cancels the work</param>
    private async Task InitialiseAsync(CancellationToken cancel)
    {
        await using var connection = await ConnectAsync(cancel).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // WAL is persistent and meaningless in memory, so only file-backed databases ask for it.
        if (!ConnectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = "PRAGMA journal_mode = WAL;";
            await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
        }

        // Migrations run first: the schema creates an index over is_saved, which can't be built
        // until an older index has actually been given that column.
        await MigrateAsync(connection, cancel).ConfigureAwait(false);

        command.CommandText = Schema.Ddl;
        await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Brings an index created by an earlier build up to the current schema.</summary>
    /// <param name="connection">The open connection</param>
    /// <param name="cancel">Cancels the work</param>
    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken cancel)
    {
        // A brand new database has no tables yet; the schema below creates them already correct.
        if (!await HasTableAsync(connection, "albums", cancel).ConfigureAwait(false)) return;
        if (await HasColumnAsync(connection, "albums", "is_saved", cancel).ConfigureAwait(false)) return;

        await using var command = connection.CreateCommand();
        command.CommandText = Schema.AddIsSavedToAlbums;
        await command.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks whether a table exists.</summary>
    /// <param name="connection">The open connection</param>
    /// <param name="table">The table to look for</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>True when the table is present</returns>
    private static async Task<bool> HasTableAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancel)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table;";
        command.Parameters.AddWithValue("@table", table);

        var result = await command.ExecuteScalarAsync(cancel).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    /// <summary>Checks whether a table already has a column.</summary>
    /// <param name="connection">The open connection</param>
    /// <param name="table">The table to inspect</param>
    /// <param name="column">The column to look for</param>
    /// <param name="cancel">Cancels the query</param>
    /// <returns>True when the column is present</returns>
    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancel)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column;";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        var result = await command.ExecuteScalarAsync(cancel).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _keepAlive?.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
