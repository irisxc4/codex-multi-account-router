using System.Security.Cryptography;
using System.Text;

namespace CodexRouter.Control;

public sealed class ControlEndpoint
{
    public ControlEndpoint(string root)
    {
        Root = Path.GetFullPath(root);
        TokenPath = Path.Combine(Root, "control.token");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant()));
        PipeName = "codex-router-control-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public string Root { get; }
    public string TokenPath { get; }
    public string PipeName { get; }

    public async Task<string> GetOrCreateTokenAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);
        if (File.Exists(TokenPath))
        {
            var existing = (await File.ReadAllTextAsync(TokenPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (existing.Length >= 32)
            {
                return existing;
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var temp = TokenPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temp, token, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temp, TokenPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(TokenPath))
            {
                // Another Router process won the token creation race.
            }
            // Do not mark the token file Hidden. On Windows, CREATE_ALWAYS against a hidden file
            // can fail with AccessDenied, which breaks token rotation and repair. The token's
            // confidentiality comes from its random value and per-user control channel, not the Hidden bit.
            return (await File.ReadAllTextAsync(TokenPath, cancellationToken).ConfigureAwait(false)).Trim();
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    public async Task<string> ReadTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(TokenPath))
        {
            throw new FileNotFoundException("Codex Router control token does not exist.", TokenPath);
        }
        var token = (await File.ReadAllTextAsync(TokenPath, cancellationToken).ConfigureAwait(false)).Trim();
        if (token.Length < 32)
        {
            throw new InvalidDataException("Codex Router control token is invalid.");
        }
        return token;
    }
}
