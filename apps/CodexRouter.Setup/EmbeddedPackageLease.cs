using System.IO.Compression;
using System.Reflection;

namespace CodexRouter.Setup;

internal sealed class EmbeddedPackageLease : IDisposable
{
    private const string ResourceName = "CodexRouter.Setup.package.zip";
    private readonly bool _deleteOnDispose;

    private EmbeddedPackageLease(string directoryPath, bool deleteOnDispose)
    {
        DirectoryPath = directoryPath;
        _deleteOnDispose = deleteOnDispose;
    }

    public string DirectoryPath { get; }

    public static EmbeddedPackageLease Open()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var payload = assembly.GetManifestResourceStream(ResourceName);
        if (payload is not null)
        {
            var extractionRoot = Path.Combine(
                Path.GetTempPath(),
                "codex-router-setup",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractionRoot);
            try
            {
                using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
                archive.ExtractToDirectory(extractionRoot, overwriteFiles: false);
                EnsureManifestExists(extractionRoot);
                return new EmbeddedPackageLease(extractionRoot, deleteOnDispose: true);
            }
            catch
            {
                TryDelete(extractionRoot);
                throw;
            }
        }

        // Developer builds are not payload-bearing. Keeping an adjacent-package fallback
        // preserves the existing repository workflow while release builds remain standalone.
        var adjacentPackage = Path.Combine(AppContext.BaseDirectory, "package");
        if (File.Exists(Path.Combine(adjacentPackage, "package-manifest.json")))
        {
            return new EmbeddedPackageLease(adjacentPackage, deleteOnDispose: false);
        }

        throw new InvalidOperationException(
            "This setup executable does not contain an install payload, and no adjacent 'package' directory was found. Download or rebuild the complete release installer.");
    }

    public void Dispose()
    {
        if (_deleteOnDispose) TryDelete(DirectoryPath);
    }

    private static void EnsureManifestExists(string directoryPath)
    {
        if (!File.Exists(Path.Combine(directoryPath, "package-manifest.json")))
        {
            throw new InvalidDataException("The embedded Codex Router package is missing package-manifest.json.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
