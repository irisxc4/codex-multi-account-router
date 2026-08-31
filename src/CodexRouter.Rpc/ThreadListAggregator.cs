using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexRouter.Domain;
using CodexRouter.Storage;

namespace CodexRouter.Rpc;

public sealed record ThreadListAggregatorOptions(
    int DefaultLimit = 20,
    int MaxLimit = 100,
    TimeSpan? CursorTtl = null,
    int MaxCursorStates = 128,
    int MaxPagesPerWorkerPerCursor = 1000)
{
    public TimeSpan EffectiveCursorTtl => CursorTtl ?? TimeSpan.FromMinutes(10);
}

public sealed class InvalidCompositeCursorException : Exception
{
    public InvalidCompositeCursorException(string message) : base(message) { }
}

public sealed class ThreadListAggregator
{
    private readonly RouterRepository _repository;
    private readonly RpcWorkerAccess _workerAccess;
    private readonly ThreadListAggregatorOptions _options;
    private readonly ConcurrentDictionary<string, CursorState> _cursors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cursorCreationGate = new(1, 1);

    public ThreadListAggregator(
        RouterRepository repository,
        RpcWorkerAccess workerAccess,
        ThreadListAggregatorOptions? options = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workerAccess = workerAccess ?? throw new ArgumentNullException(nameof(workerAccess));
        _options = options ?? new ThreadListAggregatorOptions();
        if (_options.DefaultLimit is < 1 || _options.MaxLimit < _options.DefaultLimit || _options.MaxCursorStates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public async Task<JsonElement> ListAsync(JsonElement parameters, CancellationToken cancellationToken = default)
    {
        if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new ArgumentException("thread/list params must be an object.", nameof(parameters));
        }

        CleanupExpired();
        var limit = ReadLimit(parameters);
        var fingerprint = Fingerprint(parameters);
        var cursor = ReadCursor(parameters);
        CursorState state;
        if (cursor is null)
        {
            state = await CreateStateAsync(parameters, fingerprint, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!_cursors.TryGetValue(cursor, out state!))
            {
                throw new InvalidCompositeCursorException("Composite thread-list cursor is unknown or expired.");
            }
            if (!string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidCompositeCursorException("thread/list filters changed while reusing a composite cursor.");
            }
        }

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state.LastAccessedAt = DateTimeOffset.UtcNow;
            var output = new List<JsonElement>(limit);
            while (output.Count < limit)
            {
                foreach (var workerState in state.Workers)
                {
                    if (workerState.Buffer.Count == 0 && !workerState.Exhausted)
                    {
                        await FetchNextPageAsync(state, workerState, limit, cancellationToken).ConfigureAwait(false);
                    }
                }

                var candidates = state.Workers.Where(static worker => worker.Buffer.Count > 0).ToArray();
                if (candidates.Length == 0)
                {
                    break;
                }

                var winner = candidates
                    .OrderByDescending(worker => SortValue(worker.Buffer.Peek(), state.SortKey))
                    .ThenBy(worker => ThreadIdValue(worker.Buffer.Peek()), StringComparer.Ordinal)
                    .ThenBy(worker => worker.AccountId.Value, StringComparer.Ordinal)
                    .First();
                var item = winner.Buffer.Dequeue();
                await PersistDiscoveredOwnershipAsync(item, winner.AccountId, winner.LastWorkerId, cancellationToken)
                    .ConfigureAwait(false);
                output.Add(item);
            }

            var hasMore = state.Workers.Any(static worker => worker.Buffer.Count > 0 || !worker.Exhausted);
            string? nextCursor = null;
            if (hasMore)
            {
                nextCursor = state.Id;
                _cursors[state.Id] = state;
            }
            else
            {
                _cursors.TryRemove(state.Id, out _);
            }

            return CreateResponse(output, nextCursor);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task<CursorState> CreateStateAsync(
        JsonElement parameters,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await _cursorCreationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupExpired();
            if (_cursors.Count >= _options.MaxCursorStates)
            {
                var oldest = _cursors.Values.OrderBy(static state => state.LastAccessedAt).FirstOrDefault();
                if (oldest is not null)
                {
                    _cursors.TryRemove(oldest.Id, out _);
                }
            }

            var accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
            var id = $"cr-{Guid.NewGuid():N}";
            var state = new CursorState(
                id,
                fingerprint,
                ReadSortKey(parameters),
                CloneBaseParameters(parameters),
                accounts.Select(account => new WorkerCursorState(account.Profile.Id)).ToArray(),
                DateTimeOffset.UtcNow);
            _cursors[id] = state;
            return state;
        }
        finally
        {
            _cursorCreationGate.Release();
        }
    }

    private async Task FetchNextPageAsync(
        CursorState state,
        WorkerCursorState workerState,
        int clientLimit,
        CancellationToken cancellationToken)
    {
        if (++workerState.FetchCount > _options.MaxPagesPerWorkerPerCursor)
        {
            throw new InvalidCompositeCursorException($"Worker pagination exceeded {_options.MaxPagesPerWorkerPerCursor} pages.");
        }

        var pageLimit = Math.Clamp(clientLimit, 1, _options.MaxLimit);
        var requestParams = BuildWorkerListParams(state.BaseParameters, workerState.NextCursor, pageLimit);
        await using var lease = await _workerAccess.AcquireAccountAsync(workerState.AccountId, cancellationToken).ConfigureAwait(false);
        workerState.LastWorkerId = lease.Worker.WorkerId;
        var response = await lease.Worker.SendRequestAsync(
            "thread/list",
            requestParams,
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Worker {workerState.AccountId} returned an invalid thread/list response.");
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                workerState.Buffer.Enqueue(item.Clone());
            }
        }

        var previousCursor = workerState.NextCursor;
        workerState.NextCursor = response.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null;
        workerState.Exhausted = workerState.NextCursor is null;
        if (workerState.Buffer.Count == 0 &&
            previousCursor is not null &&
            string.Equals(previousCursor, workerState.NextCursor, StringComparison.Ordinal))
        {
            throw new InvalidCompositeCursorException($"Worker {workerState.AccountId} repeated an empty page cursor.");
        }
    }

    private async Task PersistDiscoveredOwnershipAsync(
        JsonElement thread,
        AccountId accountId,
        WorkerId? workerId,
        CancellationToken cancellationToken)
    {
        var id = ThreadIdValue(thread);
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }
        var threadId = new ThreadId(id);
        var existing = await _repository.GetThreadRouteAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.AccountId != accountId)
            {
                throw new ThreadOwnershipCollisionException(threadId, new[] { existing.AccountId, accountId });
            }
            return;
        }

        var route = new ThreadRoute(
            threadId,
            accountId,
            workerId ?? new WorkerId($"discovered-{accountId.Value}"),
            RouteReason.Recovery,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        try
        {
            await _repository.InsertThreadRouteAsync(route, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException)
        {
            existing = await _repository.GetThreadRouteAsync(threadId, cancellationToken).ConfigureAwait(false);
            if (existing is null || existing.AccountId != accountId)
            {
                throw;
            }
        }
    }

    private int ReadLimit(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("limit", out var limitElement) &&
            limitElement.ValueKind == JsonValueKind.Number &&
            limitElement.TryGetInt32(out var limit))
        {
            return Math.Clamp(limit, 1, _options.MaxLimit);
        }
        return _options.DefaultLimit;
    }

    private static string? ReadCursor(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("cursor", out var cursor) &&
        cursor.ValueKind == JsonValueKind.String
            ? cursor.GetString()
            : null;

    private static string ReadSortKey(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("sortKey", out var sort) &&
        sort.ValueKind == JsonValueKind.String &&
        string.Equals(sort.GetString(), "created_at", StringComparison.Ordinal)
            ? "created_at"
            : "updated_at";

    private static Dictionary<string, JsonElement> CloneBaseParameters(JsonElement parameters)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        foreach (var property in parameters.EnumerateObject())
        {
            if (property.Name is "cursor" or "limit")
            {
                continue;
            }
            result[property.Name] = property.Value.Clone();
        }
        return result;
    }

    private static Dictionary<string, object?> BuildWorkerListParams(
        IReadOnlyDictionary<string, JsonElement> baseParameters,
        string? cursor,
        int limit)
    {
        var result = baseParameters.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value, StringComparer.Ordinal);
        result["cursor"] = cursor;
        result["limit"] = limit;
        return result;
    }

    private static long SortValue(JsonElement thread, string sortKey)
    {
        var property = sortKey == "created_at" ? "createdAt" : "updatedAt";
        return thread.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
            ? seconds
            : long.MinValue;
    }

    private static string ThreadIdValue(JsonElement thread) =>
        thread.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty
            : string.Empty;

    private static JsonElement CreateResponse(IReadOnlyList<JsonElement> data, string? nextCursor)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("data");
            writer.WriteStartArray();
            foreach (var item in data)
            {
                item.WriteTo(writer);
            }
            writer.WriteEndArray();
            writer.WritePropertyName("nextCursor");
            if (nextCursor is null) writer.WriteNullValue(); else writer.WriteStringValue(nextCursor);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static string Fingerprint(JsonElement parameters)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(parameters, writer, skipCursor: true);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer, bool skipCursor)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().Where(property => !(skipCursor && property.Name == "cursor")).OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer, skipCursor: false);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer, skipCursor: false);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private void CleanupExpired()
    {
        var threshold = DateTimeOffset.UtcNow - _options.EffectiveCursorTtl;
        foreach (var pair in _cursors)
        {
            if (pair.Value.LastAccessedAt < threshold)
            {
                _cursors.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class CursorState
    {
        public CursorState(
            string id,
            string fingerprint,
            string sortKey,
            IReadOnlyDictionary<string, JsonElement> baseParameters,
            IReadOnlyList<WorkerCursorState> workers,
            DateTimeOffset lastAccessedAt)
        {
            Id = id;
            Fingerprint = fingerprint;
            SortKey = sortKey;
            BaseParameters = baseParameters;
            Workers = workers;
            LastAccessedAt = lastAccessedAt;
        }

        public string Id { get; }
        public string Fingerprint { get; }
        public string SortKey { get; }
        public IReadOnlyDictionary<string, JsonElement> BaseParameters { get; }
        public IReadOnlyList<WorkerCursorState> Workers { get; }
        public DateTimeOffset LastAccessedAt { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class WorkerCursorState
    {
        public WorkerCursorState(AccountId accountId) => AccountId = accountId;
        public AccountId AccountId { get; }
        public Queue<JsonElement> Buffer { get; } = new();
        public string? NextCursor { get; set; }
        public bool Exhausted { get; set; }
        public int FetchCount { get; set; }
        public WorkerId? LastWorkerId { get; set; }
    }
}
