using CodexRouter.Domain;

namespace CodexRouter.Protocol;

public static class CodexProtocolRequirements
{
    public static readonly IReadOnlySet<string> RequiredMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "initialize",
        "thread/start",
        "thread/resume",
        "thread/fork",
        "thread/list",
        "turn/start",
        "turn/interrupt",
        "account/read",
        "account/login/start",
        "account/rateLimits/read"
    };

    public static readonly IReadOnlySet<string> OptionalMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "thread/read",
        "thread/archive",
        "thread/delete",
        "thread/unsubscribe",
        "turn/steer",
        "account/usage/read",
        "account/rateLimits/updated",
        "account/login/completed",
        "account/updated"
    };
}

public sealed class CompatibilityEvaluator
{
    public CompatibilityReport Evaluate(BinaryIdentity binary, SchemaMetadata stableSchema)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(stableSchema);

        var methodSet = stableSchema.Methods.ToHashSet(StringComparer.Ordinal);
        var missingRequired = CodexProtocolRequirements.RequiredMethods.Where(method => !methodSet.Contains(method)).ToArray();
        var missingOptional = CodexProtocolRequirements.OptionalMethods.Where(method => !methodSet.Contains(method)).ToArray();
        var issues = new List<CompatibilityIssue>();

        foreach (var method in missingRequired)
        {
            issues.Add(new CompatibilityIssue(
                "required-rpc-missing",
                CompatibilityIssueSeverity.Error,
                $"Required Codex AppServer method '{method}' is missing from the stable schema.",
                method));
        }

        foreach (var method in missingOptional)
        {
            issues.Add(new CompatibilityIssue(
                "optional-rpc-missing",
                CompatibilityIssueSeverity.Warning,
                $"Optional Codex AppServer method or notification '{method}' is missing from the stable schema.",
                method));
        }

        var state = missingRequired.Length > 0
            ? CompatibilityState.Incompatible
            : missingOptional.Length > 0
                ? CompatibilityState.Degraded
                : CompatibilityState.Compatible;

        return new CompatibilityReport(
            state,
            binary,
            stableSchema,
            DateTimeOffset.UtcNow,
            issues,
            missingRequired,
            missingOptional);
    }

    public CompatibilityReport FromFailure(
        BinaryIdentity? binary,
        string code,
        string message,
        CompatibilityState state = CompatibilityState.Unknown)
    {
        return new CompatibilityReport(
            state,
            binary,
            null,
            DateTimeOffset.UtcNow,
            new[] { new CompatibilityIssue(code, CompatibilityIssueSeverity.Error, message) },
            CodexProtocolRequirements.RequiredMethods.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            CodexProtocolRequirements.OptionalMethods.OrderBy(static x => x, StringComparer.Ordinal).ToArray());
    }
}

public sealed record CompatibilityProbeOptions(
    CodexBinaryDiscoveryOptions? BinaryDiscovery = null,
    SchemaGenerationOptions? SchemaGeneration = null);

public sealed class CodexCompatibilityProbe
{
    private readonly CodexBinaryDiscovery _binaryDiscovery;
    private readonly CodexSchemaGenerator _schemaGenerator;
    private readonly CompatibilityEvaluator _evaluator;

    public CodexCompatibilityProbe(
        CodexBinaryDiscovery? binaryDiscovery = null,
        CodexSchemaGenerator? schemaGenerator = null,
        CompatibilityEvaluator? evaluator = null)
    {
        _binaryDiscovery = binaryDiscovery ?? new CodexBinaryDiscovery();
        _schemaGenerator = schemaGenerator ?? new CodexSchemaGenerator();
        _evaluator = evaluator ?? new CompatibilityEvaluator();
    }

    public async Task<CompatibilityReport> ProbeAsync(
        CompatibilityProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CompatibilityProbeOptions();
        var discovery = await _binaryDiscovery.DiscoverAsync(options.BinaryDiscovery, cancellationToken).ConfigureAwait(false);
        if (discovery.Binary is null)
        {
            return _evaluator.FromFailure(null, "codex-binary-not-found",
                discovery.Error ?? "Codex binary discovery failed.");
        }

        var schemaOptions = options.SchemaGeneration ?? new SchemaGenerationOptions();
        if (schemaOptions.Flavor != SchemaFlavor.Stable)
        {
            schemaOptions = schemaOptions with { Flavor = SchemaFlavor.Stable };
        }

        var generated = await _schemaGenerator.GenerateAsync(discovery.Binary, schemaOptions, cancellationToken)
            .ConfigureAwait(false);
        if (generated.Metadata is null)
        {
            return _evaluator.FromFailure(discovery.Binary, "schema-generation-failed",
                generated.Error ?? "Stable Codex AppServer schema generation failed.");
        }

        return _evaluator.Evaluate(discovery.Binary, generated.Metadata);
    }
}
