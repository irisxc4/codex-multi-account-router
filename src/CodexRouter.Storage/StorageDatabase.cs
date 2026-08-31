using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CodexRouter.Storage;

public sealed record StorageOptions(
    string DatabasePath,
    TimeSpan? BusyTimeout = null)
{
    public TimeSpan EffectiveBusyTimeout => BusyTimeout ?? TimeSpan.FromSeconds(5);

    public string FullDatabasePath => Path.GetFullPath(DatabasePath);
}

public class StorageException : Exception
{
    public StorageException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class StorageBusyException : StorageException
{
    public StorageBusyException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class StorageCorruptionException : StorageException
{
    public StorageCorruptionException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class StorageDatabase
{
    private readonly StorageOptions _options;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);

    public StorageDatabase(StorageOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException("Database path cannot be empty.", nameof(options));
        }
        if (options.EffectiveBusyTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Busy timeout cannot be negative.");
        }
    }

    public string DatabasePath => _options.FullDatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await SqliteMigrationRunner.ApplyAsync(connection, StorageMigrations.All, cancellationToken).ConfigureAwait(false);
            await EnsureIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw StorageErrors.Translate(ex, "Failed to initialize router storage.");
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw StorageErrors.Translate(ex, $"Failed to open SQLite database '{DatabasePath}'.");
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> IntegrityCheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch (SqliteException ex)
        {
            throw StorageErrors.Translate(ex, "SQLite integrity check failed.");
        }
    }

    private async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Clamp((long)_options.EffectiveBusyTimeout.TotalMilliseconds, 0, int.MaxValue);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = {timeoutMs};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageCorruptionException($"SQLite quick_check returned '{result ?? "<null>"}'.",
                new InvalidDataException("SQLite database integrity check failed."));
        }
    }
}

internal sealed record SqliteMigration(int Version, string Name, string Sql)
{
    public string Checksum => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql))).ToLowerInvariant();
}

internal static class SqliteMigrationRunner
{
    public static async Task ApplyAsync(
        SqliteConnection connection,
        IReadOnlyList<SqliteMigration> migrations,
        CancellationToken cancellationToken = default)
    {
        await using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    checksum TEXT NOT NULL,
                    applied_at INTEGER NOT NULL
                );
                """;
            await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var migration in migrations.OrderBy(static migration => migration.Version))
        {
            var existing = await ReadAppliedMigrationAsync(connection, migration.Version, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.Value.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.Value.Name, migration.Name, StringComparison.Ordinal))
                {
                    throw new StorageException(
                        $"Migration {migration.Version} was previously applied with different content. Refusing schema drift.");
                }
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = migration.Sql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var record = connection.CreateCommand())
                {
                    record.Transaction = (SqliteTransaction)transaction;
                    record.CommandText = """
                        INSERT INTO schema_migrations(version, name, checksum, applied_at)
                        VALUES ($version, $name, $checksum, $applied_at);
                        """;
                    record.Parameters.AddWithValue("$version", migration.Version);
                    record.Parameters.AddWithValue("$name", migration.Name);
                    record.Parameters.AddWithValue("$checksum", migration.Checksum);
                    record.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<(string Name, string Checksum)?> ReadAppliedMigrationAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, checksum FROM schema_migrations WHERE version = $version;";
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }
}

internal static class StorageErrors
{
    public static StorageException Translate(SqliteException exception, string message)
    {
        return exception.SqliteErrorCode switch
        {
            5 or 6 => new StorageBusyException(message, exception),
            11 or 26 => new StorageCorruptionException(message, exception),
            _ => new StorageException(message, exception)
        };
    }
}
