using CodexRouter.Domain;

namespace CodexRouter.Migration;

public enum ThreadMigrationState
{
    Pending,
    Snapshotting,
    CreatingTarget,
    Seeding,
    Completed,
    Failed,
    Canceled
}

public sealed record ThreadMigrationSnapshot(
    string Version,
    ThreadId SourceThreadId,
    AccountId SourceAccountId,
    AccountId TargetAccountId,
    string? Cwd,
    string? GitBranch,
    string? GitCommit,
    string? GitStatus,
    string? GitDiff,
    IReadOnlyList<string> RelevantFiles,
    string TaskGoal,
    string CompletedWork,
    string PendingWork,
    string RecentVisibleContext,
    DateTimeOffset CapturedAt);

public sealed record ThreadMigrationJob(
    string Id,
    ThreadId SourceThreadId,
    AccountId SourceAccountId,
    AccountId TargetAccountId,
    ThreadId? TargetThreadId,
    ThreadMigrationState State,
    ThreadMigrationSnapshot? Snapshot,
    string? HandoffText,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ThreadMigrationStartResult(string JobId, ThreadMigrationState State);

public sealed record GitWorkspaceSnapshot(
    string? Branch,
    string? Commit,
    string? Status,
    string? Diff,
    IReadOnlyList<string> RelevantFiles);

public class ThreadMigrationException : Exception
{
    public ThreadMigrationException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class ThreadMigrationCanceledException : ThreadMigrationException
{
    public ThreadMigrationCanceledException(string jobId) : base($"Thread migration '{jobId}' was canceled.") => JobId = jobId;
    public string JobId { get; }
}
