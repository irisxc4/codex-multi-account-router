using System.Diagnostics;
using CodexRouter.Protocol;

namespace CodexRouter.Host;

public interface INativeCodexRunner
{
    Task<int> RunInheritedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class RealCodexProcess : INativeCodexRunner
{
    private readonly NativeCodexLocator _locator;

    public RealCodexProcess(CodexBinaryDiscovery? discovery = null, NativeCodexLocator? nativeLocator = null)
    {
        _locator = nativeLocator ?? new NativeCodexLocator(discovery: discovery);
    }

    public async Task<int> RunInheritedAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var discovery = await _locator.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (!discovery.Succeeded || discovery.Binary is null)
        {
            throw new FileNotFoundException(discovery.Error ?? "Real Codex CLI could not be discovered.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = discovery.Binary.Path,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the real Codex CLI.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
