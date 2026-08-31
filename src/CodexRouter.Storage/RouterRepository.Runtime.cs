namespace CodexRouter.Storage;

public sealed record RuntimeStateValue(string Key, string Value, DateTimeOffset UpdatedAt);

public sealed partial class RouterRepository
{
    public Task SetRuntimeStateAsync(
        string key,
        string value,
        DateTimeOffset? updatedAt = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Runtime-state key cannot be empty.", nameof(key));
        ArgumentNullException.ThrowIfNull(value);
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO runtime_state(key, value, updated_at)
                VALUES ($key, $value, $updated)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$updated", ToDbTime(updatedAt ?? DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, "Failed to write runtime state.", cancellationToken);
    }

    public Task<RuntimeStateValue?> GetRuntimeStateAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Runtime-state key cannot be empty.", nameof(key));
        return ExecuteAsync<RuntimeStateValue?>(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value, updated_at FROM runtime_state WHERE key = $key LIMIT 1;";
            command.Parameters.AddWithValue("$key", key);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            return new RuntimeStateValue(reader.GetString(0), reader.GetString(1), FromDbTime(reader.GetInt64(2)));
        }, "Failed to read runtime state.", cancellationToken);
    }

    public Task<bool> DeleteRuntimeStateAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Runtime-state key cannot be empty.", nameof(key));
        return ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM runtime_state WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }, "Failed to delete runtime state.", cancellationToken);
    }
}
