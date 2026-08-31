using CodexRouter.Accounts;
using CodexRouter.Domain;
using CodexRouter.Storage;
using CodexRouter.Workers;

namespace CodexRouter.Control;

public sealed record ChatGptSessionOnboardingResult(
    string AccountId,
    string? Email,
    string? PlanType,
    DateTimeOffset ImportedAt);

/// <summary>
/// Converts an already-authenticated ChatGPT web session into the durable AgentIdentity
/// auth mode supported by official Codex. The web access token is used only for the
/// one-time Agent Identity registration request and is never persisted by Router.
/// </summary>
public sealed class ChatGptSessionAgentIdentityOnboarding
{
    private readonly AccountService _accounts;
    private readonly ProfileMaterializer _materializer;
    private readonly string _templateSourceCodexHome;
    private readonly IAgentIdentityRegistrar _registrar;
    private readonly ICodexCredentialWriter _credentialWriter;

    public ChatGptSessionAgentIdentityOnboarding(
        AccountService accounts,
        ProfileMaterializer materializer,
        string templateSourceCodexHome,
        IAgentIdentityRegistrar? registrar = null,
        ICodexCredentialWriter? credentialWriter = null)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _templateSourceCodexHome = Path.GetFullPath(
            string.IsNullOrWhiteSpace(templateSourceCodexHome)
                ? throw new ArgumentException("Template source CODEX_HOME is required.", nameof(templateSourceCodexHome))
                : templateSourceCodexHome);
        _registrar = registrar ?? new AgentIdentityRegistrar();
        _credentialWriter = credentialWriter ?? new CodexDirectKeyringStore();
    }

    public async Task<ChatGptSessionOnboardingResult> ImportAsync(
        string alias,
        string sessionJson,
        string? proxyUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Account alias cannot be empty.", nameof(alias));
        if (!Directory.Exists(_templateSourceCodexHome))
        {
            throw new DirectoryNotFoundException($"Template source CODEX_HOME does not exist: {_templateSourceCodexHome}");
        }

        // Parse before creating any profile so malformed or expired browser data leaves no state behind.
        var session = ChatGptSessionImportParser.Parse(sessionJson);
        var normalizedProxy = CodexLoginProxy.Normalize(proxyUrl);
        if (normalizedProxy is not null && new Uri(normalizedProxy).Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("ChatGPT Session import currently supports HTTP/HTTPS proxies only.", nameof(proxyUrl));
        }

        var template = await _materializer.ImportSharedTemplateAsync(_templateSourceCodexHome, cancellationToken)
            .ConfigureAwait(false);
        var profile = await _accounts.CreateAccountProfileAsync(
            alias.Trim(),
            template,
            enabled: false,
            lifecycle: AccountLifecycle.Pending,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var credentialSaved = false;
        try
        {
            await ProfileWorkerNetworkRoute.SaveProxyAsync(profile.CodexHome, normalizedProxy, cancellationToken)
                .ConfigureAwait(false);

            var identity = await _registrar.RegisterAsync(session, normalizedProxy, cancellationToken)
                .ConfigureAwait(false);

            // From this point on the browser bearer token is no longer needed. Only the
            // AgentIdentity record is persisted, using the official Codex direct-keyring schema.
            session = session with { AccessToken = string.Empty };
            await _credentialWriter.SaveAgentIdentityAsync(profile.CodexHome, identity, cancellationToken)
                .ConfigureAwait(false);
            credentialSaved = true;

            var verified = await _accounts.CompletePendingExternalLoginAsync(profile.Id, cancellationToken)
                .ConfigureAwait(false);
            var resolvedEmail = verified.Email ?? session.Email;
            try
            {
                _ = await _accounts.RefreshQuotaAsync(verified.Id, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Quota is observational metadata. A valid authenticated account must not be
                // rolled back merely because the quota endpoint is temporarily unavailable.
            }

            return new ChatGptSessionOnboardingResult(
                verified.Id.Value,
                resolvedEmail,
                verified.PlanType ?? session.PlanType,
                DateTimeOffset.UtcNow);
        }
        catch (Exception importFailure)
        {
            Exception? credentialRollbackFailure = null;
            if (credentialSaved)
            {
                try
                {
                    await _credentialWriter.DeleteAsync(profile.CodexHome, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    credentialRollbackFailure = ex;
                }
            }

            // If the keyring rollback failed, keep the pending profile directory intact so the
            // CODEX_HOME-derived credential key remains recoverable instead of orphaning it.
            if (credentialRollbackFailure is null)
            {
                try
                {
                    await _accounts.DeleteAccountAsync(profile.Id, force: true, cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception profileRollbackFailure)
                {
                    throw new InvalidOperationException(
                        $"{importFailure.Message}; pending-profile rollback failed: {profileRollbackFailure.Message}",
                        importFailure);
                }
                throw;
            }

            throw new InvalidOperationException(
                $"{importFailure.Message}; Codex keyring rollback failed: {credentialRollbackFailure.Message}. The pending profile was retained for safe recovery.",
                importFailure);
        }
    }
}
