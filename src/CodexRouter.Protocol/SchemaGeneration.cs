using System.Text.Json;
using CodexRouter.Domain;

namespace CodexRouter.Protocol;

public sealed record SchemaGenerationOptions(
    string? CacheRoot = null,
    TimeSpan? Timeout = null,
    SchemaFlavor Flavor = SchemaFlavor.Stable)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(30);

    public string EffectiveCacheRoot => CacheRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexRouter",
        "schema-cache");
}

public sealed record SchemaGenerationResult(
    SchemaMetadata? Metadata,
    string? Error,
    bool FromCache)
{
    public bool Succeeded => Metadata is not null;
}

public sealed class CodexSchemaGenerator
{
    private const string MetadataFileName = "router-schema-metadata.json";
    private readonly IProcessRunner _processRunner;
    private readonly SchemaMethodRegistry _methodRegistry;

    public CodexSchemaGenerator(IProcessRunner? processRunner = null, SchemaMethodRegistry? methodRegistry = null)
    {
        _processRunner = processRunner ?? new SystemProcessRunner();
        _methodRegistry = methodRegistry ?? new SchemaMethodRegistry();
    }

    public async Task<SchemaGenerationResult> GenerateAsync(
        BinaryIdentity binary,
        SchemaGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binary);
        options ??= new SchemaGenerationOptions();

        var flavorName = options.Flavor == SchemaFlavor.Experimental ? "experimental" : "stable";
        var finalDirectory = Path.Combine(options.EffectiveCacheRoot, binary.Sha256, flavorName);
        var cached = await TryReadCacheAsync(finalDirectory, binary, options.Flavor, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return new SchemaGenerationResult(cached, null, true);
        }

        Directory.CreateDirectory(options.EffectiveCacheRoot);
        var stagingDirectory = Path.Combine(options.EffectiveCacheRoot, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var arguments = new List<string>
            {
                "app-server",
                "generate-json-schema",
                "--out",
                stagingDirectory
            };
            if (options.Flavor == SchemaFlavor.Experimental)
            {
                arguments.Add("--experimental");
            }

            var processResult = await _processRunner.RunAsync(
                new ProcessRequest(binary.Path, arguments, options.EffectiveTimeout),
                cancellationToken).ConfigureAwait(false);

            if (processResult.StartException is not null)
            {
                return new SchemaGenerationResult(null,
                    $"Schema generator could not start: {processResult.StartException.Message}", false);
            }

            if (processResult.TimedOut)
            {
                return new SchemaGenerationResult(null,
                    $"Schema generation timed out after {options.EffectiveTimeout.TotalSeconds:0.###} seconds.", false);
            }

            if (processResult.ExitCode != 0)
            {
                return new SchemaGenerationResult(null,
                    $"Schema generation failed with exit code {processResult.ExitCode}: {Trim(processResult.StandardError)}", false);
            }

            var schemaFiles = Directory.EnumerateFiles(stagingDirectory, "*.json", SearchOption.AllDirectories)
                .Where(static path => !path.EndsWith(MetadataFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (schemaFiles.Length == 0)
            {
                return new SchemaGenerationResult(null, "Codex reported success but emitted no JSON schema files.", false);
            }

            var methods = await _methodRegistry.ExtractMethodsAsync(schemaFiles, cancellationToken).ConfigureAwait(false);
            var metadata = new SchemaMetadata(
                options.Flavor,
                finalDirectory,
                binary.Sha256,
                binary.Version,
                DateTimeOffset.UtcNow,
                schemaFiles.Length,
                methods);

            await WriteMetadataAsync(stagingDirectory, metadata with { DirectoryPath = finalDirectory }, cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            if (Directory.Exists(finalDirectory))
            {
                Directory.Delete(finalDirectory, recursive: true);
            }
            Directory.Move(stagingDirectory, finalDirectory);

            return new SchemaGenerationResult(metadata, null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SchemaGenerationResult(null, $"Schema generation I/O failure: {ex.Message}", false);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // A failed cleanup is harmless because staging directories are never consumed as cache entries.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as above; diagnostics can clean stale staging directories later.
                }
            }
        }
    }

    private static async Task<SchemaMetadata?> TryReadCacheAsync(
        string directory,
        BinaryIdentity binary,
        SchemaFlavor flavor,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(metadataPath);
            var metadata = await JsonSerializer.DeserializeAsync<SchemaMetadata>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (metadata is null ||
                metadata.Flavor != flavor ||
                !string.Equals(metadata.BinarySha256, binary.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.BinaryVersion, binary.Version, StringComparison.Ordinal))
            {
                return null;
            }

            var actualCount = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                .Count(static path => !path.EndsWith(MetadataFileName, StringComparison.OrdinalIgnoreCase));
            return actualCount == metadata.SchemaFileCount ? metadata : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteMetadataAsync(
        string directory,
        SchemaMetadata metadata,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, MetadataFileName);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "…";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

public sealed class SchemaMethodRegistry
{
    public async Task<IReadOnlyList<string>> ExtractMethodsAsync(
        IEnumerable<string> schemaFiles,
        CancellationToken cancellationToken = default)
    {
        var methods = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in schemaFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(file);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            Visit(document.RootElement, methods);
        }

        return methods.ToArray();
    }

    private static void Visit(JsonElement element, ISet<string> methods)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "method", StringComparison.Ordinal))
                    {
                        CollectMethodValues(property.Value, methods, allowDirectString: true);
                    }
                    Visit(property.Value, methods);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, methods);
                }
                break;
        }
    }

    private static void CollectMethodValues(JsonElement element, ISet<string> methods, bool allowDirectString)
    {
        if (allowDirectString && element.ValueKind == JsonValueKind.String)
        {
            AddIfMethod(element.GetString(), methods);
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "const" or "enum")
                {
                    CollectMethodValues(property.Value, methods, allowDirectString: true);
                }
                else if (property.Name is "oneOf" or "anyOf" or "allOf")
                {
                    CollectMethodValues(property.Value, methods, allowDirectString: false);
                }
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectMethodValues(item, methods, allowDirectString: true);
            }
        }
    }

    private static void AddIfMethod(string? value, ISet<string> methods)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Equals("initialize", StringComparison.Ordinal) ||
            (value.Contains('/', StringComparison.Ordinal) &&
             value.All(static character => char.IsLetterOrDigit(character) || character is '/' or '_' or '-')))
        {
            methods.Add(value);
        }
    }
}
