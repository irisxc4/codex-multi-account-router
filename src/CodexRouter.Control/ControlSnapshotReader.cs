using CodexRouter.Domain;
using CodexRouter.Storage;

namespace CodexRouter.Control;

public sealed class ControlSnapshotReader
{
    private readonly RouterRepository _repository;

    public ControlSnapshotReader(RouterRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<ControlSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _repository.GetRouterSettingsAsync(cancellationToken).ConfigureAwait(false);
        var current = await _repository.GetRuntimeStateAsync("front_account_id", cancellationToken).ConfigureAwait(false);
        var currentThread = await _repository.GetRuntimeStateAsync("front_thread_id", cancellationToken).ConfigureAwait(false);
        var accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var views = new List<ControlAccountView>(accounts.Count);
        foreach (var stored in accounts)
        {
            var quotaTask = _repository.GetLatestQuotaSnapshotAsync(stored.Profile.Id, cancellationToken);
            var healthTask = _repository.GetHealthEventsAsync(stored.Profile.Id, 1, cancellationToken);
            await Task.WhenAll(quotaTask, healthTask).ConfigureAwait(false);
            var quota = await quotaTask.ConfigureAwait(false);
            var health = (await healthTask.ConfigureAwait(false)).FirstOrDefault()?.Health;
            views.Add(new ControlAccountView(
                stored.Profile.Id.Value,
                stored.Profile.Alias,
                stored.Profile.Email,
                stored.Profile.PlanType,
                stored.Profile.Enabled,
                stored.Profile.Priority,
                (health?.State ?? AccountHealthState.Unknown).ToString(),
                health?.Reason,
                string.Equals(current?.Value, stored.Profile.Id.Value, StringComparison.Ordinal),
                quota?.FetchedAt,
                quota?.Buckets.Select(bucket => new ControlQuotaBucket(
                    bucket.LimitId,
                    bucket.LimitName,
                    bucket.Slot.ToString(),
                    bucket.UsedPercent,
                    bucket.RemainingPercent,
                    bucket.WindowDuration?.TotalMinutes,
                    bucket.ResetsAt)).ToArray() ?? Array.Empty<ControlQuotaBucket>()));
        }

        return new ControlSnapshot(
            settings.Mode.ToString(),
            settings.PinnedAccountId?.Value,
            current?.Value,
            currentThread?.Value,
            views,
            DateTimeOffset.UtcNow);
    }
}
