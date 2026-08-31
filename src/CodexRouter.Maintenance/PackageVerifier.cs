using System.Security.Cryptography;
using System.Text.Json;

namespace CodexRouter.Maintenance;

public sealed class PackageVerifier
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<CodexRouterPackageManifest> VerifyAsync(
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        packageDirectory = Path.GetFullPath(packageDirectory);
        var manifestPath = Path.Combine(packageDirectory, "package-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("package-manifest.json is missing.", manifestPath);
        }

        CodexRouterPackageManifest manifest;
        await using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, true))
        {
            manifest = await JsonSerializer.DeserializeAsync<CodexRouterPackageManifest>(stream, _json, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Package manifest is empty or invalid.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException("Package version is missing.");
        if (manifest.Files.Count == 0) throw new InvalidDataException("Package manifest has no files.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Name) || Path.IsPathRooted(file.Name) || file.Name.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsafe package file name: '{file.Name}'.");
            }
            if (!names.Add(file.Name)) throw new InvalidDataException($"Duplicate package file '{file.Name}'.");
            var path = Path.Combine(packageDirectory, file.Name);
            if (!File.Exists(path)) throw new FileNotFoundException($"Package file '{file.Name}' is missing.", path);
            var info = new FileInfo(path);
            if (info.Length != file.Size) throw new InvalidDataException($"Package file '{file.Name}' size mismatch.");
            var hash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Package file '{file.Name}' SHA-256 mismatch.");
            }
        }

        if (!names.Contains("codex-route.exe") || !names.Contains("CodexRouterOverlay.exe"))
        {
            throw new InvalidDataException("Package must contain codex-route.exe and CodexRouterOverlay.exe.");
        }
        return manifest;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
