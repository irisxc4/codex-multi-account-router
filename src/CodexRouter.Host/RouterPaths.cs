namespace CodexRouter.Host;

public sealed record RouterPaths(string Root)
{
    public string Root { get; } = Path.GetFullPath(Root);
    public string DatabasePath => Path.Combine(Root, "router.db");
    public string IntegrationStatePath => Path.Combine(Root, "integration-state.json");
    public string LogsRoot => Path.Combine(Root, "logs");
    public string ProfilesRoot => Path.Combine(Root, "profiles");

    public static RouterPaths Default
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("LOCALAPPDATA could not be resolved.");
            }
            return new RouterPaths(Path.Combine(localAppData, "CodexRouter"));
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsRoot);
    }
}
