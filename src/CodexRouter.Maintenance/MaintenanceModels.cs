namespace CodexRouter.Maintenance;

public sealed record InstallLayout(string Root)
{
    public string Root { get; } = Path.GetFullPath(Root);
    public string BinDirectory => Path.Combine(Root, "bin");
    public string RouteExecutable => Path.Combine(BinDirectory, "codex-route.exe");
    public string OverlayExecutable => Path.Combine(BinDirectory, "CodexRouterOverlay.exe");
    public string InstalledManifest => Path.Combine(Root, "installed-package.json");
    public string DiagnosticsDirectory => Path.Combine(Root, "diagnostics");
}

public sealed record PackageFile(string Name, long Size, string Sha256);

public sealed record CodexRouterPackageManifest(
    string Version,
    string Architecture,
    DateTimeOffset BuiltAt,
    IReadOnlyList<PackageFile> Files);

public sealed record InstallResult(
    bool Changed,
    string Version,
    string BinDirectory,
    bool StartupEnabled,
    string Message);

public sealed record UninstallResult(
    bool Changed,
    bool DataRetained,
    string Message);

public sealed record RecoveryItem(string Kind, string Key, string Status, string Message);

public sealed record RecoveryReport(
    IReadOnlyList<RecoveryItem> Items,
    int Repaired,
    int Conflicts,
    DateTimeOffset CompletedAt);

public sealed record DiagnosticsBundleResult(
    string ZipPath,
    int FileCount,
    long SizeBytes,
    DateTimeOffset CreatedAt);
