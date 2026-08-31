namespace CodexRouter.Storage;

internal static class StorageMigrations
{
    public static readonly IReadOnlyList<SqliteMigration> All = new[]
    {
        new SqliteMigration(1, "initial-router-schema", InitialSchema),
        new SqliteMigration(2, "route-decision-audit", RouteDecisionAuditSchema),
        new SqliteMigration(3, "orphan-thread-recovery", OrphanThreadRecoverySchema),
        new SqliteMigration(4, "runtime-state", RuntimeStateSchema),
        new SqliteMigration(5, "thread-migration-engine", ThreadMigrationSchema),
        new SqliteMigration(6, "account-lifecycle", AccountLifecycleSchema)
    };

    private const string InitialSchema = """
        CREATE TABLE accounts (
            id TEXT PRIMARY KEY,
            alias TEXT NOT NULL,
            email TEXT NULL,
            plan_type TEXT NULL,
            codex_home TEXT NOT NULL COLLATE NOCASE UNIQUE,
            enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0, 1)),
            priority INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            last_seen_at INTEGER NULL
        );

        CREATE TABLE account_preferences (
            account_id TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            route_weight REAL NOT NULL DEFAULT 1.0 CHECK(route_weight >= 0),
            short_reserve_percent INTEGER NOT NULL DEFAULT 15 CHECK(short_reserve_percent BETWEEN 0 AND 100),
            long_reserve_percent INTEGER NOT NULL DEFAULT 8 CHECK(long_reserve_percent BETWEEN 0 AND 100),
            updated_at INTEGER NOT NULL
        );

        CREATE TABLE quota_snapshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            fetched_at INTEGER NOT NULL,
            plan_type TEXT NULL,
            reached_type TEXT NULL,
            spend_control_reached INTEGER NULL CHECK(spend_control_reached IS NULL OR spend_control_reached IN (0, 1)),
            has_credits INTEGER NULL CHECK(has_credits IS NULL OR has_credits IN (0, 1)),
            unlimited_credits INTEGER NULL CHECK(unlimited_credits IS NULL OR unlimited_credits IN (0, 1)),
            credit_balance TEXT NULL
        );

        CREATE TABLE quota_buckets (
            snapshot_id INTEGER NOT NULL REFERENCES quota_snapshots(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            limit_id TEXT NOT NULL,
            limit_name TEXT NULL,
            slot TEXT NOT NULL CHECK(slot IN ('primary', 'secondary', 'other')),
            used_percent INTEGER NOT NULL CHECK(used_percent BETWEEN 0 AND 100),
            window_duration_seconds INTEGER NULL CHECK(window_duration_seconds IS NULL OR window_duration_seconds >= 0),
            resets_at INTEGER NULL,
            PRIMARY KEY(snapshot_id, ordinal)
        );

        CREATE TABLE usage_snapshots (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            fetched_at INTEGER NOT NULL,
            lifetime_tokens INTEGER NULL,
            peak_daily_tokens INTEGER NULL,
            longest_running_turn_seconds INTEGER NULL,
            current_streak_days INTEGER NULL,
            longest_streak_days INTEGER NULL
        );

        CREATE TABLE usage_daily_buckets (
            snapshot_id INTEGER NOT NULL REFERENCES usage_snapshots(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            start_date TEXT NOT NULL,
            tokens INTEGER NOT NULL CHECK(tokens >= 0),
            PRIMARY KEY(snapshot_id, ordinal)
        );

        CREATE TABLE thread_routes (
            thread_id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
            worker_id TEXT NOT NULL,
            reason TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            last_used_at INTEGER NOT NULL
        );

        CREATE TABLE worker_sessions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            worker_id TEXT NOT NULL,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            state TEXT NOT NULL,
            process_id INTEGER NULL,
            started_at INTEGER NOT NULL,
            ended_at INTEGER NULL,
            exit_code INTEGER NULL,
            failure TEXT NULL
        );

        CREATE TABLE health_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            state TEXT NOT NULL,
            reason TEXT NULL,
            cooldown_until INTEGER NULL,
            created_at INTEGER NOT NULL
        );

        CREATE TABLE router_settings (
            id INTEGER PRIMARY KEY CHECK(id = 1),
            mode TEXT NOT NULL,
            pinned_account_id TEXT NULL REFERENCES accounts(id) ON DELETE SET NULL,
            short_reserve_percent INTEGER NOT NULL CHECK(short_reserve_percent BETWEEN 0 AND 100),
            long_reserve_percent INTEGER NOT NULL CHECK(long_reserve_percent BETWEEN 0 AND 100),
            quota_stale_after_seconds INTEGER NOT NULL CHECK(quota_stale_after_seconds > 0),
            worker_idle_timeout_seconds INTEGER NOT NULL CHECK(worker_idle_timeout_seconds > 0),
            updated_at INTEGER NOT NULL
        );

        INSERT INTO router_settings(
            id, mode, pinned_account_id, short_reserve_percent, long_reserve_percent,
            quota_stale_after_seconds, worker_idle_timeout_seconds, updated_at)
        VALUES (1, 'auto', NULL, 15, 8, 300, 900, 0);

        CREATE TABLE compatibility_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            state TEXT NOT NULL,
            binary_path TEXT NULL,
            binary_version TEXT NULL,
            binary_sha256 TEXT NULL,
            checked_at INTEGER NOT NULL,
            report_json TEXT NOT NULL
        );

        CREATE TABLE migration_jobs (
            id TEXT PRIMARY KEY,
            source_thread_id TEXT NOT NULL,
            source_account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
            destination_account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
            destination_thread_id TEXT NULL,
            state TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            failure TEXT NULL
        );

        CREATE INDEX idx_quota_snapshots_account_fetched
            ON quota_snapshots(account_id, fetched_at DESC, id DESC);
        CREATE INDEX idx_usage_snapshots_account_fetched
            ON usage_snapshots(account_id, fetched_at DESC, id DESC);
        CREATE INDEX idx_thread_routes_account
            ON thread_routes(account_id, last_used_at DESC);
        CREATE INDEX idx_health_events_account_created
            ON health_events(account_id, created_at DESC, id DESC);
        CREATE INDEX idx_worker_sessions_account_started
            ON worker_sessions(account_id, started_at DESC, id DESC);
        CREATE INDEX idx_migration_jobs_state_updated
            ON migration_jobs(state, updated_at DESC);
        CREATE INDEX idx_compatibility_runs_checked
            ON compatibility_runs(checked_at DESC, id DESC);
        """;

    private const string AccountLifecycleSchema = """
        ALTER TABLE accounts
        ADD COLUMN lifecycle TEXT NOT NULL DEFAULT 'active'
            CHECK(lifecycle IN ('pending', 'active'));

        CREATE INDEX idx_accounts_lifecycle_priority
            ON accounts(lifecycle, priority DESC, created_at ASC, id ASC);
        """;

    private const string ThreadMigrationSchema = """
        CREATE TABLE thread_migration_jobs (
            id TEXT PRIMARY KEY,
            source_thread_id TEXT NOT NULL,
            source_account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
            target_account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT,
            target_thread_id TEXT NULL,
            state TEXT NOT NULL,
            snapshot_json TEXT NULL,
            handoff_text TEXT NULL,
            error TEXT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            completed_at INTEGER NULL,
            CHECK(source_account_id <> target_account_id)
        );

        CREATE UNIQUE INDEX idx_thread_migration_active_source_target
            ON thread_migration_jobs(source_thread_id, target_account_id)
            WHERE state NOT IN ('completed', 'canceled');
        CREATE INDEX idx_thread_migration_state_updated
            ON thread_migration_jobs(state, updated_at DESC);
        CREATE INDEX idx_thread_migration_target_thread
            ON thread_migration_jobs(target_thread_id)
            WHERE target_thread_id IS NOT NULL;

        CREATE TABLE thread_migration_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id TEXT NOT NULL REFERENCES thread_migration_jobs(id) ON DELETE CASCADE,
            from_state TEXT NULL,
            to_state TEXT NOT NULL,
            message TEXT NULL,
            created_at INTEGER NOT NULL
        );

        CREATE INDEX idx_thread_migration_events_job
            ON thread_migration_events(job_id, created_at ASC, id ASC);
        """;

    private const string RuntimeStateSchema = """
        CREATE TABLE runtime_state (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at INTEGER NOT NULL
        );
        """;

    private const string OrphanThreadRecoverySchema = """
        CREATE TABLE orphan_threads (
            thread_id TEXT NOT NULL,
            account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            worker_id TEXT NOT NULL,
            reason TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            resolved_at INTEGER NULL,
            PRIMARY KEY(thread_id, account_id)
        );

        CREATE INDEX idx_orphan_threads_unresolved
            ON orphan_threads(resolved_at, created_at DESC);
        """;

    private const string RouteDecisionAuditSchema = """
        CREATE TABLE route_decisions (
            id TEXT PRIMARY KEY,
            thread_id TEXT NULL,
            winner_account_id TEXT NULL REFERENCES accounts(id) ON DELETE SET NULL,
            reason TEXT NOT NULL,
            decision_json TEXT NOT NULL,
            created_at INTEGER NOT NULL
        );

        CREATE INDEX idx_route_decisions_thread_created
            ON route_decisions(thread_id, created_at DESC);
        CREATE INDEX idx_route_decisions_created
            ON route_decisions(created_at DESC);
        """;
}
