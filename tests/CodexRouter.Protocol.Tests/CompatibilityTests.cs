using CodexRouter.Domain;
using CodexRouter.Protocol;
using System.Text.Json;
using Xunit;

namespace CodexRouter.Protocol.Tests;

public sealed class CompatibilityTests
{
    [Fact]
    public async Task Explicit_missing_binary_fails_closed()
    {
        var discovery = new CodexBinaryDiscovery(new FakeProcessRunner(_ =>
            Task.FromResult(new ProcessResult(0, "codex-cli fake", string.Empty, false))));

        var result = await discovery.DiscoverAsync(new CodexBinaryDiscoveryOptions(
            ExplicitPath: Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")));

        Assert.False(result.Succeeded);
        Assert.Contains("invalid", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Executable_that_does_not_identify_as_codex_is_rejected()
    {
        var path = CreateTempCandidate();
        try
        {
            var discovery = new CodexBinaryDiscovery(new FakeProcessRunner(_ =>
                Task.FromResult(new ProcessResult(0, "some-other-cli 1.2.3", string.Empty, false))));

            var result = await discovery.DiscoverAsync(new CodexBinaryDiscoveryOptions(ExplicitPath: path));

            Assert.False(result.Succeeded);
            Assert.Contains("did not identify", result.Attempts.Single().Failure!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Version_probe_timeout_is_reported_without_throwing()
    {
        var path = CreateTempCandidate();
        try
        {
            var discovery = new CodexBinaryDiscovery(new FakeProcessRunner(_ =>
                Task.FromResult(new ProcessResult(null, string.Empty, string.Empty, true))));

            var result = await discovery.DiscoverAsync(new CodexBinaryDiscoveryOptions(ExplicitPath: path));

            Assert.False(result.Succeeded);
            Assert.Contains("timed out", result.Attempts.Single().Failure!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Schema_generation_failure_is_reported_without_partial_cache()
    {
        var binaryPath = CreateTempCandidate();
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"codex-router-schema-failure-{Guid.NewGuid():N}");
        try
        {
            var binary = new BinaryIdentity(binaryPath, "0.test", new string('b', 64), 1, DateTimeOffset.UtcNow);
            var generator = new CodexSchemaGenerator(new FakeProcessRunner(_ =>
                Task.FromResult(new ProcessResult(17, string.Empty, "generator exploded", false))));

            var result = await generator.GenerateAsync(binary, new SchemaGenerationOptions(CacheRoot: cacheRoot));

            Assert.False(result.Succeeded);
            Assert.Contains("17", result.Error!);
            Assert.False(Directory.Exists(Path.Combine(cacheRoot, binary.Sha256, "stable")));
        }
        finally
        {
            File.Delete(binaryPath);
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Missing_required_rpc_is_incompatible()
    {
        var identity = FakeIdentity();
        var methods = CodexProtocolRequirements.RequiredMethods.Where(method => method != "thread/start").ToArray();
        var schema = new SchemaMetadata(SchemaFlavor.Stable, "x", identity.Sha256, identity.Version,
            DateTimeOffset.UtcNow, 1, methods);

        var report = new CompatibilityEvaluator().Evaluate(identity, schema);

        Assert.Equal(CompatibilityState.Incompatible, report.State);
        Assert.False(report.RoutingAllowed);
        Assert.Contains("thread/start", report.MissingRequiredMethods);
    }

    [Fact]
    public void Missing_only_optional_rpc_is_degraded_but_routing_safe()
    {
        var identity = FakeIdentity();
        var schema = new SchemaMetadata(SchemaFlavor.Stable, "x", identity.Sha256, identity.Version,
            DateTimeOffset.UtcNow, 1, CodexProtocolRequirements.RequiredMethods.ToArray());

        var report = new CompatibilityEvaluator().Evaluate(identity, schema);

        Assert.Equal(CompatibilityState.Degraded, report.State);
        Assert.True(report.RoutingAllowed);
        Assert.Empty(report.MissingRequiredMethods);
        Assert.NotEmpty(report.MissingOptionalMethods);
    }

    [Fact]
    public async Task Method_registry_extracts_const_and_enum_shapes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-router-schema-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "sample.json");
        try
        {
            await File.WriteAllTextAsync(file, """
            {
              "oneOf": [
                { "properties": { "method": { "const": "thread/start" } } },
                { "properties": { "method": { "enum": ["turn/start", "turn/interrupt"] } } },
                { "properties": { "title": { "const": "not/a/method because it is not under method" } } }
              ]
            }
            """);

            var methods = await new SchemaMethodRegistry().ExtractMethodsAsync(new[] { file });

            Assert.Equal(new[] { "thread/start", "turn/interrupt", "turn/start" }, methods);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Compatibility_report_is_json_serializable()
    {
        var identity = FakeIdentity();
        var schema = new SchemaMetadata(SchemaFlavor.Stable, "x", identity.Sha256, identity.Version,
            DateTimeOffset.UtcNow, 1, CodexProtocolRequirements.RequiredMethods.ToArray());
        var report = new CompatibilityEvaluator().Evaluate(identity, schema);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTrip = JsonSerializer.Deserialize<CompatibilityReport>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(roundTrip);
        Assert.Equal(report.State, roundTrip!.State);
        Assert.Equal(report.Binary!.Sha256, roundTrip.Binary!.Sha256);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_local_codex_binary_generates_stable_schema_and_passes_required_rpc_gate()
    {
        var discovery = await new CodexBinaryDiscovery().DiscoverAsync();
        Assert.True(discovery.Succeeded, discovery.Error);
        Assert.NotNull(discovery.Binary);
        Assert.StartsWith("0.", discovery.Binary!.Version, StringComparison.Ordinal);
        Assert.Equal(64, discovery.Binary.Sha256.Length);

        var cacheRoot = Path.Combine(Path.GetTempPath(), $"codex-router-real-schema-{Guid.NewGuid():N}");
        try
        {
            var generator = new CodexSchemaGenerator();
            var generated = await generator.GenerateAsync(discovery.Binary,
                new SchemaGenerationOptions(CacheRoot: cacheRoot, Timeout: TimeSpan.FromSeconds(45)));

            Assert.True(generated.Succeeded, generated.Error);
            Assert.NotNull(generated.Metadata);
            Assert.True(generated.Metadata!.SchemaFileCount > 0);

            var report = new CompatibilityEvaluator().Evaluate(discovery.Binary, generated.Metadata);
            Assert.Empty(report.MissingRequiredMethods);
            Assert.True(report.RoutingAllowed,
                $"Compatibility state: {report.State}; missing: {string.Join(", ", report.MissingRequiredMethods)}");

            var cached = await generator.GenerateAsync(discovery.Binary,
                new SchemaGenerationOptions(CacheRoot: cacheRoot, Timeout: TimeSpan.FromSeconds(45)));
            Assert.True(cached.FromCache);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    private static BinaryIdentity FakeIdentity() =>
        new(Path.Combine(Path.GetTempPath(), "codex.exe"), "0.148.0-alpha.9", new string('a', 64), 1, DateTimeOffset.UtcNow);

    private static string CreateTempCandidate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-candidate-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, new byte[] { 0 });
        return path;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRequest, Task<ProcessResult>> _handler;

        public FakeProcessRunner(Func<ProcessRequest, Task<ProcessResult>> handler) => _handler = handler;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
            _handler(request);
    }
}
