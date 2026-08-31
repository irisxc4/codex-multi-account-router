using CodexRouter.Domain;
using CodexRouter.Workers;
using Xunit;

namespace CodexRouter.Workers.Tests;

public sealed class ProfileMaterializerTests
{
    [Fact]
    public async Task Import_and_materialize_copies_root_hooks_json_without_copying_private_files()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-hooks");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = \"gpt-a\"");
            await File.WriteAllTextAsync(Path.Combine(source, "hooks.json"), "{\"hooks\":[\"\u4E2D\u6587 hook\"]}");
            Directory.CreateDirectory(Path.Combine(source, "hooks"));
            await File.WriteAllTextAsync(Path.Combine(source, "hooks", "notify.ps1"), "Write-Output '\u4E2D\u6587'");
            await File.WriteAllTextAsync(Path.Combine(source, "auth.json"), "DO-NOT-COPY");
            Directory.CreateDirectory(Path.Combine(source, "session"));
            await File.WriteAllTextAsync(Path.Combine(source, "session", "private.json"), "DO-NOT-COPY");

            var layout = new ProfileLayout(Path.Combine(root, "router"));
            var materializer = new ProfileMaterializer(layout);
            var template = await materializer.ImportSharedTemplateAsync(source);
            var account = new AccountId("a");
            var result = await materializer.MaterializeAsync(account, template);

            Assert.Equal("{\"hooks\":[\"\u4E2D\u6587 hook\"]}", await File.ReadAllTextAsync(Path.Combine(result.CodexHome, "hooks.json")));
            Assert.Equal("Write-Output '\u4E2D\u6587'", await File.ReadAllTextAsync(Path.Combine(result.CodexHome, "hooks", "notify.ps1")));
            Assert.False(File.Exists(Path.Combine(result.CodexHome, "auth.json")));
            Assert.False(Directory.Exists(Path.Combine(result.CodexHome, "session")));
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Missing_hooks_sync_only_fills_gaps_and_never_overwrites_account_state()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-hooks-repair");
        try
        {
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(Path.Combine(source, "hooks"));
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(source, "hooks.json"), "SOURCE-HOOKS");
            await File.WriteAllTextAsync(Path.Combine(source, "hooks", "existing.ps1"), "SOURCE-EXISTING");
            await File.WriteAllTextAsync(Path.Combine(source, "hooks", "missing.ps1"), "SOURCE-MISSING");
            await File.WriteAllTextAsync(Path.Combine(source, "auth.json"), "SOURCE-AUTH");
            await File.WriteAllTextAsync(Path.Combine(target, "hooks.json"), "ACCOUNT-HOOKS");
            Directory.CreateDirectory(Path.Combine(target, "hooks"));
            await File.WriteAllTextAsync(Path.Combine(target, "hooks", "existing.ps1"), "ACCOUNT-EXISTING");
            await File.WriteAllTextAsync(Path.Combine(target, "auth.json"), "ACCOUNT-AUTH");
            Directory.CreateDirectory(Path.Combine(target, "session"));
            await File.WriteAllTextAsync(Path.Combine(target, "session", "thread.json"), "ACCOUNT-SESSION");

            var materializer = new ProfileMaterializer(new ProfileLayout(Path.Combine(root, "router")));
            var result = await materializer.SynchronizeMissingHooksAsync(target, source);

            Assert.False(result.RootHooksCopied);
            Assert.Equal("ACCOUNT-HOOKS", await File.ReadAllTextAsync(Path.Combine(target, "hooks.json")));
            Assert.Equal("ACCOUNT-EXISTING", await File.ReadAllTextAsync(Path.Combine(target, "hooks", "existing.ps1")));
            Assert.Equal("SOURCE-MISSING", await File.ReadAllTextAsync(Path.Combine(target, "hooks", "missing.ps1")));
            Assert.Equal("ACCOUNT-AUTH", await File.ReadAllTextAsync(Path.Combine(target, "auth.json")));
            Assert.Equal("ACCOUNT-SESSION", await File.ReadAllTextAsync(Path.Combine(target, "session", "thread.json")));

            var samePath = await materializer.SynchronizeMissingHooksAsync(source, source);
            Assert.False(samePath.RootHooksCopied);
            Assert.Equal(0, samePath.HookFilesCopied);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Import_and_materialize_sanitizes_config_and_never_copies_private_state()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-import");
        try
        {
            var source = Path.Combine(root, "source-codex");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), """
                model = "gpt-5.6-codex"
                approval_policy = "on-request"
                tool_output_token_limit = 12345
                cli_auth_credentials_store = "file"
                made_up_private_setting = "drop-me"

                [features]
                web_search = true
                secret_auth_storage = false

                [mcp_servers.memoryguard]
                command = "memoryguard.exe"
                args = ["serve"]
                api_key = "DO-NOT-COPY"

                [mcp_servers.memoryguard.env]
                REAL_SECRET = "DO-NOT-COPY-EITHER"
                """);
            await File.WriteAllTextAsync(Path.Combine(source, "auth.json"), "super-secret-auth");
            Directory.CreateDirectory(Path.Combine(source, "sessions"));
            await File.WriteAllTextAsync(Path.Combine(source, "sessions", "session.json"), "private-session");
            Directory.CreateDirectory(Path.Combine(source, "skills", "shared-skill"));
            await File.WriteAllTextAsync(Path.Combine(source, "skills", "shared-skill", "SKILL.md"), "shared skill");
            Directory.CreateDirectory(Path.Combine(source, "plugins", "good"));
            await File.WriteAllTextAsync(Path.Combine(source, "plugins", "good", "plugin.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(source, "plugins", "good", ".env"), "SECRET=bad");

            var materializer = new ProfileMaterializer(new ProfileLayout(Path.Combine(root, "router")));
            var template = await materializer.ImportSharedTemplateAsync(source);
            var account = new AccountId("account-a");
            var result = await materializer.MaterializeAsync(account, template);

            var rendered = await File.ReadAllTextAsync(Path.Combine(result.CodexHome, "config.toml"));
            Assert.Contains("gpt-5.6-codex", rendered);
            Assert.Contains("tool_output_token_limit = 12345", rendered);
            Assert.Contains("cli_auth_credentials_store = \"keyring\"", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secret_auth_storage = true", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret_auth_storage = false", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("memoryguard.exe", rendered);
            Assert.DoesNotContain("DO-NOT-COPY", rendered);
            Assert.DoesNotContain("REAL_SECRET", rendered);
            Assert.DoesNotContain("made_up_private_setting", rendered);
            Assert.False(File.Exists(Path.Combine(result.CodexHome, "auth.json")));
            Assert.False(Directory.Exists(Path.Combine(result.CodexHome, "sessions")));
            Assert.True(File.Exists(Path.Combine(result.CodexHome, "skills", "shared-skill", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(result.CodexHome, "plugins", "good", "plugin.json")));
            Assert.False(File.Exists(Path.Combine(result.CodexHome, "plugins", "good", ".env")));

            var importBackup = Directory.EnumerateFiles(
                Path.Combine(root, "router", "shared", "imports"),
                "config.original.redacted.toml",
                SearchOption.AllDirectories).Single();
            var backupText = await File.ReadAllTextAsync(importBackup);
            Assert.DoesNotContain("DO-NOT-COPY", backupText);
            Assert.DoesNotContain("REAL_SECRET", backupText);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Account_override_cannot_disable_encrypted_keyring_storage_or_inject_secret()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-override");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), """
                model = "gpt-base"
                approval_policy = "on-request"
                """);

            var layout = new ProfileLayout(Path.Combine(root, "router"));
            var materializer = new ProfileMaterializer(layout);
            var template = await materializer.ImportSharedTemplateAsync(source);
            var account = new AccountId("a");
            Directory.CreateDirectory(layout.ProfileRoot(account));
            await File.WriteAllTextAsync(layout.OverridePath(account), """
                model = "gpt-account-a"
                cli_auth_credentials_store = "file"
                api_key = "bad"

                [features]
                web_search = false
                secret_auth_storage = false
                """);

            var result = await materializer.MaterializeAsync(account, template);
            var rendered = await File.ReadAllTextAsync(Path.Combine(result.CodexHome, "config.toml"));

            Assert.Contains("gpt-account-a", rendered);
            Assert.Contains("cli_auth_credentials_store = \"keyring\"", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secret_auth_storage = true", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret_auth_storage = false", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api_key", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bad", rendered);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Rematerialization_preserves_auth_sessions_and_unmanaged_files()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-rematerialize");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = " + '"' + "gpt-a" + '"');
            Directory.CreateDirectory(Path.Combine(source, "skills", "managed"));
            await File.WriteAllTextAsync(Path.Combine(source, "skills", "managed", "SKILL.md"), "v1");

            var layout = new ProfileLayout(Path.Combine(root, "router"));
            var materializer = new ProfileMaterializer(layout);
            var template = await materializer.ImportSharedTemplateAsync(source);
            var account = new AccountId("a");
            var first = await materializer.MaterializeAsync(account, template);

            await File.WriteAllTextAsync(Path.Combine(first.CodexHome, "auth.json"), "AUTH-MUST-STAY");
            Directory.CreateDirectory(Path.Combine(first.CodexHome, "sessions"));
            await File.WriteAllTextAsync(Path.Combine(first.CodexHome, "sessions", "thread.json"), "SESSION-MUST-STAY");
            Directory.CreateDirectory(Path.Combine(first.CodexHome, "skills", "personal"));
            await File.WriteAllTextAsync(Path.Combine(first.CodexHome, "skills", "personal", "SKILL.md"), "PERSONAL");

            await materializer.MaterializeAsync(account, template);

            Assert.Equal("AUTH-MUST-STAY", await File.ReadAllTextAsync(Path.Combine(first.CodexHome, "auth.json")));
            Assert.Equal("SESSION-MUST-STAY", await File.ReadAllTextAsync(Path.Combine(first.CodexHome, "sessions", "thread.json")));
            Assert.Equal("PERSONAL", await File.ReadAllTextAsync(Path.Combine(first.CodexHome, "skills", "personal", "SKILL.md")));
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Drift_detection_reports_config_and_managed_asset_changes()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-drift");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = \"gpt-a\"");
            Directory.CreateDirectory(Path.Combine(source, "rules"));
            await File.WriteAllTextAsync(Path.Combine(source, "rules", "base.rules"), "allow");

            var materializer = new ProfileMaterializer(new ProfileLayout(Path.Combine(root, "router")));
            var template = await materializer.ImportSharedTemplateAsync(source);
            var account = new AccountId("a");
            var profile = await materializer.MaterializeAsync(account, template);
            Assert.False((await materializer.DetectDriftAsync(account)).HasDrift);

            await File.AppendAllTextAsync(Path.Combine(profile.CodexHome, "config.toml"), "\n# changed\n");
            await File.WriteAllTextAsync(Path.Combine(profile.CodexHome, "rules", "base.rules"), "changed");

            var drift = await materializer.DetectDriftAsync(account);
            Assert.True(drift.HasDrift);
            Assert.Contains("config.toml", drift.Paths);
            Assert.Contains("rules/base.rules", drift.Paths);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Removed_managed_asset_is_deleted_only_if_user_did_not_modify_it()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-managed-delete");
        try
        {
            var source1 = Path.Combine(root, "source1");
            Directory.CreateDirectory(source1);
            await File.WriteAllTextAsync(Path.Combine(source1, "config.toml"), "model = \"gpt-a\"");
            Directory.CreateDirectory(Path.Combine(source1, "prompts"));
            await File.WriteAllTextAsync(Path.Combine(source1, "prompts", "old.md"), "managed-v1");

            var source2 = Path.Combine(root, "source2");
            Directory.CreateDirectory(source2);
            await File.WriteAllTextAsync(Path.Combine(source2, "config.toml"), "model = \"gpt-b\"");

            var layout = new ProfileLayout(Path.Combine(root, "router"));
            var materializer = new ProfileMaterializer(layout);
            var template1 = await materializer.ImportSharedTemplateAsync(source1);
            var template2 = await materializer.ImportSharedTemplateAsync(source2);

            var cleanAccount = new AccountId("clean");
            var clean = await materializer.MaterializeAsync(cleanAccount, template1);
            await materializer.MaterializeAsync(cleanAccount, template2);
            Assert.False(File.Exists(Path.Combine(clean.CodexHome, "prompts", "old.md")));

            var changedAccount = new AccountId("changed");
            var changed = await materializer.MaterializeAsync(changedAccount, template1);
            var changedPath = Path.Combine(changed.CodexHome, "prompts", "old.md");
            await File.WriteAllTextAsync(changedPath, "user-changed");
            await materializer.MaterializeAsync(changedAccount, template2);
            Assert.Equal("user-changed", await File.ReadAllTextAsync(changedPath));
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Invalid_source_toml_fails_without_creating_template()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-invalid");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "broken = [");
            var layout = new ProfileLayout(Path.Combine(root, "router"));
            var materializer = new ProfileMaterializer(layout);

            await Assert.ThrowsAsync<ProfileMaterializationException>(() => materializer.ImportSharedTemplateAsync(source));
            Assert.False(Directory.Exists(layout.TemplatesRoot) && Directory.EnumerateDirectories(layout.TemplatesRoot).Any());
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Different_accounts_receive_distinct_codex_homes_from_same_template()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-isolation");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = \"gpt-a\"");
            var materializer = new ProfileMaterializer(new ProfileLayout(Path.Combine(root, "router")));
            var template = await materializer.ImportSharedTemplateAsync(source);

            var a = await materializer.MaterializeAsync(new AccountId("a"), template);
            var b = await materializer.MaterializeAsync(new AccountId("b"), template);

            Assert.NotEqual(a.CodexHome, b.CodexHome);
            Assert.Equal(a.ConfigSha256, b.ConfigSha256);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Repeated_import_of_same_content_reuses_one_template_directory()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-content-addressed");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "skills"));
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = \"gpt-a\"");
            await File.WriteAllTextAsync(Path.Combine(source, "hooks.json"), "{\"hooks\":[]}");
            await File.WriteAllTextAsync(Path.Combine(source, "skills", "common.md"), "same-content");

            var layout = new ProfileLayout(Path.Combine(root, "router"));
            var materializer = new ProfileMaterializer(layout);
            var first = await materializer.ImportSharedTemplateAsync(source);
            var second = await materializer.ImportSharedTemplateAsync(source);

            Assert.Equal(first.DirectoryPath, second.DirectoryPath);
            Assert.Single(Directory.EnumerateDirectories(layout.TemplatesRoot));
            Assert.StartsWith("content-", first.Metadata.Version, StringComparison.Ordinal);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Asset_or_root_hook_change_creates_a_new_content_template()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-content-change");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "skills"));
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = \"gpt-a\"");
            await File.WriteAllTextAsync(Path.Combine(source, "skills", "common.md"), "v1");
            var materializer = new ProfileMaterializer(new ProfileLayout(Path.Combine(root, "router")));
            var first = await materializer.ImportSharedTemplateAsync(source);

            await File.WriteAllTextAsync(Path.Combine(source, "skills", "common.md"), "v2");
            var second = await materializer.ImportSharedTemplateAsync(source);
            Assert.NotEqual(first.Metadata.Version, second.Metadata.Version);

            await File.WriteAllTextAsync(Path.Combine(source, "hooks.json"), "{\"hooks\":[\"changed\"]}");
            var third = await materializer.ImportSharedTemplateAsync(source);
            Assert.NotEqual(second.Metadata.Version, third.Metadata.Version);
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Runtime_plugin_objects_are_deduplicated_and_normal_assets_remain_copies()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-runtime-objects");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "plugins", ".plugin-appserver"));
            Directory.CreateDirectory(Path.Combine(source, "plugins", "editable"));
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = \"gpt-a\"");
            await File.WriteAllTextAsync(Path.Combine(source, "plugins", ".plugin-appserver", "runtime.bin"), "runtime");
            await File.WriteAllTextAsync(Path.Combine(source, "plugins", "editable", "plugin.json"), "editable");

            var layout = new ProfileLayout(Path.Combine(root, "router"));
            var materializer = new ProfileMaterializer(layout);
            var template = await materializer.ImportSharedTemplateAsync(source);
            var first = await materializer.MaterializeAsync(new AccountId("a"), template);
            var second = await materializer.MaterializeAsync(new AccountId("b"), template);

            var objects = Directory.EnumerateFiles(layout.ObjectsRoot).ToArray();
            Assert.Single(objects);
            Assert.Equal("runtime", await File.ReadAllTextAsync(objects[0]));
            Assert.Equal("runtime", await File.ReadAllTextAsync(Path.Combine(first.CodexHome, "plugins", ".plugin-appserver", "runtime.bin")));
            Assert.Equal("runtime", await File.ReadAllTextAsync(Path.Combine(second.CodexHome, "plugins", ".plugin-appserver", "runtime.bin")));
            Assert.Equal("editable", await File.ReadAllTextAsync(Path.Combine(first.CodexHome, "plugins", "editable", "plugin.json")));
            Assert.Equal("editable", await File.ReadAllTextAsync(Path.Combine(second.CodexHome, "plugins", "editable", "plugin.json")));
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    [Fact]
    public async Task Compaction_updates_profile_reference_and_removes_duplicate_legacy_template()
    {
        var root = WorkerTestHelpers.CreateTempRoot("profile-compaction");
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "config.toml"), "model = \"gpt-a\"");
            var layout = new ProfileLayout(Path.Combine(root, "router"));
            var materializer = new ProfileMaterializer(layout);
            var template = await materializer.ImportSharedTemplateAsync(source);
            var profile = await materializer.MaterializeAsync(new AccountId("a"), template);

            var legacy = Path.Combine(layout.TemplatesRoot, "20240101000000-legacy");
            CopyDirectory(template.DirectoryPath, legacy);
            var legacyMetadataPath = Path.Combine(legacy, "metadata.json");
            var legacyText = await File.ReadAllTextAsync(legacyMetadataPath);
            legacyText = legacyText.Replace(template.Metadata.Version, "20240101000000-legacy", StringComparison.Ordinal);
            await File.WriteAllTextAsync(legacyMetadataPath, legacyText);
            var profileMetadataPath = Path.Combine(profile.CodexHome, ".codex-router-profile.json");
            var profileText = await File.ReadAllTextAsync(profileMetadataPath);
            profileText = profileText.Replace(template.Metadata.Version, "20240101000000-legacy", StringComparison.Ordinal);
            await File.WriteAllTextAsync(profileMetadataPath, profileText);

            var report = await materializer.CompactTemplatesAsync(maxUnreferencedHistory: 0);

            Assert.Equal(1, report.DuplicateTemplatesRemoved);
            Assert.True(Directory.Exists(template.DirectoryPath));
            Assert.False(Directory.Exists(legacy));
            Assert.Contains(template.Metadata.Version, await File.ReadAllTextAsync(profileMetadataPath));
        }
        finally
        {
            WorkerTestHelpers.Cleanup(root);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
