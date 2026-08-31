using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexRouter.Control;
using CodexRouter.Domain;
using CodexRouter.Workers;
using Xunit;

namespace CodexRouter.Control.Tests;

public sealed class AgentIdentityImportTests
{
    [Fact]
    public void Web_session_parser_extracts_account_identity_without_requiring_refresh_token()
    {
        var accessToken = Jwt(new Dictionary<string, object?>
        {
            ["sub"] = "user-subject",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = "person@example.test",
            ["https://api.openai.com/auth"] = new
            {
                chatgpt_user_id = "user-123",
                chatgpt_account_id = "account-from-jwt",
                chatgpt_plan_type = "plus",
                chatgpt_account_is_fedramp = false
            }
        });
        var sessionJson = JsonSerializer.Serialize(new
        {
            accessToken,
            account = new { id = "account-from-jwt", planType = "plus" },
            user = new { email = "fallback@example.test" }
        });

        var parsed = ChatGptSessionImportParser.Parse(sessionJson);

        Assert.Equal(accessToken, parsed.AccessToken);
        Assert.Equal("account-from-jwt", parsed.AccountId);
        Assert.Equal("user-123", parsed.ChatGptUserId);
        Assert.Equal("person@example.test", parsed.Email);
        Assert.Equal("plus", parsed.PlanType);
        Assert.False(parsed.IsFedRamp);
        Assert.NotNull(parsed.ExpiresAt);
        Assert.Empty(parsed.SafeAudience);
        Assert.Empty(parsed.SafeScopes);
    }

    [Fact]
    public void Web_session_parser_rejects_selected_account_that_differs_from_token_binding()
    {
        var accessToken = Jwt(new Dictionary<string, object?>
        {
            ["sub"] = "user-123",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new
            {
                chatgpt_user_id = "user-123",
                chatgpt_account_id = "account-from-token",
                chatgpt_plan_type = "plus"
            }
        });
        var sessionJson = JsonSerializer.Serialize(new
        {
            accessToken,
            account = new { id = "different-selected-account", planType = "plus" }
        });

        var error = Assert.Throws<InvalidOperationException>(() => ChatGptSessionImportParser.Parse(sessionJson));

        Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(accessToken, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_session_parser_rejects_expired_access_token()
    {
        var token = Jwt(new Dictionary<string, object?>
        {
            ["sub"] = "user-123",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new
            {
                chatgpt_user_id = "user-123",
                chatgpt_account_id = "account-123",
                chatgpt_plan_type = "plus"
            }
        });
        var json = JsonSerializer.Serialize(new { accessToken = token });

        var error = Assert.Throws<InvalidOperationException>(() => ChatGptSessionImportParser.Parse(json));
        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_identity_registration_uses_bearer_only_for_registration_and_returns_durable_record()
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(client, "router-test");
        var session = new ChatGptSessionImport(
            "test-web-access-token",
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1));

        var identity = await registrar.RegisterAsync(session, proxyUrl: null);

        Assert.Equal("runtime-123", identity.AgentRuntimeId);
        Assert.Equal("account-123", identity.AccountId);
        Assert.Equal("user-123", identity.ChatGptUserId);
        Assert.Equal("plus", identity.PlanType);
        Assert.Null(identity.TaskId);
        Assert.Equal(2, handler.PostAuthorizations.Count);
        Assert.Equal("codex-router-route-preflight", handler.PostAuthorizations[0]?.Parameter);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("test-web-access-token", handler.Authorization?.Parameter);
        Assert.All(handler.PostOriginators, static value => Assert.Equal("codex_cli_rs", value));
        Assert.All(handler.PostUserAgents, static value => Assert.StartsWith("codex_cli_rs/router-test ", value, StringComparison.Ordinal));
        Assert.NotNull(handler.Body);
        using var requestJson = JsonDocument.Parse(handler.Body!);
        Assert.Equal("codex-cli", requestJson.RootElement.GetProperty("abom").GetProperty("agent_harness_id").GetString());
        Assert.Equal("cli-windows", requestJson.RootElement.GetProperty("abom").GetProperty("running_location").GetString());
        Assert.StartsWith("ssh-ed25519 ", requestJson.RootElement.GetProperty("agent_public_key").GetString());
        Assert.Equal("responsesapi", requestJson.RootElement.GetProperty("capabilities")[0].GetString());
        Assert.True(Convert.FromBase64String(identity.AgentPrivateKey).Length > 32);
    }

    [Fact]
    public async Task Agent_identity_registration_preflight_401_allows_real_registration()
    {
        var handler = new ScriptedHandler(new object[]
        {
            JsonStatus(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"no_user_info\"}}"),
            JsonStatus(HttpStatusCode.OK, "{\"agent_runtime_id\":\"runtime-123\"}")
        });
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(client, "router-test");
        var session = new ChatGptSessionImport(
            "test-web-access-token",
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1));

        var identity = await registrar.RegisterAsync(session, proxyUrl: null);

        Assert.Equal("runtime-123", identity.AgentRuntimeId);
        Assert.Equal("account-123", identity.AccountId);
        Assert.Equal("user-123", identity.ChatGptUserId);
        Assert.Equal("plus", identity.PlanType);
        Assert.Null(identity.TaskId);
        var posts = handler.Requests.Where(static request => request.Method == "POST").ToArray();
        Assert.Equal(2, posts.Length);
        Assert.Equal("codex-router-route-preflight", posts[0].AuthorizationParameter);
        Assert.Equal("test-web-access-token", posts[1].AuthorizationParameter);
        Assert.Equal("Bearer", handler.LastPostAuthorization?.Scheme);
        Assert.Equal("test-web-access-token", handler.LastPostAuthorization?.Parameter);
        Assert.NotNull(handler.LastPostBody);
        using var requestJson = JsonDocument.Parse(handler.LastPostBody!);
        Assert.Equal("codex-cli", requestJson.RootElement.GetProperty("abom").GetProperty("agent_harness_id").GetString());
        Assert.StartsWith("ssh-ed25519 ", requestJson.RootElement.GetProperty("agent_public_key").GetString());
    }

    [Fact]
    public async Task Agent_identity_registration_preflight_region_403_blocks_before_real_token_is_sent()
    {
        const string realToken = "real-access-token-must-never-be-sent";
        var handler = new ScriptedHandler(new object[]
        {
            JsonStatus(
                HttpStatusCode.Forbidden,
                "{\"error\":{\"code\":\"unsupported_country_region_territory\"}}",
                "preflight-ray")
        });
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(client, "router-test");
        var session = new ChatGptSessionImport(
            realToken,
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registrar.RegisterAsync(session, proxyUrl: null));

        Assert.Contains("route preflight was rejected", error.Message, StringComparison.Ordinal);
        Assert.Contains("networkRoute=direct", error.Message, StringComparison.Ordinal);
        Assert.Contains("egressCountry=CN; cloudflareColo=SIN", error.Message, StringComparison.Ordinal);
        Assert.Contains("cfRay=preflight-ray", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(realToken, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests, static request => request.Method == "POST");
        Assert.Contains(handler.Requests, static request =>
            request.Method == "GET" && request.Uri is not null && request.Uri.Contains("/cdn-cgi/trace", StringComparison.Ordinal));
        foreach (var request in handler.Requests)
        {
            Assert.DoesNotContain(realToken, request.AuthorizationParameter ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(realToken, request.Body ?? string.Empty, StringComparison.Ordinal);
            if (request.AuthorizationParameter is not null)
            {
                Assert.Equal("codex-router-route-preflight", request.AuthorizationParameter);
            }
        }
    }

    [Fact]
    public async Task Agent_identity_registration_preflight_network_failure_does_not_block_registration()
    {
        var handler = new ScriptedHandler(new object[]
        {
            new HttpRequestException("preflight network failure"),
            JsonStatus(HttpStatusCode.OK, "{\"agent_runtime_id\":\"runtime-123\"}")
        });
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(client, "router-test");
        var session = new ChatGptSessionImport(
            "test-web-access-token",
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1));

        var identity = await registrar.RegisterAsync(session, proxyUrl: null);

        Assert.Equal("runtime-123", identity.AgentRuntimeId);
        var posts = handler.Requests.Where(static request => request.Method == "POST").ToArray();
        Assert.Equal(2, posts.Length);
        Assert.Equal("codex-router-route-preflight", posts[0].AuthorizationParameter);
        Assert.Equal("test-web-access-token", posts[1].AuthorizationParameter);
    }

    [Fact]
    public async Task Agent_identity_registration_proxy_preflight_network_failure_blocks_before_real_token_is_sent()
    {
        const string realToken = "real-access-token-must-never-be-sent";
        var handler = new ScriptedHandler(new object[]
        {
            new HttpRequestException("proxy connection refused")
        });
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(client, "router-test");
        var session = new ChatGptSessionImport(
            realToken,
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registrar.RegisterAsync(session, proxyUrl: "http://127.0.0.1:7897"));

        Assert.Contains("route preflight could not reach OpenAI", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("networkRoute=proxy(http://127.0.0.1:7897)", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(realToken, error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests, static request => request.Method == "POST");
        Assert.All(handler.Requests, static request =>
            Assert.Equal("codex-router-route-preflight", request.AuthorizationParameter));
    }

    [Fact]
    public async Task Agent_identity_registration_retries_transient_failures_with_same_official_request_shape()
    {
        var delays = new List<TimeSpan>();
        var handler = new ScriptedHandler(new object[]
        {
            JsonStatus(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"no_user_info\"}}"),
            JsonStatus(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"temporarily_unavailable\"}}"),
            JsonStatus(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limit_exceeded\"}}"),
            JsonStatus(HttpStatusCode.OK, "{\"agent_runtime_id\":\"runtime-after-retry\"}")
        });
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(
            client,
            "router-test",
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var session = new ChatGptSessionImport(
            "test-web-access-token",
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1));

        var identity = await registrar.RegisterAsync(session, proxyUrl: null);

        Assert.Equal("runtime-after-retry", identity.AgentRuntimeId);
        Assert.Equal(2, delays.Count);
        var realRequests = handler.Requests
            .Where(static request => request.AuthorizationParameter == "test-web-access-token")
            .ToArray();
        Assert.Equal(3, realRequests.Length);
        Assert.All(realRequests, static request => Assert.Equal("codex_cli_rs", request.Originator));
        Assert.All(realRequests, static request => Assert.StartsWith("codex_cli_rs/router-test ", request.UserAgent, StringComparison.Ordinal));
        Assert.Single(realRequests.Select(static request => request.Body).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Agent_identity_registration_real_403_includes_route_preflight_passed_and_egress_trace()
    {
        const string realToken = "secret-access-token-that-must-not-leak";
        var handler = new ScriptedHandler(new object[]
        {
            JsonStatus(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"no_user_info\"}}"),
            JsonStatus(
                HttpStatusCode.Forbidden,
                "{\"error\":{\"code\":\"request_forbidden\",\"type\":\"request_forbidden\",\"message\":\"blocked for diagnostic test\",\"sensitive-extra\":\"must-not-appear\"}}",
                "test-ray")
        });
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(client, "router-test");
        var session = new ChatGptSessionImport(
            realToken,
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1),
            "https://auth.openai.com",
            new[] { "aud-one" },
            new[] { "openid", "profile" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registrar.RegisterAsync(session, proxyUrl: null));

        Assert.Contains("HTTP 403", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("networkRoute=direct", error.Message, StringComparison.Ordinal);
        Assert.Contains("code=request_forbidden", error.Message, StringComparison.Ordinal);
        Assert.Contains("type=request_forbidden", error.Message, StringComparison.Ordinal);
        Assert.Contains("message=blocked for diagnostic test", error.Message, StringComparison.Ordinal);
        Assert.Contains("cfRay=test-ray", error.Message, StringComparison.Ordinal);
        Assert.Contains("tokenIssuer=https://auth.openai.com", error.Message, StringComparison.Ordinal);
        Assert.Contains("tokenAudience=aud-one", error.Message, StringComparison.Ordinal);
        Assert.Contains("tokenScopes=openid,profile", error.Message, StringComparison.Ordinal);
        Assert.Contains("routePreflight=passed", error.Message, StringComparison.Ordinal);
        Assert.Contains("egressCountry=CN; cloudflareColo=SIN", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(realToken, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-extra", error.Message, StringComparison.Ordinal);
        var posts = handler.Requests.Where(static request => request.Method == "POST").ToArray();
        Assert.Equal(2, posts.Length);
        Assert.Equal("codex-router-route-preflight", posts[0].AuthorizationParameter);
        Assert.Equal(realToken, posts[1].AuthorizationParameter);
    }

    [Fact]
    public async Task Agent_identity_registration_failure_exposes_only_safe_structured_error_metadata()
    {
        var handler = new FailingHandler();
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(client, "router-test");
        var session = new ChatGptSessionImport(
            "secret-access-token-that-must-not-leak",
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1),
            "https://auth.openai.com",
            new[] { "aud-one" },
            new[] { "openid", "profile" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registrar.RegisterAsync(session, proxyUrl: null));

        Assert.Contains("HTTP 403", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("networkRoute=direct", error.Message, StringComparison.Ordinal);
        Assert.Contains("code=request_forbidden", error.Message, StringComparison.Ordinal);
        Assert.Contains("type=request_forbidden", error.Message, StringComparison.Ordinal);
        Assert.Contains("message=blocked for diagnostic test", error.Message, StringComparison.Ordinal);
        Assert.Contains("cfRay=test-ray", error.Message, StringComparison.Ordinal);
        Assert.Contains("tokenIssuer=https://auth.openai.com", error.Message, StringComparison.Ordinal);
        Assert.Contains("tokenAudience=aud-one", error.Message, StringComparison.Ordinal);
        Assert.Contains("tokenScopes=openid,profile", error.Message, StringComparison.Ordinal);
        Assert.Contains("routePreflight=passed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-access-token", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-extra", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_identity_registration_failure_reports_selected_proxy_route()
    {
        var handler = new FailingHandler();
        using var client = new HttpClient(handler);
        var registrar = new AgentIdentityRegistrar(client, "router-test");
        var session = new ChatGptSessionImport(
            "secret-access-token-that-must-not-leak",
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false,
            DateTimeOffset.UtcNow.AddHours(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registrar.RegisterAsync(session, proxyUrl: "http://127.0.0.1:7897"));

        Assert.Contains("networkRoute=proxy(http://127.0.0.1:7897)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_keyring_payload_contains_agent_identity_but_no_chatgpt_tokens()
    {
        var identity = new CodexAgentIdentityRecord(
            "runtime-123",
            "pkcs8-base64",
            "account-123",
            "user-123",
            "person@example.test",
            "plus",
            false);

        var json = CodexDirectKeyringStore.SerializeAgentIdentity(identity);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("agentIdentity", root.GetProperty("auth_mode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("OPENAI_API_KEY").ValueKind);
        Assert.False(root.TryGetProperty("tokens", out _));
        Assert.False(root.TryGetProperty("last_refresh", out _));
        var stored = root.GetProperty("agent_identity");
        Assert.Equal("runtime-123", stored.GetProperty("agent_runtime_id").GetString());
        Assert.Equal("pkcs8-base64", stored.GetProperty("agent_private_key").GetString());
        Assert.False(stored.TryGetProperty("task_id", out _));
    }

    [Fact]
    public async Task Synthetic_agent_identity_is_readable_by_real_codex_when_opted_in()
    {
        if (!OperatingSystem.IsWindows()) return;
        var codex = Environment.GetEnvironmentVariable("CODEX_ROUTER_LIVE_CODEX");
        if (string.IsNullOrWhiteSpace(codex) || !File.Exists(codex)) return;

        var home = Path.Combine(Path.GetTempPath(), $"codex-router-live-keyring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var keyMaterial = AgentIdentityRegistrar.GenerateKeyMaterial();
        var writer = new CodexDirectKeyringStore();
        var identity = new CodexAgentIdentityRecord(
            "synthetic-runtime",
            keyMaterial.PrivateKeyPkcs8Base64,
            "synthetic-account",
            "synthetic-user",
            "synthetic@example.test",
            "plus",
            false,
            "synthetic-task");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(home, "config.toml"),
                "cli_auth_credentials_store = \"keyring\"\n[features]\nsecret_auth_storage = false\n");
            await writer.SaveAgentIdentityAsync(home, identity);

            var startInfo = new ProcessStartInfo
            {
                FileName = codex,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = home
            };
            startInfo.ArgumentList.Add("login");
            startInfo.ArgumentList.Add("status");
            startInfo.Environment["CODEX_HOME"] = home;
            startInfo.Environment.Remove("CODEX_CLI_PATH");
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start live Codex probe.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var text = (await stdout) + "\n" + (await stderr);

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Logged in using access token", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { await writer.DeleteAsync(home); } catch { }
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Synthetic_agent_identity_is_reported_as_chatgpt_by_real_app_server_when_opted_in()
    {
        var required = string.Equals(Environment.GetEnvironmentVariable("CODEX_ROUTER_LIVE_REQUIRED"), "1", StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            if (required) Assert.Fail("Live AgentIdentity probe requires Windows.");
            return;
        }
        var codex = Environment.GetEnvironmentVariable("CODEX_ROUTER_LIVE_CODEX");
        if (string.IsNullOrWhiteSpace(codex) || !File.Exists(codex))
        {
            if (required) Assert.Fail($"Live Codex path is missing or invalid: '{codex ?? "<null>"}'.");
            return;
        }

        var home = Path.Combine(Path.GetTempPath(), $"codex-router-live-agent-account-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var writer = new CodexDirectKeyringStore();
        var keyMaterial = AgentIdentityRegistrar.GenerateKeyMaterial();
        var accountId = new AccountId("synthetic-agent-account");
        var identity = new CodexAgentIdentityRecord(
            "synthetic-runtime",
            keyMaterial.PrivateKeyPkcs8Base64,
            "synthetic-account",
            "synthetic-user",
            "synthetic@example.test",
            "plus",
            false,
            "synthetic-task");
        AppServerWorker? worker = null;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(home, "config.toml"),
                "cli_auth_credentials_store = \"keyring\"\n[features]\nsecret_auth_storage = false\n");
            await writer.SaveAgentIdentityAsync(home, identity);
            worker = new AppServerWorker(
                WorkerLaunchSpec.ForCodex(new WorkerId("synthetic-live-worker"), accountId, codex, home),
                new WorkerStartOptions(InitializeTimeout: TimeSpan.FromSeconds(20), StopTimeout: TimeSpan.FromSeconds(5)));
            await worker.StartAsync();

            var response = await worker.SendRequestAsync(
                "account/read",
                new { refreshToken = false },
                TimeSpan.FromSeconds(15));

            var account = response.GetProperty("account");
            Assert.Equal("chatgpt", account.GetProperty("type").GetString());
            Assert.Equal("synthetic@example.test", account.GetProperty("email").GetString());
            Assert.Equal("plus", account.GetProperty("planType").GetString());
        }
        finally
        {
            if (worker is not null)
            {
                try { await worker.DisposeAsync(); } catch { }
            }
            try { await writer.DeleteAsync(home); } catch { }
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Encrypted_secret_storage_accepts_payload_larger_than_windows_credential_limit_when_opted_in()
    {
        var required = string.Equals(Environment.GetEnvironmentVariable("CODEX_ROUTER_LIVE_REQUIRED"), "1", StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            if (required) Assert.Fail("Live encrypted-secret probe requires Windows.");
            return;
        }

        var codex = Environment.GetEnvironmentVariable("CODEX_ROUTER_LIVE_CODEX");
        if (string.IsNullOrWhiteSpace(codex) || !File.Exists(codex))
        {
            if (required) Assert.Fail($"Live Codex path is missing or invalid: '{codex ?? "<null>"}'.");
            return;
        }

        var home = Path.Combine(Path.GetTempPath(), $"codex-router-live-secret-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var writer = new CodexDirectKeyringStore();
        try
        {
            // The synthetic value is deliberately larger than WinCred's 2560-byte blob limit.
            // It is not a real credential and is never written to test output.
            var syntheticApiKey = "sk-codex-router-probe-" + new string('x', 4096);
            var login = await RunCodexAsync(
                codex,
                home,
                syntheticApiKey,
                "login",
                "--with-api-key",
                "-c",
                "cli_auth_credentials_store=\"keyring\"",
                "-c",
                "features.secret_auth_storage=true");

            Assert.Equal(0, login.ExitCode);
            Assert.False(File.Exists(Path.Combine(home, "auth.json")));
            var encryptedStore = Path.Combine(home, "secrets", "codex_auth.age");
            var createdFiles = Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(home, path))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.True(File.Exists(encryptedStore), $"Encrypted store was not found. Created files: {string.Join(", ", createdFiles)}");
            Assert.True(new FileInfo(encryptedStore).Length > 0);

            var status = await RunCodexAsync(
                codex,
                home,
                standardInput: null,
                "login",
                "status",
                "-c",
                "cli_auth_credentials_store=\"keyring\"",
                "-c",
                "features.secret_auth_storage=true");
            Assert.Equal(0, status.ExitCode);
            Assert.Contains("Logged in", status.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                _ = await RunCodexAsync(
                    codex,
                    home,
                    standardInput: null,
                    "logout",
                    "-c",
                    "cli_auth_credentials_store=\"keyring\"",
                    "-c",
                    "features.secret_auth_storage=true");
            }
            catch { }
            await writer.DeleteAsync(home);
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Direct_keyring_target_matches_keyring_rs_windows_convention()
    {
        if (!OperatingSystem.IsWindows()) return;
        var home = Path.Combine(Path.GetTempPath(), $"codex-router-keyring-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            var accountName = CodexDirectKeyringStore.ComputeAccountName(home);
            Assert.StartsWith("cli|", accountName);
            Assert.Equal(20, accountName.Length);
            Assert.Equal($"{accountName}.Codex Auth", CodexDirectKeyringStore.TargetName(accountName));

            var secretsAccountName = CodexDirectKeyringStore.ComputeSecretsAccountName(home);
            Assert.StartsWith("secrets|", secretsAccountName);
            Assert.Equal(24, secretsAccountName.Length);
            Assert.Equal($"{secretsAccountName}.codex", CodexDirectKeyringStore.SecretsTargetName(secretsAccountName));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCodexAsync(
        string codex,
        string codexHome,
        string? standardInput,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = codex,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = codexHome
        };
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment.Remove("CODEX_CLI_PATH");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start live Codex probe.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteLineAsync(standardInput);
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, (await stdout) + "\n" + (await stderr));
    }

    private static string Jwt(object payload)
    {
        static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        var body = Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.signature";
    }

    private static HttpResponseMessage JsonStatus(HttpStatusCode status, string json, string? cfRay = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (cfRay is not null)
        {
            response.Headers.TryAddWithoutValidation("cf-ray", cfRay);
        }
        return response;
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"request_forbidden\",\"type\":\"request_forbidden\",\"message\":\"blocked for diagnostic test\",\"sensitive-extra\":\"must-not-appear\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.TryAddWithoutValidation("cf-ray", "test-ray");
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }
        public List<AuthenticationHeaderValue?> PostAuthorizations { get; } = new();
        public List<string?> PostOriginators { get; } = new();
        public List<string?> PostUserAgents { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Authorization = request.Headers.Authorization;
                Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                PostAuthorizations.Add(Authorization);
                PostOriginators.Add(request.Headers.TryGetValues("originator", out var originators)
                    ? originators.SingleOrDefault()
                    : null);
                PostUserAgents.Add(request.Headers.UserAgent.ToString());
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"agent_runtime_id\":\"runtime-123\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<object> _postResults;
        private readonly string _traceBody;

        public ScriptedHandler(IEnumerable<object> postResults, string traceBody = "loc=CN\ncolo=SIN\n")
        {
            _postResults = new Queue<object>(postResults);
            _traceBody = traceBody;
        }

        public List<CapturedRequest> Requests { get; } = new();
        public AuthenticationHeaderValue? LastPostAuthorization { get; private set; }
        public string? LastPostBody { get; private set; }

        public sealed record CapturedRequest(
            string Method,
            string? AuthorizationParameter,
            string? Originator,
            string? UserAgent,
            string? Body,
            string? Uri);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("originator", out var originators)
                    ? originators.SingleOrDefault()
                    : null,
                request.Headers.UserAgent.ToString(),
                body,
                request.RequestUri?.ToString()));

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_traceBody, Encoding.UTF8, "text/plain")
                };
            }

            if (_postResults.Count == 0)
            {
                throw new InvalidOperationException("ScriptedHandler has no remaining POST responses.");
            }

            var next = _postResults.Dequeue();
            if (next is Exception ex)
            {
                throw ex;
            }

            LastPostAuthorization = request.Headers.Authorization;
            LastPostBody = body;
            return next as HttpResponseMessage
                ?? throw new InvalidOperationException("Unsupported scripted POST result.");
        }
    }
}
