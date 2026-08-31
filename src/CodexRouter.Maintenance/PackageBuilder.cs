using System.Text.Json;

namespace CodexRouter.Maintenance;

public sealed class PackageBuilder
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<CodexRouterPackageManifest> BuildAsync(
        string cliPublishDirectory,
        string overlayPublishDirectory,
        string outputDirectory,
        string version,
        string architecture = "win-x64",
        CancellationToken cancellationToken = default)
    {
        cliPublishDirectory = Path.GetFullPath(cliPublishDirectory);
        overlayPublishDirectory = Path.GetFullPath(overlayPublishDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Package version is required.", nameof(version));

        var required = new[]
        {
            (Source: Path.Combine(cliPublishDirectory, "codex-route.exe"), Name: "codex-route.exe"),
            (Source: Path.Combine(overlayPublishDirectory, "CodexRouterOverlay.exe"), Name: "CodexRouterOverlay.exe")
        };
        foreach (var file in required)
        {
            if (!File.Exists(file.Source)) throw new FileNotFoundException($"Published executable '{file.Name}' is missing.", file.Source);
        }

        var staging = outputDirectory + $".staging-{Guid.NewGuid():N}";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<PackageFile>();
            foreach (var file in required)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(staging, file.Name);
                File.Copy(file.Source, destination, overwrite: false);
                var info = new FileInfo(destination);
                files.Add(new PackageFile(
                    file.Name,
                    info.Length,
                    await PackageVerifier.ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false)));
            }

            var manifest = new CodexRouterPackageManifest(
                version.Trim(),
                architecture,
                DateTimeOffset.UtcNow,
                files.OrderBy(static file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray());
            await using (var stream = new FileStream(
                Path.Combine(staging, "package-manifest.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _ = await new PackageVerifier().VerifyAsync(staging, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
            Directory.Move(staging, outputDirectory);
            return manifest;
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }
}
