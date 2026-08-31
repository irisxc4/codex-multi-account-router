using Microsoft.Data.Sqlite;

namespace CodexRouter.Storage;

public sealed partial class RouterRepository
{
    private readonly StorageDatabase _database;

    public RouterRepository(StorageDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public StorageDatabase Database => _database;

    private async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> operation,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await operation(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw StorageErrors.Translate(ex, failureMessage);
        }
    }

    private async Task ExecuteAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await operation(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            throw StorageErrors.Translate(ex, failureMessage);
        }
    }

    private static long ToDbTime(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
    private static DateTimeOffset FromDbTime(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static bool? ReadNullableBoolean(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
}
