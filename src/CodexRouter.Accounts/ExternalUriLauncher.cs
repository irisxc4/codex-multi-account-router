using System.Diagnostics;

namespace CodexRouter.Accounts;

public interface IExternalUriLauncher
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class WindowsExternalUriLauncher : IExternalUriLauncher
{
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Only absolute HTTP(S) authentication URLs can be opened.", nameof(uri));
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
        if (process is null)
        {
            throw new AccountServiceException("Windows did not accept the authentication URL launch request.");
        }
        process.Dispose();
        return Task.CompletedTask;
    }
}
