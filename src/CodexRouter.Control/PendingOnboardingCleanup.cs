using CodexRouter.Accounts;
using CodexRouter.Storage;

namespace CodexRouter.Control;

public static class PendingOnboardingCleanup
{
    public static async Task<int> CleanupAsync(
        RouterRepository repository,
        AccountService accounts,
        ICodexCredentialWriter? credentialWriter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(accounts);
        credentialWriter ??= new CodexDirectKeyringStore();

        var pending = (await repository.ListAllAccountsAsync(cancellationToken).ConfigureAwait(false))
            .Where(static account => account.Lifecycle == AccountLifecycle.Pending)
            .ToArray();
        var removed = 0;
        foreach (var account in pending)
        {
            // The credential key is derived from CODEX_HOME, so never remove the profile
            // directory until the corresponding keyring entry has been successfully deleted.
            // DeleteAsync treats a missing credential as success.
            await credentialWriter.DeleteAsync(account.Profile.CodexHome, cancellationToken).ConfigureAwait(false);
            await accounts.DeleteAccountAsync(account.Profile.Id, force: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            removed++;
        }
        return removed;
    }
}
