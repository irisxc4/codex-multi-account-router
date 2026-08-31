using CodexRouter.Domain;
using CodexRouter.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexRouter.Storage.Tests;

public sealed class StorageResilienceTests
{
    [Fact]
    public async Task Corrupted_database_is_detected_and_classified()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "router.db");
        try
        {
            await File.WriteAllTextAsync(path, "this is not sqlite");
            var database = new StorageDatabase(new StorageOptions(path));

            var exception = await Assert.ThrowsAsync<StorageCorruptionException>(() => database.InitializeAsync());
            Assert.NotNull(exception.InnerException);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Locked_writer_becomes_storage_busy_exception()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "router.db");
        try
        {
            var database = new StorageDatabase(new StorageOptions(path, TimeSpan.FromMilliseconds(75)));
            await database.InitializeAsync();
            var repository = new RouterRepository(database);
            var account = new AccountProfile(new AccountId("a"), "A", Path.Combine(root, "profiles", "a"));
            await repository.CreateAccountAsync(account);

            await using var blocker = await database.OpenConnectionAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO health_events(account_id, state, reason, cooldown_until, created_at)
                    VALUES ('a', 'healthy', 'blocker', NULL, 1);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<StorageBusyException>(() => repository.AppendHealthEventAsync(
                new AccountHealth(account.Id, AccountHealthState.Healthy, DateTimeOffset.UtcNow, "contender")));

            await transaction.RollbackAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_migration_rolls_back_its_schema_and_version_record()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "migration.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();

            var broken = new SqliteMigration(
                99,
                "intentionally-broken",
                """
                CREATE TABLE transient_test(id INTEGER PRIMARY KEY);
                INSERT INTO definitely_missing_table(id) VALUES (1);
                """);

            await Assert.ThrowsAsync<SqliteException>(() =>
                SqliteMigrationRunner.ApplyAsync(connection, new[] { broken }));

            Assert.False(await TableExistsAsync(connection, "transient_test"));

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 99;";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Configured_connections_use_WAL_and_foreign_keys()
    {
        var root = NewRoot();
        try
        {
            var database = new StorageDatabase(new StorageOptions(Path.Combine(root, "router.db")));
            await database.InitializeAsync();
            await using var connection = await database.OpenConnectionAsync();

            await using var journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", Convert.ToString(await journal.ExecuteScalarAsync())?.ToLowerInvariant());

            await using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, Convert.ToInt64(await foreignKeys.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Schema_has_no_credential_or_secret_columns()
    {
        var root = NewRoot();
        try
        {
            var database = new StorageDatabase(new StorageOptions(Path.Combine(root, "router.db")));
            await database.InitializeAsync();
            await using var connection = await database.OpenConnectionAsync();

            await using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            await using var tableReader = await tables.ExecuteReaderAsync();
            var tableNames = new List<string>();
            while (await tableReader.ReadAsync())
            {
                tableNames.Add(tableReader.GetString(0));
            }

            var forbidden = new[]
            {
                "access_token",
                "refresh_token",
                "password",
                "cookie",
                "api_key",
                "apikey",
                "secret"
            };

            foreach (var table in tableNames)
            {
                await using var columns = connection.CreateCommand();
                columns.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
                await using var columnReader = await columns.ExecuteReaderAsync();
                while (await columnReader.ReadAsync())
                {
                    var column = columnReader.GetString(1).ToLowerInvariant();
                    Assert.DoesNotContain(forbidden, value => column.Contains(value, StringComparison.Ordinal));
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Migration_checksum_drift_is_rejected()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "drift.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await SqliteMigrationRunner.ApplyAsync(connection, new[]
            {
                new SqliteMigration(1, "sample", "CREATE TABLE sample(id INTEGER PRIMARY KEY);")
            });

            var error = await Assert.ThrowsAsync<StorageException>(() =>
                SqliteMigrationRunner.ApplyAsync(connection, new[]
                {
                    new SqliteMigration(1, "sample", "CREATE TABLE sample_changed(id INTEGER PRIMARY KEY);")
                }));

            Assert.Contains("schema drift", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-storage-resilience-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
