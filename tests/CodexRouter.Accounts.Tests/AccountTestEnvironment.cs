using System.Text.Json;
using CodexRouter.Accounts;
using CodexRouter.Domain;
using CodexRouter.Storage;
using CodexRouter.Workers;
using Microsoft.Data.Sqlite;

namespace CodexRouter.Accounts.Tests;

internal sealed class AccountTestEnvironment : IAsyncDisposable
{
    private AccountTestEnvironment(
        string root,
        StorageDatabase database,
        RouterRepository repository,
        ProfileMaterializer materializer,
        SharedTemplate template,
        FakeWorkerFactory factory,
        WorkerPool pool,
        FakeUriLauncher launcher,
        AccountService service)
    {
        Root = root;
        Database = database;
        Repository = repository;
        Materializer = materializer;
        Template = template;
        Factory = factory;
        Pool = pool;
        Launcher = launcher;
        Service = service;
    }

    public string Root { get; }
    public StorageDatabase Database { get; }
    public RouterRepository Repository { get; }
    public ProfileMaterializer Materializer { get; }
    public SharedTemplate Template { get; }
    public FakeWorkerFactory Factory { get; }
    public WorkerPool Pool { get; }
    public FakeUriLauncher Launcher { get; }
    public AccountService Service { get; }

    public static async Task<AccountTestEnvironment> CreateAsync(AccountServiceOptions? serviceOptions = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-account-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = new StorageDatabase(new StorageOptions(Path.Combine(root, "router.db")));
        await database.InitializeAsync();
        var repository = new RouterRepository(database);

        var source = Path.Combine(root, "source-codex");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), """
            model = "gpt-5.6-codex"
            approval_policy = "on-request"
            """);
        var materializer = new ProfileMaterializer(new ProfileLayout(Path.Combine(root, "router-data")));
        var template = await materializer.ImportSharedTemplateAsync(source);

        var factory = new FakeWorkerFactory();
        var pool = new WorkerPool(factory, new WorkerPoolOptions(
            MaxResidentWorkers: 10,
            IdleTtl: TimeSpan.FromHours(1),
            MaintenanceInterval: TimeSpan.FromHours(1),
            CrashThreshold: 3,
            CrashWindow: TimeSpan.FromMinutes(2),
            QuarantineDuration: TimeSpan.FromMinutes(5)));
        var launcher = new FakeUriLauncher();
        var service = new AccountService(repository, pool, materializer, uriLauncher: launcher,
            options: serviceOptions ?? new AccountServiceOptions(
                LoginTimeout: TimeSpan.FromSeconds(5),
                QuotaStaleAfter: TimeSpan.FromMinutes(5)));
        return new AccountTestEnvironment(root, database, repository, materializer, template, factory, pool, launcher, service);
    }

    public async Task<AccountProfile> CreateAccountAsync(string id, string? alias = null)
    {
        return await Service.CreateAccountProfileAsync(alias ?? id.ToUpperInvariant(), Template, new AccountId(id));
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        await Pool.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    internal sealed class FakeUriLauncher : IExternalUriLauncher
    {
        public List<Uri> Opened { get; } = new();
        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(uri);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeWorkerFactory : IAppServerWorkerFactory
    {
        private int _sequence;
        private readonly Dictionary<string, FakeWorkerConfiguration> _configurations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeWorker> _latest = new(StringComparer.Ordinal);

        public FakeWorkerConfiguration Configure(AccountId accountId)
        {
            if (!_configurations.TryGetValue(accountId.Value, out var configuration))
            {
                configuration = new FakeWorkerConfiguration(accountId);
                _configurations.Add(accountId.Value, configuration);
            }
            return configuration;
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
        public FakeWorkerConfiguration(AccountId accountId)
        {
            AccountId = accountId;
            AccountRead = Parse("""
                {"account":{"type":"chatgpt","email":"default@example.com","planType":"plus"},"requiresOpenaiAuth":true}
                """);
            RateLimitsRead = Parse("""
                {"rateLimits":{"limitId":"codex","limitName":"Codex","primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":1786845600},"secondary":{"usedPercent":30,"windowDurationMins":10080,"resetsAt":1787443200},"planType":"plus","rateLimitReachedType":null}}
                """);
            UsageRead = Parse("""
                {"summary":{"lifetimeTokens":1000,"peakDailyTokens":500,"longestRunningTurnSec":50,"currentStreakDays":2,"longestStreakDays":4},"dailyUsageBuckets":[{"startDate":"2026-08-16","tokens":100}]}
                """);
        }

        public AccountId AccountId { get; }
        public JsonElement AccountRead { get; set; }
        public JsonElement RateLimitsRead { get; set; }
        public JsonElement UsageRead { get; set; }
        public TaskCompletionSource<bool>? RateLimitsReadGate { get; set; }
        public bool UsageUnsupported { get; set; }
        public bool LogoutFails { get; set; }
        public List<(string Method, object? Parameters)> Calls { get; } = new();

        public static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    internal sealed class FakeWorker : IAppServerWorker
    {
        private readonly FakeWorkerConfiguration _configuration;

        public FakeWorker(WorkerId workerId, AccountId accountId, FakeWorkerConfiguration configuration)
        {
            WorkerId = workerId;
            AccountId = accountId;
            _configuration = configuration;
        }

        public WorkerId WorkerId { get; }
        public AccountId AccountId { get; }
        public WorkerState State { get; private set; } = WorkerState.Stopped;
        public int? ProcessId => IsAlive ? 1234 : null;
        public bool IsAlive => State is WorkerState.Ready or WorkerState.Busy or WorkerState.Draining;
        public event EventHandler<WorkerStateChange>? StateChanged;
        public event EventHandler<WorkerNotification>? NotificationReceived;
        public event EventHandler<WorkerServerRequest>? ServerRequestReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChangeState(WorkerState.Ready);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChangeState(WorkerState.Stopped);
            return Task.CompletedTask;
        }

        public Task<JsonElement> SendRequestAsync(
            string method,
            object? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _configuration.Calls.Add((method, parameters));
            if (method == "account/rateLimits/read" && _configuration.RateLimitsReadGate is { } rateLimitsGate)
            {
                return rateLimitsGate.Task.ContinueWith(
                    _ => _configuration.RateLimitsRead.Clone(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            return method switch
            {
                "account/login/start" => Task.FromResult(FakeWorkerConfiguration.Parse(
                    $"{{\"type\":\"chatgpt\",\"loginId\":\"login-{AccountId.Value}\",\"authUrl\":\"https://auth.example.test/{AccountId.Value}\"}}")),
                "account/login/cancel" => Task.FromResult(FakeWorkerConfiguration.Parse("{\"status\":\"canceled\"}")),
                "account/read" => Task.FromResult(_configuration.AccountRead.Clone()),
                "account/rateLimits/read" => Task.FromResult(_configuration.RateLimitsRead.Clone()),
                "account/usage/read" when _configuration.UsageUnsupported => Task.FromException<JsonElement>(new AppServerRpcException(-32601, "method not found")),
                "account/usage/read" => Task.FromResult(_configuration.UsageRead.Clone()),
                "account/logout" when _configuration.LogoutFails => Task.FromException<JsonElement>(new AppServerRpcException(-32603, "logout failed")),
                "account/logout" => Task.FromResult(FakeWorkerConfiguration.Parse("{}")),
                _ => Task.FromException<JsonElement>(new AppServerRpcException(-32601, $"unknown method {method}"))
            };
        }

        public Task<JsonElement> SendRetryableRequestAsync(string method, object? parameters, DateTimeOffset deadline, bool retryable, RetryPolicy? policy = null, CancellationToken cancellationToken = default) =>
            SendRequestAsync(method, parameters, deadline - DateTimeOffset.UtcNow, cancellationToken);

        public Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RespondToServerRequestAsync(WorkerServerRequest request, object? result = null, RpcErrorPayload? error = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

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

        public void Emit(string method, string paramsJson)
        {
            var parameters = FakeWorkerConfiguration.Parse(paramsJson);
            NotificationReceived?.Invoke(this, new WorkerNotification(
                WorkerId, AccountId, method, parameters, DateTimeOffset.UtcNow));
        }

        public ValueTask DisposeAsync()
        {
            ChangeState(WorkerState.Stopped);
            return ValueTask.CompletedTask;
        }

        private void ChangeState(WorkerState next)
        {
            var previous = State;
            State = next;
            StateChanged?.Invoke(this, new WorkerStateChange(WorkerId, AccountId, previous, next, null, DateTimeOffset.UtcNow));
        }
    }
}
