using CodexRouter.Domain;
using CodexRouter.Workers;

namespace CodexRouter.Workers.Tests;

internal static class WorkerTestHelpers
{
    public static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CodexRouter.sln")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests", "CodexRouter.FakeAppServer")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }

    public static string FakeAppServerDll
    {
        get
        {
            var testFrameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = testFrameworkDirectory.Parent?.Name ?? "Debug";
            return Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "CodexRouter.FakeAppServer",
                "bin",
                configuration,
                "net7.0",
                "CodexRouter.FakeAppServer.dll");
        }
    }

    public static WorkerLaunchSpec FakeLaunch(string root, string mode = "normal", string account = "a") =>
        new(
            new WorkerId($"worker-{account}-{mode}"),
            new AccountId(account),
            "dotnet",
            new[] { FakeAppServerDll, mode },
            Path.Combine(root, "profiles", account, "codex-home"));

    public static string CreateTempRoot(string suffix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-router-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    public static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
