using CodexRouter.Accounts;
using CodexRouter.Domain;

namespace CodexRouter.Control;

/// <summary>
/// Removes credentials before deleting a pending CODEX_HOME. The credential key is
/// derived from that directory, so a failed keyring deletion must retain the profile
/// for deterministic recovery instead of orphaning an unreachable secret.
/// </summary>
public static class PendingOnboardingRollback
{
    public static async Task<string?> TryRollbackAsync(
        AccountService accounts,
        ICodexCredentialWriter credentialWriter,
        AccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(credentialWriter);
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            await credentialWriter.DeleteAsync(profile.CodexHome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Codex keyring rollback failed: {ex.Message}. The pending profile was retained for safe recovery.";
        }

        try
        {
            await accounts.DeleteAccountAsync(
                profile.Id,
                force: true,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return $"Pending-profile rollback failed: {ex.Message}";
        }
    }
}
