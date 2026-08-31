using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;

namespace CodexRouter.Control;

public sealed record CodexAgentIdentityRecord(
    string AgentRuntimeId,
    string AgentPrivateKey,
    string AccountId,
    string ChatGptUserId,
    string? Email,
    string PlanType,
    bool ChatGptAccountIsFedRamp,
    string? TaskId = null);

public interface IAgentIdentityRegistrar
{
    Task<CodexAgentIdentityRecord> RegisterAsync(
        ChatGptSessionImport session,
        string? proxyUrl,
        CancellationToken cancellationToken = default);
}

public sealed class AgentIdentityRegistrar : IAgentIdentityRegistrar
{
    private const string RegistrationUrl = "https://auth.openai.com/api/accounts/v1/agent/register";
    private const string CloudflareTraceUrl = "https://auth.openai.com/cdn-cgi/trace";
    private const string RoutePreflightToken = "codex-router-route-preflight";
    private const string UnsupportedCountryRegionCode = "unsupported_country_region_territory";
    private const string OfficialOriginator = "codex_cli_rs";
    private const int MaxRegistrationAttempts = 3;
    private static readonly byte[] KeyDerivationContext = Encoding.ASCII.GetBytes("codex-agent-identity-ed25519-v1");
    private readonly HttpClient? _testClient;
    private readonly string _agentVersion;
    private readonly string _userAgent;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public AgentIdentityRegistrar(
        HttpClient? testClient = null,
        string agentVersion = "0.1.0-dev",
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _testClient = testClient;
        _agentVersion = NormalizeAgentVersion(agentVersion);
        _userAgent = BuildOfficialUserAgent(_agentVersion);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<CodexAgentIdentityRecord> RegisterAsync(
        ChatGptSessionImport session,
        string? proxyUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var normalizedProxy = CodexLoginProxy.Normalize(proxyUrl);
        var networkRoute = normalizedProxy is null ? "direct" : $"proxy({normalizedProxy})";
        var keyMaterial = GenerateKeyMaterial();
        using var ownedClient = _testClient is null ? CreateClient(normalizedProxy) : null;
        var client = _testClient ?? ownedClient!;
        var payload = new
        {
            abom = new
            {
                agent_version = _agentVersion,
                agent_harness_id = "codex-cli",
                running_location = "cli-windows"
            },
            agent_public_key = keyMaterial.PublicKeySsh,
            capabilities = new[] { "responsesapi" },
            ttl = (long?)null
        };

        await RejectIfRoutePreflightBlockedAsync(
                client,
                payload,
                session,
                networkRoute,
                failClosedOnTransportError: normalizedProxy is not null,
                cancellationToken)
            .ConfigureAwait(false);

        var registration = await SendRegistrationWithRetriesAsync(
                client,
                session.AccessToken,
                payload,
                session,
                networkRoute,
                cancellationToken)
            .ConfigureAwait(false);
        using var response = registration.Response;
        var responseBody = registration.Body;
        if (!response.IsSuccessStatusCode)
        {
            string? egressCountry = null;
            string? cloudflareColo = null;
            var isForbidden = response.StatusCode == HttpStatusCode.Forbidden;
            if (isForbidden)
            {
                (egressCountry, cloudflareColo) = await TryReadCloudflareTraceAsync(client, cancellationToken)
                    .ConfigureAwait(false);
            }
            throw new InvalidOperationException(BuildSafeRegistrationFailure(
                response,
                responseBody,
                session,
                networkRoute,
                egressCountry,
                cloudflareColo,
                routePreflightPassed: isForbidden,
                attempts: registration.Attempts));
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var runtimeId = root.TryGetProperty("agent_runtime_id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            throw new InvalidOperationException("OpenAI Agent Identity registration response did not contain an agent runtime id.");
        }

        return new CodexAgentIdentityRecord(
            runtimeId,
            keyMaterial.PrivateKeyPkcs8Base64,
            session.AccountId,
            session.ChatGptUserId,
            session.Email,
            NormalizePlanType(session.PlanType),
            session.IsFedRamp,
            null);
    }

    private HttpRequestMessage CreateRegistrationRequest(string accessToken, object payload, ChatGptSessionImport session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RegistrationUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("originator", OfficialOriginator);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        if (session.IsFedRamp)
        {
            request.Headers.TryAddWithoutValidation("X-OpenAI-Fedramp", "true");
        }
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private async Task RejectIfRoutePreflightBlockedAsync(
        HttpClient client,
        object payload,
        ChatGptSessionImport session,
        string networkRoute,
        bool failClosedOnTransportError,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        byte[]? body = null;
        try
        {
            using var request = CreateRegistrationRequest(RoutePreflightToken, payload, session);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            body = await ReadLimitedResponseBodyAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception ex) when (IsRetryableTransportFailure(ex))
        {
            response?.Dispose();
            if (failClosedOnTransportError)
            {
                throw new InvalidOperationException(BuildRoutePreflightTransportFailure(networkRoute, ex), ex);
            }
            return;
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.Forbidden
                || !string.Equals(TryGetErrorCode(body), UnsupportedCountryRegionCode, StringComparison.Ordinal))
            {
                return;
            }

            var (loc, colo) = await TryReadCloudflareTraceAsync(client, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(BuildRoutePreflightRejection(response, networkRoute, loc, colo));
        }
    }

    private async Task<RegistrationHttpResult> SendRegistrationWithRetriesAsync(
        HttpClient client,
        string accessToken,
        object payload,
        ChatGptSessionImport session,
        string networkRoute,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRegistrationAttempts; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = CreateRegistrationRequest(accessToken, payload, session);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                var body = await ReadLimitedResponseBodyAsync(response, timeout.Token).ConfigureAwait(false);
                if (IsRetryableStatus(response.StatusCode) && attempt < MaxRegistrationAttempts)
                {
                    var delay = RetryDelay(response, attempt);
                    response.Dispose();
                    response = null;
                    await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return new RegistrationHttpResult(response, body, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch (Exception ex) when (IsRetryableTransportFailure(ex))
            {
                response?.Dispose();
                if (attempt >= MaxRegistrationAttempts)
                {
                    throw new InvalidOperationException(BuildRegistrationTransportFailure(networkRoute, attempt, ex), ex);
                }
                await _delayAsync(DefaultRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("OpenAI Agent Identity registration retry loop ended unexpectedly.");
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static bool IsRetryableTransportFailure(Exception error) =>
        error is HttpRequestException or IOException or OperationCanceledException;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var requested = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (requested is { } value && value > TimeSpan.Zero)
        {
            return value <= TimeSpan.FromSeconds(2) ? value : TimeSpan.FromSeconds(2);
        }
        return DefaultRetryDelay(attempt);
    }

    private static TimeSpan DefaultRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(250 * Math.Clamp(attempt, 1, 4));

    private static async Task<(string? Loc, string? Colo)> TryReadCloudflareTraceAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CloudflareTraceUrl);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var body = await ReadLimitedResponseBodyAsync(response, timeout.Token).ConfigureAwait(false);
            return ParseCloudflareTrace(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, null);
        }
    }

    private static (string? Loc, string? Colo) ParseCloudflareTrace(ReadOnlySpan<byte> body)
    {
        string? loc = null;
        string? colo = null;
        foreach (var rawLine in Encoding.UTF8.GetString(body).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (loc is null && line.StartsWith("loc=", StringComparison.Ordinal))
            {
                loc = line[4..].Trim();
                if (string.IsNullOrWhiteSpace(loc)) loc = null;
            }
            else if (colo is null && line.StartsWith("colo=", StringComparison.Ordinal))
            {
                colo = line[5..].Trim();
                if (string.IsNullOrWhiteSpace(colo)) colo = null;
            }
        }
        return (loc, colo);
    }

    private static string? TryGetErrorCode(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                return code.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string BuildRoutePreflightRejection(
        HttpResponseMessage response,
        string networkRoute,
        string? egressCountry,
        string? cloudflareColo)
    {
        var parts = new List<string>
        {
            "OpenAI Agent Identity registration route preflight was rejected by region policy before authentication",
            $"networkRoute={networkRoute}"
        };
        if (!string.IsNullOrWhiteSpace(egressCountry)) parts.Add($"egressCountry={SafeDiagnosticValue(egressCountry)}");
        if (!string.IsNullOrWhiteSpace(cloudflareColo)) parts.Add($"cloudflareColo={SafeDiagnosticValue(cloudflareColo)}");
        if (response.Headers.TryGetValues("cf-ray", out var cfRayValues))
        {
            var cfRay = cfRayValues.FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(cfRay) && cfRay.Length <= 96) parts.Add($"cfRay={cfRay}");
        }
        parts.Add("the selected network route exits in a region OpenAI does not support. Switch the proxy node to a supported region (e.g. JP/SG/US) and retry.");
        return string.Join("; ", parts);
    }

    private static string BuildRoutePreflightTransportFailure(string networkRoute, Exception error) =>
        string.Join("; ", new[]
        {
            "OpenAI Agent Identity registration route preflight could not reach OpenAI through the selected explicit proxy",
            $"networkRoute={networkRoute}",
            $"errorType={SafeDiagnosticValue(error.GetType().Name)}",
            "start the proxy or select a working HTTP/HTTPS proxy before retrying. The real ChatGPT access token was not sent"
        });

    private static string BuildRegistrationTransportFailure(string networkRoute, int attempts, Exception error) =>
        string.Join("; ", new[]
        {
            "OpenAI Agent Identity registration could not reach OpenAI after bounded retries",
            $"networkRoute={networkRoute}",
            $"attempts={attempts}",
            $"errorType={SafeDiagnosticValue(error.GetType().Name)}",
            "check the selected proxy route and retry"
        });

    internal static AgentKeyMaterial GenerateKeyMaterial()
    {
        Span<byte> seedMaterial = stackalloc byte[64];
        RandomNumberGenerator.Fill(seedMaterial);
        var combined = new byte[KeyDerivationContext.Length + seedMaterial.Length];
        KeyDerivationContext.CopyTo(combined, 0);
        seedMaterial.CopyTo(combined.AsSpan(KeyDerivationContext.Length));
        var digest = SHA512.HashData(combined);
        CryptographicOperations.ZeroMemory(combined);
        CryptographicOperations.ZeroMemory(seedMaterial);

        var seed = digest.AsSpan(0, 32).ToArray();
        try
        {
            var privateKey = new Ed25519PrivateKeyParameters(seed);
            var publicKey = privateKey.GeneratePublicKey().GetEncoded();
            var privateKeyInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(privateKey).GetEncoded();
            try
            {
                return new AgentKeyMaterial(
                    Convert.ToBase64String(privateKeyInfo),
                    EncodeSshEd25519PublicKey(publicKey));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKeyInfo);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    internal static string EncodeSshEd25519PublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 32) throw new ArgumentException("Ed25519 public key must be 32 bytes.", nameof(publicKey));
        var algorithm = Encoding.ASCII.GetBytes("ssh-ed25519");
        var blob = new byte[4 + algorithm.Length + 4 + publicKey.Length];
        var offset = 0;
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(offset, 4), (uint)algorithm.Length);
        offset += 4;
        algorithm.CopyTo(blob, offset);
        offset += algorithm.Length;
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(offset, 4), (uint)publicKey.Length);
        offset += 4;
        publicKey.CopyTo(blob.AsSpan(offset));
        return $"ssh-ed25519 {Convert.ToBase64String(blob)}";
    }

    private static async Task<byte[]> ReadLimitedResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int maxBytes = 64 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= maxBytes)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (buffer.Length > maxBytes)
        {
            throw new InvalidOperationException("OpenAI Agent Identity registration response exceeded 64 KiB.");
        }
        return buffer.ToArray();
    }

    private static string BuildSafeRegistrationFailure(
        HttpResponseMessage response,
        ReadOnlySpan<byte> body,
        ChatGptSessionImport session,
        string networkRoute,
        string? egressCountry = null,
        string? cloudflareColo = null,
        bool routePreflightPassed = false,
        int attempts = 1)
    {
        var parts = new List<string>
        {
            $"OpenAI Agent Identity registration failed with HTTP {(int)response.StatusCode} ({response.StatusCode})",
            $"networkRoute={networkRoute}",
            $"attempts={attempts}"
        };

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
            AppendSafeJsonString(parts, error, "code");
            AppendSafeJsonString(parts, error, "type");
            AppendSafeJsonString(parts, error, "message");
            AppendSafeJsonString(parts, error, "param");
        }
        catch (JsonException)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType)) parts.Add($"contentType={mediaType}");
            parts.Add("responseBody=non-json");
        }

        if (response.Headers.TryGetValues("cf-ray", out var cfRayValues))
        {
            var cfRay = cfRayValues.FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(cfRay) && cfRay.Length <= 96) parts.Add($"cfRay={cfRay}");
        }

        if (!string.IsNullOrWhiteSpace(session.Issuer)) parts.Add($"tokenIssuer={SafeDiagnosticValue(session.Issuer)}");
        if (session.SafeAudience.Count > 0) parts.Add($"tokenAudience={string.Join(',', session.SafeAudience.Select(SafeDiagnosticValue))}");
        if (session.SafeScopes.Count > 0) parts.Add($"tokenScopes={string.Join(',', session.SafeScopes.Select(SafeDiagnosticValue))}");
        if (!string.IsNullOrWhiteSpace(egressCountry)) parts.Add($"egressCountry={SafeDiagnosticValue(egressCountry)}");
        if (!string.IsNullOrWhiteSpace(cloudflareColo)) parts.Add($"cloudflareColo={SafeDiagnosticValue(cloudflareColo)}");
        if (routePreflightPassed) parts.Add("routePreflight=passed");
        return string.Join("; ", parts);
    }

    private static void AppendSafeJsonString(ICollection<string> parts, JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return;
        string? text = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.GetRawText()
        };
        if (!string.IsNullOrWhiteSpace(text)) parts.Add($"{name}={SafeDiagnosticValue(text)}");
    }

    private static string SafeDiagnosticValue(string value)
    {
        var clean = new string(value
            .Where(static ch => !char.IsControl(ch) && ch is not ';' and not '\r' and not '\n')
            .Take(240)
            .ToArray());
        return clean.Length == 0 ? "(empty)" : clean;
    }

    private static string NormalizeAgentVersion(string value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "0.1.0-dev" : value.Trim();
        var normalized = new string(candidate
            .Where(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or '+')
            .Take(64)
            .ToArray());
        return normalized.Length == 0 ? "0.1.0-dev" : normalized;
    }

    private static string BuildOfficialUserAgent(string agentVersion)
    {
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var osVersion = new string(Environment.OSVersion.VersionString
            .Where(static ch => ch is >= ' ' and <= '~' && ch is not '(' and not ')')
            .Take(80)
            .ToArray())
            .Trim();
        if (osVersion.Length == 0) osVersion = "Windows";
        return $"{OfficialOriginator}/{agentVersion} ({osVersion}; {architecture}) CodexRouter";
    }

    private static HttpClient CreateClient(string? normalizedProxyUrl)
    {
        if (normalizedProxyUrl is null)
        {
            // Direct route. The registration endpoint enforces country/region policy at the
            // network edge per connection address, before authentication is evaluated. Hosts
            // whose IPv4 egress is tunneled but whose IPv6 egress leaks the native network
            // (a common Clash/TUN split) hit a spurious region 403 over IPv6, so prefer IPv4
            // and fall back to IPv6 only when no IPv4 connection can be established.
            var directHandler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = ConnectPreferringIPv4Async
            };
            return new HttpClient(directHandler, disposeHandler: true);
        }

        var proxyUri = new Uri(normalizedProxyUrl);
        if (proxyUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Agent Identity registration currently supports HTTP/HTTPS proxy URLs only.", nameof(normalizedProxyUrl));
        }
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(proxyUri),
            UseProxy = true
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static async ValueTask<Stream> ConnectPreferringIPv4Async(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var ordered = addresses
            .OrderBy(static address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();
        if (ordered.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        SocketException? lastFailure = null;
        foreach (var address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw lastFailure!;
    }

    private static string NormalizePlanType(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return value.Trim().ToLowerInvariant() switch
        {
            "education" => "edu",
            "hc" => "enterprise",
            var normalized => normalized
        };
    }

    private sealed record RegistrationHttpResult(HttpResponseMessage Response, byte[] Body, int Attempts);

    internal sealed record AgentKeyMaterial(string PrivateKeyPkcs8Base64, string PublicKeySsh);
}
