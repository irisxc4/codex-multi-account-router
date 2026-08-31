using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexRouter.Domain;
using Tomlyn;
using Tomlyn.Model;

namespace CodexRouter.Workers;

public sealed record ProfileLayout(string Root)
{
    public string Root { get; } = Path.GetFullPath(Root);
    public string SharedRoot => Path.Combine(Root, "shared");
    public string TemplatesRoot => Path.Combine(SharedRoot, "templates");
    public string ObjectsRoot => Path.Combine(SharedRoot, "objects");
    public string ImportsRoot => Path.Combine(SharedRoot, "imports");
    public string ProfilesRoot => Path.Combine(Root, "profiles");
    public string ProfileRoot(AccountId accountId) => Path.Combine(ProfilesRoot, SafeSegment(accountId.Value));
    public string CodexHome(AccountId accountId) => Path.Combine(ProfileRoot(accountId), "codex-home");
    public string OverridePath(AccountId accountId) => Path.Combine(ProfileRoot(accountId), "override.toml");

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }
        return builder.ToString();
    }
}

public sealed record SharedTemplateMetadata(
    string Version,
    string ConfigSha256,
    string SourcePath,
    string SourceSha256,
    DateTimeOffset ImportedAt,
    IReadOnlyList<string> ManagedAssetRoots,
    IReadOnlyList<string> RemovedSensitivePaths);

public sealed record SharedTemplate(
    string DirectoryPath,
    SharedTemplateMetadata Metadata)
{
    public string ConfigPath => Path.Combine(DirectoryPath, "config-template.toml");
    public string AssetsPath => Path.Combine(DirectoryPath, "assets");
    public string HooksPath => Path.Combine(DirectoryPath, "hooks.json");
}

public sealed record HookSyncResult(
    bool RootHooksCopied,
    int HookFilesCopied,
    bool SourceAvailable);

public sealed record MaterializedProfile(
    AccountId AccountId,
    string CodexHome,
    string TemplateVersion,
    string ConfigSha256,
    bool HadDrift,
    IReadOnlyList<string> DriftedPaths,
    DateTimeOffset MaterializedAt);

public sealed record ProfileDriftReport(
    AccountId AccountId,
    bool HasDrift,
    IReadOnlyList<string> Paths);

public sealed record TemplateCompactionResult(
    int TemplatesBefore,
    int TemplatesAfter,
    int DuplicateTemplatesRemoved,
    int UnreferencedTemplatesRemoved,
    int ProfilesUpdated,
    IReadOnlyList<string> RemovedTemplateVersions);

public sealed class StagedProfileDeletion : IAsyncDisposable
{
    private readonly string? _originalPath;
    private readonly string? _stagedPath;
    private int _completed;

    internal StagedProfileDeletion(AccountId accountId, string? originalPath, string? stagedPath)
    {
        AccountId = accountId;
        _originalPath = originalPath;
        _stagedPath = stagedPath;
    }

    public AccountId AccountId { get; }
    public bool HasProfile => _stagedPath is not null;

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _completed, 1) != 0 || _stagedPath is null)
        {
            return Task.CompletedTask;
        }
        if (Directory.Exists(_stagedPath))
        {
            Directory.Delete(_stagedPath, recursive: true);
        }
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _completed, 1) != 0 || _stagedPath is null || _originalPath is null)
        {
            return Task.CompletedTask;
        }
        if (Directory.Exists(_stagedPath) && !Directory.Exists(_originalPath))
        {
            Directory.Move(_stagedPath, _originalPath);
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _completed) == 0)
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}

public sealed class ProfileMaterializationException : Exception
{
    public ProfileMaterializationException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class ProfileConfigPolicy
{
    private static readonly HashSet<string> AllowedRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "model",
        "review_model",
        "model_provider",
        "approval_policy",
        "sandbox_mode",
        "model_reasoning_effort",
        "model_reasoning_summary",
        "model_verbosity",
        "plan_mode_reasoning_effort",
        "personality",
        "web_search",
        "network_access",
        "project_doc_max_bytes",
        "project_doc_fallback_filenames",
        "tool_output_token_limit",
        "features",
        "tui",
        "shell_environment_policy",
        "mcp_servers",
        "notify",
        "tools",
        "skills",
        "apps",
        "experimental"
    };

    private static readonly string[] ForbiddenKeyFragments =
    {
        "access_token",
        "refresh_token",
        "token",
        "password",
        "passwd",
        "cookie",
        "api_key",
        "apikey",
        "authorization",
        "credential",
        "secret",
        "private_key"
    };

    private static readonly HashSet<string> ForbiddenTableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "env",
        "headers",
        "http_headers",
        "bearer_token",
        "auth",
        "credentials",
        "secrets"
    };

    public (TomlTable Sanitized, IReadOnlyList<string> RemovedPaths) Sanitize(TomlTable source)
    {
        var result = new TomlTable();
        var removed = new List<string>();

        foreach (var pair in source)
        {
            if (!AllowedRootKeys.Contains(pair.Key))
            {
                removed.Add(pair.Key);
                continue;
            }

            if (TrySanitizeValue(pair.Key, pair.Value, pair.Key, removed, out var sanitized))
            {
                result[pair.Key] = sanitized!;
            }
        }

        EnforceManagedAuthStorage(result);
        return (result, removed);
    }

    public TomlTable MergeOverride(TomlTable template, TomlTable userOverride, ICollection<string> removed)
    {
        var merged = DeepCloneTable(template);
        foreach (var pair in userOverride)
        {
            if (!AllowedRootKeys.Contains(pair.Key) &&
                !string.Equals(pair.Key, "cli_auth_credentials_store", StringComparison.OrdinalIgnoreCase))
            {
                removed.Add(pair.Key);
                continue;
            }

            if (string.Equals(pair.Key, "cli_auth_credentials_store", StringComparison.OrdinalIgnoreCase))
            {
                removed.Add(pair.Key);
                continue;
            }

            if (TrySanitizeValue(pair.Key, pair.Value, pair.Key, removed, out var sanitized))
            {
                if (merged.TryGetValue(pair.Key, out var existing) &&
                    existing is TomlTable existingTable &&
                    sanitized is TomlTable incomingTable)
                {
                    MergeTable(existingTable, incomingTable);
                }
                else
                {
                    merged[pair.Key] = sanitized!;
                }
            }
        }

        EnforceManagedAuthStorage(merged);
        return merged;
    }

    public void EnforceManagedAuthStorage(TomlTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        table["cli_auth_credentials_store"] = "keyring";
        if (!table.TryGetValue("features", out var featuresValue) || featuresValue is not TomlTable features)
        {
            features = new TomlTable();
            table["features"] = features;
        }
        // Keep the credential payload encrypted under the isolated CODEX_HOME and store only
        // its short encryption key in Windows Credential Manager. Direct keyring storage cannot
        // hold current ChatGPT OAuth payloads because WinCred caps a credential blob at 2560 bytes.
        features["secret_auth_storage"] = true;
    }

    private static bool TrySanitizeValue(
        string key,
        object? value,
        string path,
        ICollection<string> removed,
        out object? sanitized)
    {
        if (IsSensitiveKey(key) || ForbiddenTableKeys.Contains(key))
        {
            removed.Add(path);
            sanitized = null;
            return false;
        }

        switch (value)
        {
            case TomlTable table:
            {
                var clean = new TomlTable();
                foreach (var child in table)
                {
                    var childPath = $"{path}.{child.Key}";
                    if (TrySanitizeValue(child.Key, child.Value, childPath, removed, out var childValue))
                    {
                        clean[child.Key] = childValue!;
                    }
                }
                sanitized = clean;
                return true;
            }
            case TomlTableArray tableArray:
            {
                var clean = new TomlTableArray();
                var index = 0;
                foreach (var childTable in tableArray)
                {
                    if (TrySanitizeValue(key, childTable, $"{path}[{index}]", removed, out var childValue) &&
                        childValue is TomlTable childClean)
                    {
                        clean.Add(childClean);
                    }
                    index++;
                }
                sanitized = clean;
                return true;
            }
            case Array array:
            {
                var clean = new List<object?>();
                foreach (var item in array)
                {
                    clean.Add(CloneScalarOrCollection(item));
                }
                sanitized = clean.ToArray();
                return true;
            }
            default:
                sanitized = CloneScalarOrCollection(value);
                return true;
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace('-', '_').ToLowerInvariant();
        // This is a numeric output budget, not a credential. Keep the exception explicit
        // rather than weakening the conservative token/secret filter globally.
        if (normalized == "tool_output_token_limit") return false;
        return ForbiddenKeyFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    private static object? CloneScalarOrCollection(object? value)
    {
        if (value is null || value is string || value.GetType().IsValueType)
        {
            return value;
        }

        if (value is TomlTable table)
        {
            return DeepCloneTable(table);
        }

        if (value is TomlTableArray tables)
        {
            var clone = new TomlTableArray();
            foreach (var item in tables)
            {
                clone.Add(DeepCloneTable(item));
            }
            return clone;
        }

        if (value is IEnumerable<object?> sequence)
        {
            return sequence.Select(CloneScalarOrCollection).ToArray();
        }

        return value;
    }

    private static TomlTable DeepCloneTable(TomlTable source)
    {
        var clone = new TomlTable();
        foreach (var pair in source)
        {
            clone[pair.Key] = CloneScalarOrCollection(pair.Value)!;
        }
        return clone;
    }

    private static void MergeTable(TomlTable target, TomlTable source)
    {
        foreach (var pair in source)
        {
            if (target.TryGetValue(pair.Key, out var existing) &&
                existing is TomlTable existingTable &&
                pair.Value is TomlTable sourceTable)
            {
                MergeTable(existingTable, sourceTable);
            }
            else
            {
                target[pair.Key] = CloneScalarOrCollection(pair.Value)!;
            }
        }
    }
}

public sealed class ProfileMaterializer
{
    private const string ManagedManifestFile = ".codex-router-managed-assets.json";
    private const string ProfileMetadataFile = ".codex-router-profile.json";
    private static readonly string[] ManagedAssetRoots = { "skills", "rules", "prompts", "hooks", "plugins", "agents" };
    private static readonly string[] PrivateOrSensitivePathFragments =
    {
        "auth.json",
        ".env",
        "secret",
        "credential",
        "token",
        "cookie",
        "session",
        "sqlite",
        "state",
        "cache",
        "log"
    };

    private readonly ProfileLayout _layout;
    private readonly ProfileConfigPolicy _policy;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim RuntimeObjectGate = new(1, 1);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    public ProfileMaterializer(ProfileLayout layout, ProfileConfigPolicy? policy = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _policy = policy ?? new ProfileConfigPolicy();
    }

    public async Task<SharedTemplate> ImportSharedTemplateAsync(
        string sourceCodexHome,
        CancellationToken cancellationToken = default)
    {
        sourceCodexHome = Path.GetFullPath(sourceCodexHome);
        var sourceConfig = Path.Combine(sourceCodexHome, "config.toml");
        if (!File.Exists(sourceConfig))
        {
            throw new ProfileMaterializationException($"Source Codex config does not exist: {sourceConfig}");
        }

        string originalText;
        try
        {
            originalText = await File.ReadAllTextAsync(sourceConfig, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileMaterializationException("Failed to read source Codex config.", ex);
        }

        TomlTable parsed;
        try
        {
            parsed = Toml.ToModel(originalText);
        }
        catch (Exception ex) when (ex is not ProfileMaterializationException)
        {
            throw new ProfileMaterializationException("Source Codex config is not valid TOML.", ex);
        }

        var (sanitized, removed) = _policy.Sanitize(parsed);
        var sanitizedText = Toml.FromModel(sanitized);
        _ = Toml.ToModel(sanitizedText);

        var sourceHash = Sha256(originalText);
        var configHash = Sha256(sanitizedText);
        var assets = await EnumerateManagedAssetsAsync(sourceCodexHome, cancellationToken).ConfigureAwait(false);
        var rootHooksPath = Path.Combine(sourceCodexHome, "hooks.json");
        var rootHooksHash = File.Exists(rootHooksPath)
            ? await Sha256FileAsync(rootHooksPath, cancellationToken).ConfigureAwait(false)
            : null;
        var contentHash = ComputeTemplateContentHash(configHash, rootHooksHash, assets);
        var version = $"content-{contentHash}";
        var finalDirectory = Path.Combine(_layout.TemplatesRoot, version);

        // Content-addressing makes repeated imports cheap and deterministic. A second
        // process may win the race to publish the same directory; in that case the
        // already-published template is the canonical result.
        if (Directory.Exists(finalDirectory))
        {
            return await LoadSharedTemplateAsync(finalDirectory, cancellationToken).ConfigureAwait(false);
        }

        var staging = finalDirectory + $".staging-{Guid.NewGuid():N}";
        Directory.CreateDirectory(staging);

        try
        {
            await WriteAtomicTextAsync(Path.Combine(staging, "config-template.toml"), sanitizedText, cancellationToken)
                .ConfigureAwait(false);

            var assetsDirectory = Path.Combine(staging, "assets");
            Directory.CreateDirectory(assetsDirectory);
            foreach (var asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(assetsDirectory, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await MaterializeManagedAssetAsync(asset.SourcePath, target, asset.Sha256, cancellationToken)
                    .ConfigureAwait(false);
            }

            var sourceHooksFile = Path.Combine(sourceCodexHome, "hooks.json");
            if (File.Exists(sourceHooksFile))
            {
                await CopyFileAtomicallyAsync(sourceHooksFile, Path.Combine(staging, "hooks.json"), cancellationToken)
                    .ConfigureAwait(false);
            }

            var metadata = new SharedTemplateMetadata(
                version,
                configHash,
                sourceConfig,
                sourceHash,
                DateTimeOffset.UtcNow,
                ManagedAssetRoots,
                removed.OrderBy(static x => x, StringComparer.Ordinal).ToArray());
            await WriteJsonAsync(Path.Combine(staging, "metadata.json"), metadata, cancellationToken).ConfigureAwait(false);

            await WriteImportRecordAsync(sourceConfig, originalText, sanitizedText, metadata, cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(_layout.TemplatesRoot);
            try
            {
                Directory.Move(staging, finalDirectory);
            }
            catch (IOException) when (Directory.Exists(finalDirectory))
            {
                // Another importer published the same content while this process was
                // copying its staging tree. Its directory is equivalent by hash.
                TryDeleteDirectory(staging);
                return await LoadSharedTemplateAsync(finalDirectory, cancellationToken).ConfigureAwait(false);
            }

            return new SharedTemplate(finalDirectory, metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException)
        {
            throw new ProfileMaterializationException("Failed to import shared Codex profile template.", ex);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    public async Task<MaterializedProfile> MaterializeAsync(
        AccountId accountId,
        SharedTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var codexHome = _layout.CodexHome(accountId);
        Directory.CreateDirectory(codexHome);

        var drift = await DetectDriftAsync(accountId, cancellationToken).ConfigureAwait(false);
        var templateModel = Toml.ToModel(
            await File.ReadAllTextAsync(template.ConfigPath, cancellationToken).ConfigureAwait(false));

        var removed = new List<string>();
        var overridePath = _layout.OverridePath(accountId);
        TomlTable merged = templateModel;
        if (File.Exists(overridePath))
        {
            var overrideModel = Toml.ToModel(
                await File.ReadAllTextAsync(overridePath, cancellationToken).ConfigureAwait(false));
            merged = _policy.MergeOverride(templateModel, overrideModel, removed);
        }
        else
        {
            _policy.EnforceManagedAuthStorage(merged);
        }
        _policy.EnforceManagedAuthStorage(merged);

        var rendered = Toml.FromModel(merged);
        _ = Toml.ToModel(rendered);

        var configPath = Path.Combine(codexHome, "config.toml");
        await BackupManagedConfigAsync(codexHome, configPath, cancellationToken).ConfigureAwait(false);
        await WriteAtomicTextAsync(configPath, rendered, cancellationToken).ConfigureAwait(false);

        var manifest = await SynchronizeManagedAssetsAsync(codexHome, template.AssetsPath, cancellationToken)
            .ConfigureAwait(false);
        await CopyIfMissingAsync(template.HooksPath, Path.Combine(codexHome, "hooks.json"), cancellationToken)
            .ConfigureAwait(false);
        var configHash = Sha256(rendered);
        var metadata = new ProfileMetadata(template.Metadata.Version, configHash, manifest, DateTimeOffset.UtcNow);
        await WriteJsonAsync(Path.Combine(codexHome, ProfileMetadataFile), metadata, cancellationToken).ConfigureAwait(false);

        return new MaterializedProfile(
            accountId,
            codexHome,
            template.Metadata.Version,
            configHash,
            drift.HasDrift,
            drift.Paths,
            metadata.MaterializedAt);
    }

    /// <summary>
    /// Repairs only missing hook assets from an existing Codex home. Existing hooks,
    /// credentials, sessions, state and other account data are never overwritten.
    /// </summary>
    public async Task<HookSyncResult> SynchronizeMissingHooksAsync(
        string targetCodexHome,
        string sourceCodexHome,
        CancellationToken cancellationToken = default)
    {
        targetCodexHome = Path.GetFullPath(targetCodexHome);
        sourceCodexHome = Path.GetFullPath(sourceCodexHome);
        if (string.Equals(targetCodexHome, sourceCodexHome, StringComparison.OrdinalIgnoreCase))
        {
            return new HookSyncResult(
                RootHooksCopied: false,
                HookFilesCopied: 0,
                SourceAvailable: File.Exists(Path.Combine(sourceCodexHome, "hooks.json")) ||
                                 Directory.Exists(Path.Combine(sourceCodexHome, "hooks")));
        }
        Directory.CreateDirectory(targetCodexHome);

        var sourceHooksFile = Path.Combine(sourceCodexHome, "hooks.json");
        var targetHooksFile = Path.Combine(targetCodexHome, "hooks.json");
        var rootCopied = false;
        if (File.Exists(sourceHooksFile) && !File.Exists(targetHooksFile))
        {
            await CopyFileAtomicallyAsync(sourceHooksFile, targetHooksFile, cancellationToken)
                .ConfigureAwait(false);
            rootCopied = true;
        }

        var sourceHooksDirectory = Path.Combine(sourceCodexHome, "hooks");
        var targetHooksDirectory = Path.Combine(targetCodexHome, "hooks");
        var filesCopied = 0;
        if (Directory.Exists(sourceHooksDirectory))
        {
            Directory.CreateDirectory(targetHooksDirectory);
            foreach (var source in Directory.EnumerateFiles(sourceHooksDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceHooksDirectory, source);
                if (IsSensitiveRelativePath(relative))
                {
                    continue;
                }

                var target = Path.Combine(targetHooksDirectory, relative);
                if (File.Exists(target))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyFileAtomicallyAsync(source, target, cancellationToken).ConfigureAwait(false);
                filesCopied++;
            }
        }

        return new HookSyncResult(rootCopied, filesCopied, File.Exists(sourceHooksFile) || Directory.Exists(sourceHooksDirectory));
    }

    public Task<StagedProfileDeletion> StageProfileDeletionAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileRoot = Path.GetFullPath(_layout.ProfileRoot(accountId));
        var profilesRoot = Path.GetFullPath(_layout.ProfilesRoot) + Path.DirectorySeparatorChar;
        if (!profileRoot.StartsWith(profilesRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileMaterializationException("Refusing to stage a profile outside the managed profiles root.");
        }
        if (!Directory.Exists(profileRoot))
        {
            return Task.FromResult(new StagedProfileDeletion(accountId, null, null));
        }

        var trashRoot = Path.Combine(_layout.Root, ".trash");
        Directory.CreateDirectory(trashRoot);
        var staged = Path.Combine(trashRoot, $"{accountId.Value}-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(profileRoot, staged);
            return Task.FromResult(new StagedProfileDeletion(accountId, profileRoot, staged));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileMaterializationException($"Failed to stage managed profile '{accountId}' for deletion.", ex);
        }
    }

    public Task DeleteProfileAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileRoot = Path.GetFullPath(_layout.ProfileRoot(accountId));
        var profilesRoot = Path.GetFullPath(_layout.ProfilesRoot) + Path.DirectorySeparatorChar;
        if (!profileRoot.StartsWith(profilesRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileMaterializationException("Refusing to delete a profile outside the managed profiles root.");
        }

        if (!Directory.Exists(profileRoot))
        {
            return Task.CompletedTask;
        }

        try
        {
            Directory.Delete(profileRoot, recursive: true);
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileMaterializationException($"Failed to delete managed profile '{accountId}'.", ex);
        }
    }

    public async Task<ProfileDriftReport> DetectDriftAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default)
    {
        var codexHome = _layout.CodexHome(accountId);
        var metadataPath = Path.Combine(codexHome, ProfileMetadataFile);
        if (!File.Exists(metadataPath))
        {
            return new ProfileDriftReport(accountId, false, Array.Empty<string>());
        }

        ProfileMetadata? metadata;
        await using (var stream = File.OpenRead(metadataPath))
        {
            metadata = await JsonSerializer.DeserializeAsync<ProfileMetadata>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        if (metadata is null)
        {
            return new ProfileDriftReport(accountId, true, new[] { ProfileMetadataFile });
        }

        var drift = new List<string>();
        var configPath = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(configPath) || !string.Equals(await Sha256FileAsync(configPath, cancellationToken).ConfigureAwait(false), metadata.ConfigSha256, StringComparison.OrdinalIgnoreCase))
        {
            drift.Add("config.toml");
        }

        foreach (var asset in metadata.ManagedAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.Combine(codexHome, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) || !string.Equals(await Sha256FileAsync(full, cancellationToken).ConfigureAwait(false), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                drift.Add(asset.RelativePath);
            }
        }

        return new ProfileDriftReport(accountId, drift.Count > 0, drift);
    }

    /// <summary>
    /// Compacts content-addressed and legacy template directories while preserving every
    /// profile and all profile data. Only duplicate templates and old unreferenced
    /// templates are eligible for removal; sessions, archived sessions, auth, logs and
    /// other account files are never traversed by the deletion logic.
    /// </summary>
    public async Task<TemplateCompactionResult> CompactTemplatesAsync(
        int maxUnreferencedHistory = 1,
        CancellationToken cancellationToken = default)
    {
        if (maxUnreferencedHistory < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUnreferencedHistory));
        }

        Directory.CreateDirectory(_layout.TemplatesRoot);
        var directories = Directory.EnumerateDirectories(_layout.TemplatesRoot)
            .Where(static path => !Path.GetFileName(path).Contains(".staging-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var before = directories.Length;
        if (before == 0)
        {
            return new TemplateCompactionResult(0, 0, 0, 0, 0, Array.Empty<string>());
        }

        var infos = new List<TemplateInfo>(directories.Length);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await TryLoadTemplateMetadataAsync(directory, cancellationToken).ConfigureAwait(false);
            if (metadata is null || !File.Exists(Path.Combine(directory, "config-template.toml")))
            {
                // Unknown directories are retained for safety. They may be created by a
                // newer Router version and cannot be proven safe to remove here.
                continue;
            }

            var canonicalVersion = await ComputeTemplateVersionAsync(directory, metadata, cancellationToken)
                .ConfigureAwait(false);
            infos.Add(new TemplateInfo(directory, Path.GetFileName(directory), metadata, canonicalVersion));
        }

        var canonicalByVersion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicateDirectories = new List<TemplateInfo>();
        foreach (var group in infos.GroupBy(static info => info.CanonicalVersion, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedPath = Path.Combine(_layout.TemplatesRoot, group.Key);
            var canonical = group.FirstOrDefault(info => string.Equals(info.DirectoryPath, expectedPath, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                canonical = group.OrderBy(static info => info.DirectoryPath, StringComparer.OrdinalIgnoreCase).First();
                if (!string.Equals(canonical.DirectoryPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.Move(canonical.DirectoryPath, expectedPath);
                        canonical = canonical with { DirectoryPath = expectedPath, CurrentVersion = group.Key };
                    }
                    catch (IOException) when (Directory.Exists(expectedPath))
                    {
                        // A concurrent compactor/importer already published the canonical
                        // directory. Prefer it and treat the old directory as duplicate.
                        canonical = canonical with { DirectoryPath = expectedPath, CurrentVersion = group.Key };
                    }
                }
            }

            canonicalByVersion[group.Key] = canonical.DirectoryPath;
            foreach (var member in group)
            {
                // Legacy directory names are valid references in existing profile
                // metadata. Resolve them to the content-addressed canonical version.
                canonicalByVersion[member.CurrentVersion] = canonical.DirectoryPath;
                canonicalByVersion[member.Metadata.Version] = canonical.DirectoryPath;
            }
            foreach (var duplicate in group.Where(info =>
                         !string.Equals(info.DirectoryPath, canonical.DirectoryPath, StringComparison.OrdinalIgnoreCase) &&
                         Directory.Exists(info.DirectoryPath)))
            {
                duplicateDirectories.Add(duplicate);
            }

            var canonicalMetadata = canonical.Metadata with { Version = group.Key };
            await WriteJsonAsync(Path.Combine(canonical.DirectoryPath, "metadata.json"), canonicalMetadata, cancellationToken)
                .ConfigureAwait(false);
        }

        var profilesUpdated = 0;
        var referencedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profileRoot in Directory.Exists(_layout.ProfilesRoot)
                     ? Directory.EnumerateDirectories(_layout.ProfilesRoot)
                     : Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var metadataPath in ProfileMetadataPaths(profileRoot))
            {
                if (!File.Exists(metadataPath)) continue;
                var metadata = await TryLoadProfileMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(false);
                if (metadata is null || string.IsNullOrWhiteSpace(metadata.TemplateVersion)) continue;

                if (canonicalByVersion.TryGetValue(metadata.TemplateVersion, out var canonicalPath))
                {
                    var canonicalVersion = Path.GetFileName(canonicalPath);
                    referencedVersions.Add(canonicalVersion);
                    if (!string.Equals(metadata.TemplateVersion, canonicalVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonAsync(metadataPath, metadata with { TemplateVersion = canonicalVersion }, cancellationToken)
                            .ConfigureAwait(false);
                        profilesUpdated++;
                    }
                }
            }
        }

        var removed = new List<string>();
        foreach (var duplicate in duplicateDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(duplicate.DirectoryPath))
            {
                Directory.Delete(duplicate.DirectoryPath, recursive: true);
                removed.Add(duplicate.CurrentVersion);
            }
        }

        var retained = canonicalByVersion.Values
            .Where(Directory.Exists)
            .Select(static path => Path.GetFileName(path)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreferenced = retained
            .Where(version => !referencedVersions.Contains(version))
            .Select(version => new
            {
                Version = version,
                Path = Path.Combine(_layout.TemplatesRoot, version),
                LastWrite = Directory.GetLastWriteTimeUtc(Path.Combine(_layout.TemplatesRoot, version))
            })
            .OrderByDescending(static item => item.LastWrite)
            .ThenByDescending(static item => item.Version, StringComparer.OrdinalIgnoreCase)
            .Skip(maxUnreferencedHistory)
            .ToArray();
        foreach (var stale in unreferenced)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(stale.Path))
            {
                Directory.Delete(stale.Path, recursive: true);
                removed.Add(stale.Version);
            }
        }

        // Upgrade legacy runtime copies in-place. This only touches the explicitly
        // managed plugin runtime subtree and never enters sessions or archived_sessions.
        foreach (var templatePath in canonicalByVersion.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(templatePath))
            {
                await RebuildRuntimeLinksAsync(Path.Combine(templatePath, "assets", "plugins", ".plugin-appserver"), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (Directory.Exists(_layout.ProfilesRoot))
        {
            foreach (var profileRoot in Directory.EnumerateDirectories(_layout.ProfilesRoot))
            {
                await RebuildRuntimeLinksAsync(Path.Combine(profileRoot, "codex-home", "plugins", ".plugin-appserver"), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var remaining = Directory.EnumerateDirectories(_layout.TemplatesRoot)
            .Count(static path => !Path.GetFileName(path).Contains(".staging-", StringComparison.OrdinalIgnoreCase));
        return new TemplateCompactionResult(
            before,
            remaining,
            duplicateDirectories.Count,
            removed.Count - duplicateDirectories.Count,
            profilesUpdated,
            removed.OrderBy(static version => version, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<IReadOnlyList<ManagedAsset>> SynchronizeManagedAssetsAsync(
        string codexHome,
        string templateAssetsPath,
        CancellationToken cancellationToken)
    {
        var oldManifestPath = Path.Combine(codexHome, ManagedManifestFile);
        IReadOnlyList<ManagedAsset> oldAssets = Array.Empty<ManagedAsset>();
        if (File.Exists(oldManifestPath))
        {
            try
            {
                await using var stream = File.OpenRead(oldManifestPath);
                oldAssets = await JsonSerializer.DeserializeAsync<List<ManagedAsset>>(stream, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false) ?? new List<ManagedAsset>();
            }
            catch (JsonException)
            {
                oldAssets = Array.Empty<ManagedAsset>();
            }
        }

        var newAssets = new List<ManagedAsset>();
        if (Directory.Exists(templateAssetsPath))
        {
            foreach (var source in Directory.EnumerateFiles(templateAssetsPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(templateAssetsPath, source).Replace(Path.DirectorySeparatorChar, '/');
                if (IsSensitiveRelativePath(relative))
                {
                    continue;
                }

                var destination = Path.Combine(codexHome, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var sha256 = await Sha256FileAsync(source, cancellationToken).ConfigureAwait(false);
                await MaterializeManagedAssetAsync(source, destination, sha256, cancellationToken).ConfigureAwait(false);
                newAssets.Add(new ManagedAsset(relative, sha256));
            }
        }

        var newSet = newAssets.Select(static x => x.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in oldAssets)
        {
            if (newSet.Contains(old.RelativePath))
            {
                continue;
            }

            var destination = Path.Combine(codexHome, old.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination) &&
                string.Equals(await Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false), old.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
            }
        }

        await WriteJsonAsync(oldManifestPath, newAssets, cancellationToken).ConfigureAwait(false);
        return newAssets;
    }

    private async Task WriteImportRecordAsync(
        string sourceConfig,
        string originalText,
        string sanitizedText,
        SharedTemplateMetadata metadata,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_layout.ImportsRoot);
        var directory = Path.Combine(_layout.ImportsRoot, metadata.Version);
        Directory.CreateDirectory(directory);

        var redactedBackup = RedactSensitiveToml(originalText);
        await WriteAtomicTextAsync(Path.Combine(directory, "config.original.redacted.toml"), redactedBackup, cancellationToken)
            .ConfigureAwait(false);
        await WriteAtomicTextAsync(Path.Combine(directory, "config.sanitized.toml"), sanitizedText, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(directory, "provenance.json"), new
        {
            sourcePath = sourceConfig,
            sourceSha256 = metadata.SourceSha256,
            importedAt = metadata.ImportedAt,
            removedSensitivePaths = metadata.RemovedSensitivePaths
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SourceAsset>> EnumerateManagedAssetsAsync(
        string sourceCodexHome,
        CancellationToken cancellationToken)
    {
        var assets = new List<SourceAsset>();
        foreach (var rootName in ManagedAssetRoots)
        {
            var sourceRoot = Path.Combine(sourceCodexHome, rootName);
            if (!Directory.Exists(sourceRoot)) continue;

            foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceRoot, source).Replace(Path.DirectorySeparatorChar, '/');
                if (IsSensitiveRelativePath(relative)) continue;
                var fullRelative = $"{rootName}/{relative}";
                assets.Add(new SourceAsset(fullRelative, source, await Sha256FileAsync(source, cancellationToken).ConfigureAwait(false)));
            }
        }

        return assets
            .OrderBy(static asset => asset.RelativePath, StringComparer.Ordinal)
            .ThenBy(static asset => asset.Sha256, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ComputeTemplateContentHash(
        string configHash,
        string? rootHooksHash,
        IEnumerable<SourceAsset> assets)
    {
        var builder = new StringBuilder();
        builder.Append("codex-router-template-v2\n");
        builder.Append("config\t").Append(configHash).Append('\n');
        builder.Append("root-hooks\t").Append(rootHooksHash ?? "-").Append('\n');
        foreach (var asset in assets.OrderBy(static asset => asset.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(asset.RelativePath).Append('\t').Append(asset.Sha256).Append('\n');
        }
        return Sha256(builder.ToString());
    }

    private async Task<SharedTemplate> LoadSharedTemplateAsync(string directory, CancellationToken cancellationToken)
    {
        var metadata = await TryLoadTemplateMetadataAsync(directory, cancellationToken).ConfigureAwait(false)
            ?? throw new ProfileMaterializationException($"Shared template metadata is missing: {directory}");
        return new SharedTemplate(directory, metadata);
    }

    private async Task<SharedTemplateMetadata?> TryLoadTemplateMetadataAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "metadata.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SharedTemplateMetadata>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> ComputeTemplateVersionAsync(
        string directory,
        SharedTemplateMetadata metadata,
        CancellationToken cancellationToken)
    {
        var rootHooksPath = Path.Combine(directory, "hooks.json");
        var rootHooksHash = File.Exists(rootHooksPath)
            ? await Sha256FileAsync(rootHooksPath, cancellationToken).ConfigureAwait(false)
            : null;
        var assets = new List<SourceAsset>();
        var assetsRoot = Path.Combine(directory, "assets");
        if (Directory.Exists(assetsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(assetsRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                if (!IsSensitiveRelativePath(relative))
                {
                    assets.Add(new SourceAsset(relative, path, await Sha256FileAsync(path, cancellationToken).ConfigureAwait(false)));
                }
            }
        }

        var configHash = metadata.ConfigSha256;
        if (string.IsNullOrWhiteSpace(configHash))
        {
            configHash = await Sha256FileAsync(Path.Combine(directory, "config-template.toml"), cancellationToken)
                .ConfigureAwait(false);
        }
        return $"content-{ComputeTemplateContentHash(configHash, rootHooksHash, assets)}";
    }

    private static IEnumerable<string> ProfileMetadataPaths(string profileRoot)
    {
        yield return Path.Combine(profileRoot, "codex-home", ProfileMetadataFile);
        yield return Path.Combine(profileRoot, ProfileMetadataFile);
    }

    private async Task<ProfileMetadata?> TryLoadProfileMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ProfileMetadata>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task MaterializeManagedAssetAsync(
        string source,
        string destination,
        string sha256,
        CancellationToken cancellationToken)
    {
        if (IsRuntimeObjectPath(Path.GetRelativePath(_layout.Root, destination)))
        {
            var objectPath = await EnsureRuntimeObjectAsync(source, sha256, cancellationToken).ConfigureAwait(false);
            await ReplaceWithHardLinkOrCopyAsync(objectPath, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CopyFileAtomicallyAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> EnsureRuntimeObjectAsync(
        string source,
        string sha256,
        CancellationToken cancellationToken)
    {
        var objectPath = Path.Combine(_layout.ObjectsRoot, sha256);
        Directory.CreateDirectory(_layout.ObjectsRoot);
        await RuntimeObjectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(objectPath) &&
                string.Equals(await Sha256FileAsync(objectPath, cancellationToken).ConfigureAwait(false), sha256, StringComparison.OrdinalIgnoreCase))
            {
                return objectPath;
            }

            var staged = objectPath + $".staging-{Guid.NewGuid():N}";
            try
            {
                await CopyFileAtomicallyAsync(source, staged, cancellationToken).ConfigureAwait(false);
                if (File.Exists(objectPath)) File.Delete(objectPath);
                File.Move(staged, objectPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(objectPath))
            {
                if (!string.Equals(await Sha256FileAsync(objectPath, cancellationToken).ConfigureAwait(false), sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }
            }
            finally
            {
                TryDeleteFile(staged);
            }

            return objectPath;
        }
        finally
        {
            RuntimeObjectGate.Release();
        }
    }

    private static async Task ReplaceWithHardLinkOrCopyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        TryDeleteFile(destination);
        if (TryCreateHardLink(destination, source)) return;
        await CopyFileAtomicallyAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private async Task RebuildRuntimeLinksAsync(string runtimeRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(runtimeRoot)) return;
        foreach (var path in Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = await Sha256FileAsync(path, cancellationToken).ConfigureAwait(false);
            var objectPath = await EnsureRuntimeObjectAsync(path, hash, cancellationToken).ConfigureAwait(false);
            await ReplaceWithHardLinkOrCopyAsync(objectPath, path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string RedactSensitiveToml(string originalText)
    {
        try
        {
            var table = Toml.ToModel(originalText);
            var policy = new ProfileConfigPolicy();
            var (sanitized, _) = policy.Sanitize(table);
            return "# Redacted safety backup. Sensitive and non-shared values intentionally omitted.\n" +
                   Toml.FromModel(sanitized);
        }
        catch
        {
            return "# Original config omitted because it could not be safely redacted.\n";
        }
    }

    private static async Task BackupManagedConfigAsync(string codexHome, string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return;
        }

        var backups = Path.Combine(codexHome, ".codex-router-backups");
        Directory.CreateDirectory(backups);
        var hash = await Sha256FileAsync(configPath, cancellationToken).ConfigureAwait(false);
        var target = Path.Combine(backups, $"config-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{hash[..10]}.toml");
        await CopyFileAtomicallyAsync(configPath, target, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopySafeTreeAsync(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, source);
            if (IsSensitiveRelativePath(relative))
            {
                continue;
            }
            var target = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyFileAtomicallyAsync(source, target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSensitiveRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Split('/').Any(segment => segment is ".git" or "node_modules" or "bin" or "obj"))
        {
            return true;
        }
        return PrivateOrSensitivePathFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    private static bool IsRuntimeObjectPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.Contains("plugins/.plugin-appserver/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("plugins/.plugin-appserver", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateHardLink(string destination, string source)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return CreateHardLink(destination, source, nint.Zero);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WriteAtomicTextAsync(string destination, string text, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private async Task WriteJsonAsync<T>(string destination, T value, CancellationToken cancellationToken)
    {
        var text = JsonSerializer.Serialize(value, _jsonOptions);
        await WriteAtomicTextAsync(destination, text, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyFileAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var temp = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static Task CopyIfMissingAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return Task.CompletedTask;
        }
        return CopyFileAtomicallyAsync(source, destination, cancellationToken);
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record SourceAsset(string RelativePath, string SourcePath, string Sha256);
    private sealed record TemplateInfo(
        string DirectoryPath,
        string CurrentVersion,
        SharedTemplateMetadata Metadata,
        string CanonicalVersion);
    private sealed record ManagedAsset(string RelativePath, string Sha256);
    private sealed record ProfileMetadata(
        string TemplateVersion,
        string ConfigSha256,
        IReadOnlyList<ManagedAsset> ManagedAssets,
        DateTimeOffset MaterializedAt);
}
