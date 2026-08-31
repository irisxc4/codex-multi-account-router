using System.Text;
using System.Text.Json;
using CodexRouter.Domain;

namespace CodexRouter.Migration;

public sealed class ThreadSnapshotBuilder
{
    private readonly IGitSnapshotProvider _git;
    private readonly int _maxVisibleContextChars;

    public ThreadSnapshotBuilder(IGitSnapshotProvider? git = null, int maxVisibleContextChars = 24_000)
    {
        _git = git ?? new GitSnapshotProvider();
        _maxVisibleContextChars = Math.Max(4_000, maxVisibleContextChars);
    }

    public async Task<ThreadMigrationSnapshot> BuildAsync(
        ThreadId sourceThreadId,
        AccountId sourceAccountId,
        AccountId targetAccountId,
        JsonElement threadReadResult,
        CancellationToken cancellationToken = default)
    {
        var thread = UnwrapThread(threadReadResult);
        var cwd = TryGetString(thread, "cwd");
        var git = await _git.CaptureAsync(cwd, cancellationToken).ConfigureAwait(false);
        var visibleMessages = ExtractVisibleMessages(thread);
        var taskGoal = visibleMessages.FirstOrDefault(message => message.Role == "user")?.Text
            ?? "No deterministic user task goal was recoverable from the visible thread payload.";
        var recentContext = BuildRecentVisibleContext(visibleMessages);

        return new ThreadMigrationSnapshot(
            "1",
            sourceThreadId,
            sourceAccountId,
            targetAccountId,
            cwd,
            git.Branch,
            git.Commit,
            git.Status,
            git.Diff,
            git.RelevantFiles,
            Trim(taskGoal, 6_000),
            BuildCompletedWork(git),
            "Not inferred as fact by Codex Router. Review the recent visible context and repository state before continuing.",
            recentContext,
            DateTimeOffset.UtcNow);
    }

    public string BuildHandoffText(ThreadMigrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder();
        builder.AppendLine("[Codex Router migration handoff]");
        builder.AppendLine();
        builder.AppendLine($"This is a NEW thread explicitly migrated from `{snapshot.SourceThreadId.Value}`.");
        builder.AppendLine($"Source account: `{snapshot.SourceAccountId.Value}`");
        builder.AppendLine($"Target account: `{snapshot.TargetAccountId.Value}`");
        builder.AppendLine("Do not assume this is the original thread. Verify the repository state before taking action.");
        builder.AppendLine();
        builder.AppendLine("## Task goal recovered from visible context");
        builder.AppendLine(snapshot.TaskGoal);
        builder.AppendLine();
        builder.AppendLine("## Repository / completed-work evidence");
        builder.AppendLine(snapshot.CompletedWork);
        builder.AppendLine();
        builder.AppendLine("## Pending work");
        builder.AppendLine(snapshot.PendingWork);
        builder.AppendLine();
        builder.AppendLine("## Workspace");
        builder.AppendLine($"CWD: {snapshot.Cwd ?? "unknown"}");
        builder.AppendLine($"Git branch: {snapshot.GitBranch ?? "unknown"}");
        builder.AppendLine($"Git commit: {snapshot.GitCommit ?? "unknown"}");
        if (snapshot.RelevantFiles.Count > 0)
        {
            builder.AppendLine("Relevant changed files:");
            foreach (var file in snapshot.RelevantFiles.Take(80)) builder.AppendLine($"- {file}");
        }
        if (!string.IsNullOrWhiteSpace(snapshot.GitStatus))
        {
            builder.AppendLine();
            builder.AppendLine("### git status --short");
            builder.AppendLine("```text");
            builder.AppendLine(snapshot.GitStatus);
            builder.AppendLine("```");
        }
        if (!string.IsNullOrWhiteSpace(snapshot.GitDiff))
        {
            builder.AppendLine();
            builder.AppendLine("### git diff snapshot");
            builder.AppendLine("```diff");
            builder.AppendLine(Trim(snapshot.GitDiff, 60_000));
            builder.AppendLine("```");
        }
        builder.AppendLine();
        builder.AppendLine("## Recent visible conversation context");
        builder.AppendLine(snapshot.RecentVisibleContext);
        builder.AppendLine();
        builder.AppendLine("Router intentionally does not copy hidden chain-of-thought/reasoning. Continue from visible context and workspace facts only.");
        return Trim(builder.ToString(), 90_000);
    }

    private string BuildRecentVisibleContext(IReadOnlyList<VisibleMessage> messages)
    {
        if (messages.Count == 0)
        {
            return "No visible user/assistant messages were recoverable from the source thread payload.";
        }
        var builder = new StringBuilder();
        foreach (var message in messages.TakeLast(16))
        {
            var entry = $"{message.Role.ToUpperInvariant()}: {message.Text.Trim()}\n\n";
            if (builder.Length + entry.Length > _maxVisibleContextChars)
            {
                var remaining = _maxVisibleContextChars - builder.Length;
                if (remaining > 0) builder.Append(entry.AsSpan(0, Math.Min(remaining, entry.Length)));
                break;
            }
            builder.Append(entry);
        }
        return builder.ToString().Trim();
    }

    private static string BuildCompletedWork(GitWorkspaceSnapshot git)
    {
        var parts = new List<string>
        {
            "Codex Router does not semantically guess which conversational tasks are complete. The following repository facts are captured as evidence."
        };
        if (!string.IsNullOrWhiteSpace(git.Status)) parts.Add("The working tree contains changes listed in the git status section below.");
        else if (git.Commit is not null) parts.Add("The captured Git working tree reported no short-status changes.");
        else parts.Add("No Git work-tree evidence was available.");
        if (git.RelevantFiles.Count > 0) parts.Add($"Changed/untracked file count captured: {git.RelevantFiles.Count}.");
        return string.Join(" ", parts);
    }

    private static JsonElement UnwrapThread(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("thread", out var thread) &&
            thread.ValueKind == JsonValueKind.Object)
        {
            return thread;
        }
        if (result.ValueKind == JsonValueKind.Object) return result;
        throw new ThreadMigrationException("thread/read response does not contain a thread object.");
    }

    private static IReadOnlyList<VisibleMessage> ExtractVisibleMessages(JsonElement thread)
    {
        var output = new List<VisibleMessage>();
        if (!thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = TryGetString(item, "type") ?? string.Empty;
                if (type.Contains("reasoning", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("analysis", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var role = InferRole(type, item);
                if (role is null) continue;
                var text = ExtractVisibleText(item);
                if (!string.IsNullOrWhiteSpace(text)) output.Add(new VisibleMessage(role, Trim(text, 8_000)));
            }
        }
        return output;
    }

    private static string? InferRole(string type, JsonElement item)
    {
        if (type.Contains("user", StringComparison.OrdinalIgnoreCase)) return "user";
        if (type.Contains("agent", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("assistant", StringComparison.OrdinalIgnoreCase)) return "assistant";
        var role = TryGetString(item, "role");
        if (role is "user" or "assistant") return role;
        return null;
    }

    private static string ExtractVisibleText(JsonElement item)
    {
        if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }
        if (item.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) parts.Add(part.GetString() ?? string.Empty);
                    else if (part.ValueKind == JsonValueKind.Object &&
                             part.TryGetProperty("text", out var partText) &&
                             partText.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(partText.GetString() ?? string.Empty);
                    }
                }
                return string.Join("\n", parts.Where(static value => !string.IsNullOrWhiteSpace(value)));
            }
        }
        return string.Empty;
    }

    private static string? TryGetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Trim(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n...[truncated]";

    private sealed record VisibleMessage(string Role, string Text);
}
