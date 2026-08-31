using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexRouter.Domain;
using CodexRouter.Routing;
using CodexRouter.Rpc;
using CodexRouter.Storage;
using CodexRouter.Workers;
using Microsoft.Data.Sqlite;

namespace CodexRouter.Rpc.Tests;

internal sealed class RpcTestEnvironment : IAsyncDisposable
{
    private RpcTestEnvironment(
        string root,
        StorageDatabase database,
        RouterRepository repository,
        FakeWorkerFactory factory,
        WorkerPool pool,
        RouterCoordinator router)
    {
        Root = root;
        Database = database;
        Repository = repository;
        Factory = factory;
        Pool = pool;
        Router = router;
    }

    public string Root { get; }
    public StorageDatabase Database { get; }
    public RouterRepository Repository { get; }
    public FakeWorkerFactory Factory { get; }
    public WorkerPool Pool { get; }
    public RouterCoordinator Router { get; }

    public RpcMultiplexer CreateMultiplexer(WorkerClientContext? clientContext = null) =>
        new(Repository, Pool, Router, clientContext,
            options: new RpcMultiplexerOptions(RequestTimeout: TimeSpan.FromSeconds(5)),
            threadListOptions: new ThreadListAggregatorOptions(DefaultLimit: 2, MaxLimit: 20, CursorTtl: TimeSpan.FromMinutes(5)));

    public static async Task<RpcTestEnvironment> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-rpc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = new StorageDatabase(new StorageOptions(Path.Combine(root, "router.db")));
        await database.InitializeAsync();
        var repository = new RouterRepository(database);
        await repository.AppendCompatibilityRunAsync(new CompatibilityReport(
            CompatibilityState.Compatible,
            new BinaryIdentity(Path.Combine(root, "codex.exe"), "0.test", new string('f', 64), 1, DateTimeOffset.UtcNow),
            null,
            DateTimeOffset.UtcNow,
            Array.Empty<CompatibilityIssue>(),
            Array.Empty<string>(),
            Array.Empty<string>()));
        var factory = new FakeWorkerFactory();
        var pool = new WorkerPool(factory, new WorkerPoolOptions(
            MaxResidentWorkers: 10,
            IdleTtl: TimeSpan.FromHours(1),
            MaintenanceInterval: TimeSpan.FromHours(1),
            CrashThreshold: 3,
            CrashWindow: TimeSpan.FromMinutes(2),
            QuarantineDuration: TimeSpan.FromMinutes(5)));
        var router = new RouterCoordinator(repository, pool);
        return new RpcTestEnvironment(root, database, repository, factory, pool, router);
    }

    public Task<AccountProfile> AddAccountAsync(string id, int usedPercent, params FakeThread[] threads) =>
        AddAccountCoreAsync(id, usedPercent, quotaFetchedAt: null, threads);

    public Task<AccountProfile> AddAccountAsync(
        string id,
        int usedPercent,
        DateTimeOffset quotaFetchedAt,
        params FakeThread[] threads) =>
        AddAccountCoreAsync(id, usedPercent, quotaFetchedAt, threads);

    private async Task<AccountProfile> AddAccountCoreAsync(
        string id,
        int usedPercent,
        DateTimeOffset? quotaFetchedAt,
        IReadOnlyList<FakeThread> threads)
    {
        var accountId = new AccountId(id);
        var profile = new AccountProfile(accountId, id.ToUpperInvariant(), Path.Combine(Root, "profiles", id),
            $"{id}@example.test", "plus", true, 0);
        Directory.CreateDirectory(profile.CodexHome);
        await Repository.CreateAccountAsync(profile);
        await Repository.AppendQuotaSnapshotAsync(new QuotaSnapshot(accountId, new[]
        {
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Primary, usedPercent,
                TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(2)),
            new QuotaBucket("codex", "Codex", QuotaBucketSlot.Secondary, usedPercent,
                TimeSpan.FromDays(7), DateTimeOffset.UtcNow.AddDays(3))
        }, quotaFetchedAt ?? DateTimeOffset.UtcNow));
        await Repository.AppendHealthEventAsync(new AccountHealth(accountId, AccountHealthState.Healthy, DateTimeOffset.UtcNow));
        var config = Factory.Configure(accountId);
        config.Email = $"{id}@example.test";
        foreach (var thread in threads)
        {
            config.Threads.Add(thread);
            config.KnownThreads.Add(thread.Id);
        }
        return profile;
    }

    public async ValueTask DisposeAsync()
    {
        await Pool.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    internal sealed record FakeThread(string Id, long CreatedAt, long UpdatedAt)
    {
        public JsonElement ToJson() => FakeWorkerConfiguration.Parse(
            $"{{\"id\":\"{Id}\",\"preview\":\"{Id}\",\"modelProvider\":\"openai\",\"createdAt\":{CreatedAt},\"updatedAt\":{UpdatedAt},\"status\":{{\"type\":\"notLoaded\"}},\"path\":null,\"cwd\":\"C:/work\",\"cliVersion\":\"test\",\"source\":\"vscode\",\"agentNickname\":null,\"agentRole\":null,\"gitInfo\":null,\"name\":null,\"turns\":[]}}");
    }

    internal sealed class FakeWorkerFactory : IAppServerWorkerFactory
    {
        private readonly Dictionary<string, FakeWorkerConfiguration> _configs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeWorker> _latest = new(StringComparer.Ordinal);
        private int _sequence;

        public FakeWorkerConfiguration Configure(AccountId accountId)
        {
            if (!_configs.TryGetValue(accountId.Value, out var config))
            {
                config = new FakeWorkerConfiguration(accountId);
                _configs.Add(accountId.Value, config);
            }
            return config;
        }

        public FakeWorker Latest(AccountId accountId) => _latest[accountId.Value];

        public IAppServerWorker Create(AccountProfile profile)
        {
            var worker = new FakeWorker(
                new WorkerId($"{profile.Id.Value}-{Interlocked.Increment(ref _sequence)}"),
                profile.Id,
                Configure(profile.Id));
            _latest[profile.Id.Value] = worker;
            return worker;
        }
    }

    internal sealed class FakeWorkerConfiguration
    {
        private int _threadSequence;
        private int _forkSequence;

        public FakeWorkerConfiguration()
        {
            Email = string.Empty;
            RateLimitsRead = Parse("{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300},\"secondary\":null,\"planType\":\"plus\"}}");
        }

        public FakeWorkerConfiguration(AccountId accountId)
        {
            AccountId = accountId;
            Email = $"{accountId.Value}@example.test";
        }

        public AccountId AccountId { get; }
        public string Email { get; set; }
        public List<FakeThread> Threads { get; } = new();
        public HashSet<string> KnownThreads { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<string> RetryableCalls { get; } = new();
        public ConcurrentQueue<(WorkerServerRequest Request, object? Result, RpcErrorPayload? Error)> ServerResponses { get; } = new();
        public bool OverloadThreadStart { get; set; }
        public JsonElement RateLimitsRead { get; set; }

        public string NextThreadId() => $"thread-{AccountId.Value}-{Interlocked.Increment(ref _threadSequence)}";
        public string NextForkId() => $"fork-{AccountId.Value}-{Interlocked.Increment(ref _forkSequence)}";

        public static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    internal sealed class FakeWorker : IAppServerWorker
    {
        private readonly FakeWorkerConfiguration _config;

        public FakeWorker(WorkerId workerId, AccountId accountId, FakeWorkerConfiguration config)
        {
            WorkerId = workerId;
            AccountId = accountId;
            _config = config;
        }

        public WorkerId WorkerId { get; }
        public AccountId AccountId { get; }
        public WorkerState State { get; private set; } = WorkerState.Stopped;
        public int? ProcessId => IsAlive ? 2222 : null;
        public bool IsAlive => State is WorkerState.Ready or WorkerState.Busy or WorkerState.Draining;
        public event EventHandler<WorkerStateChange>? StateChanged;
        public event EventHandler<WorkerNotification>? NotificationReceived;
        public event EventHandler<WorkerServerRequest>? ServerRequestReceived;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Change(WorkerState.Ready);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Change(WorkerState.Stopped);
            return Task.CompletedTask;
        }

        public Task<JsonElement> SendRequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _config.Calls.Enqueue(method);
            var p = ToElement(parameters);
            switch (method)
            {
                case "thread/start":
                    if (_config.OverloadThreadStart)
                    {
                        return Task.FromException<JsonElement>(new AppServerRpcException(-32001, "overloaded"));
                    }
                    var newId = _config.NextThreadId();
                    _config.KnownThreads.Add(newId);
                    return Task.FromResult(ThreadResponse(newId));

                case "thread/fork":
                    var forkId = _config.NextForkId();
                    _config.KnownThreads.Add(forkId);
                    return Task.FromResult(ThreadResponse(forkId));

                case "thread/read":
                case "thread/resume":
                    var threadId = p.GetProperty("threadId").GetString()!;
                    if (!_config.KnownThreads.Contains(threadId))
                    {
                        return Task.FromException<JsonElement>(new AppServerRpcException(-32602, "thread not found"));
                    }
                    return Task.FromResult(ThreadResponse(threadId));

                case "thread/list":
                    return Task.FromResult(ThreadList(p));

                case "turn/start":
                    return Task.FromResult(FakeWorkerConfiguration.Parse("{\"turn\":{\"id\":\"turn-1\",\"items\":[],\"status\":\"completed\",\"error\":null}}"));

                case "turn/steer":
                case "turn/interrupt":
                case "thread/archive":
                case "thread/unsubscribe":
                    return Task.FromResult(FakeWorkerConfiguration.Parse("{}"));

                case "thread/delete":
                    if (p.TryGetProperty("threadId", out var deleteId)) _config.KnownThreads.Remove(deleteId.GetString()!);
                    return Task.FromResult(FakeWorkerConfiguration.Parse("{}"));

                case "account/read":
                    return Task.FromResult(FakeWorkerConfiguration.Parse(
                        $"{{\"account\":{{\"type\":\"chatgpt\",\"email\":\"{_config.Email}\",\"planType\":\"plus\"}},\"requiresOpenaiAuth\":true}}"));

                case "account/rateLimits/read":
                    return Task.FromResult(_config.RateLimitsRead.Clone());

                default:
                    return Task.FromResult(FakeWorkerConfiguration.Parse("{}"));
            }
        }

        public async Task<JsonElement> SendRetryableRequestAsync(
            string method,
            object? parameters,
            DateTimeOffset deadline,
            bool retryable,
            RetryPolicy? policy = null,
            CancellationToken cancellationToken = default)
        {
            _config.RetryableCalls.Enqueue(method);
            return await SendRequestAsync(method, parameters, deadline - DateTimeOffset.UtcNow, cancellationToken);
        }

        public Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            _config.Calls.Enqueue($"notification:{method}");
            return Task.CompletedTask;
        }

        public Task RespondToServerRequestAsync(
            WorkerServerRequest request,
            object? result = null,
            RpcErrorPayload? error = null,
            CancellationToken cancellationToken = default)
        {
            _config.ServerResponses.Enqueue((request, result, error));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<WorkerNotification> ReadNotificationsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<WorkerServerRequest> ReadServerRequestsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public IReadOnlyList<string> GetRecentStderr() => Array.Empty<string>();

        public void EmitNotification(string method, string paramsJson)
        {
            NotificationReceived?.Invoke(this, new WorkerNotification(
                WorkerId, AccountId, method, FakeWorkerConfiguration.Parse(paramsJson), DateTimeOffset.UtcNow));
        }

        public void EmitServerRequest(string nativeId, string method, string paramsJson)
        {
            using var idDocument = JsonDocument.Parse(JsonSerializer.Serialize(nativeId));
            ServerRequestReceived?.Invoke(this, new WorkerServerRequest(
                WorkerId, AccountId, idDocument.RootElement.Clone(), method,
                FakeWorkerConfiguration.Parse(paramsJson), DateTimeOffset.UtcNow));
        }

        public ValueTask DisposeAsync()
        {
            Change(WorkerState.Stopped);
            return ValueTask.CompletedTask;
        }

        private JsonElement ThreadList(JsonElement p)
        {
            var limit = p.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var parsedLimit) ? parsedLimit : 20;
            var offset = p.TryGetProperty("cursor", out var cursorElement) && cursorElement.ValueKind == JsonValueKind.String && int.TryParse(cursorElement.GetString(), out var parsedOffset)
                ? parsedOffset
                : 0;
            var ordered = _config.Threads.OrderByDescending(static thread => thread.UpdatedAt).ToArray();
            var page = ordered.Skip(offset).Take(limit).ToArray();
            var next = offset + page.Length < ordered.Length ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("data");
                writer.WriteStartArray();
                foreach (var thread in page) thread.ToJson().WriteTo(writer);
                writer.WriteEndArray();
                writer.WritePropertyName("nextCursor");
                if (next is null) writer.WriteNullValue(); else writer.WriteStringValue(next);
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }

        private static JsonElement ThreadResponse(string id) => FakeWorkerConfiguration.Parse(
            $"{{\"thread\":{{\"id\":\"{id}\",\"preview\":\"\",\"modelProvider\":\"openai\",\"createdAt\":1,\"updatedAt\":1,\"status\":{{\"type\":\"notLoaded\"}},\"path\":null,\"cwd\":\"C:/work\",\"cliVersion\":\"test\",\"source\":\"vscode\",\"agentNickname\":null,\"agentRole\":null,\"gitInfo\":null,\"name\":null,\"turns\":[]}}}}");

        private static JsonElement ToElement(object? value)
        {
            if (value is JsonElement element) return element.Clone();
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.Clone();
        }

        private void Change(WorkerState next)
        {
            var previous = State;
            State = next;
            StateChanged?.Invoke(this, new WorkerStateChange(WorkerId, AccountId, previous, next, null, DateTimeOffset.UtcNow));
        }
    }

    internal sealed class ChannelLineReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Send(string line) => _lines.Writer.TryWrite(line);
        public void Complete() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    internal sealed class ChannelLineWriter : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> _all = new();
        public override Encoding Encoding => Encoding.UTF8;
        public IReadOnlyList<string> All => _all.ToArray();

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            var line = buffer.ToString();
            _all.Enqueue(line);
            return _lines.Writer.WriteAsync(line, cancellationToken).AsTask();
        }

        public async Task<string> ReadNextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _lines.Reader.ReadAsync(cts.Token);
        }
    }
}
