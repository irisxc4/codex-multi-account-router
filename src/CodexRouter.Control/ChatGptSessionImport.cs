using System.Text;
using System.Text.Json;

namespace CodexRouter.Control;

public sealed record ChatGptSessionImport(
    string AccessToken,
    string AccountId,
    string ChatGptUserId,
    string? Email,
    string PlanType,
    bool IsFedRamp,
    DateTimeOffset? ExpiresAt,
    string? Issuer = null,
    IReadOnlyList<string>? Audience = null,
    IReadOnlyList<string>? Scopes = null)
{
    public IReadOnlyList<string> SafeAudience => Audience ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeScopes => Scopes ?? Array.Empty<string>();
}

public static class ChatGptSessionImportParser
{
    public static ChatGptSessionImport Parse(string sessionJson, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(sessionJson))
        {
            throw new ArgumentException("ChatGPT session JSON cannot be empty.", nameof(sessionJson));
        }

        using var document = JsonDocument.Parse(sessionJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("ChatGPT session must be a JSON object.");
        }

        var root = document.RootElement;
        var accessToken = RequiredString(root, "accessToken");
        using var claimsDocument = ParseJwtPayload(accessToken);
        var claims = claimsDocument.RootElement;
        var auth = claims.TryGetProperty("https://api.openai.com/auth", out var authValue) &&
                   authValue.ValueKind == JsonValueKind.Object
            ? authValue
            : default;
        var profile = claims.TryGetProperty("https://api.openai.com/profile", out var profileValue) &&
                      profileValue.ValueKind == JsonValueKind.Object
            ? profileValue
            : default;

        var sessionAccountId = NestedString(root, "account", "id");
        var tokenAccountId = FirstNonEmpty(
            ObjectString(auth, "chatgpt_account_id"),
            ObjectString(claims, "chatgpt_account_id"));
        if (sessionAccountId is not null
            && tokenAccountId is not null
            && !string.Equals(sessionAccountId, tokenAccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected ChatGPT account does not match the account bound to the web session access token. Switch to the intended account, refresh the session page, and copy it again.");
        }
        var accountId = tokenAccountId ?? sessionAccountId;
        if (accountId is null)
        {
            throw new InvalidOperationException("ChatGPT session does not contain a usable account id.");
        }

        var chatGptUserId = FirstNonEmpty(
            ObjectString(auth, "chatgpt_user_id"),
            ObjectString(auth, "user_id"),
            ObjectString(claims, "chatgpt_user_id"),
            ObjectString(claims, "user_id"),
            ObjectString(claims, "sub"));
        if (chatGptUserId is null)
        {
            throw new InvalidOperationException("ChatGPT session access token does not contain a usable user id.");
        }

        var email = FirstNonEmpty(
            ObjectString(claims, "email"),
            ObjectString(profile, "email"),
            NestedString(root, "user", "email"));
        var planType = FirstNonEmpty(
            ObjectString(auth, "chatgpt_plan_type"),
            NestedString(root, "account", "planType"),
            NestedString(root, "account", "plan_type")) ?? "unknown";
        var isFedRamp = ObjectBoolean(auth, "chatgpt_account_is_fedramp") ?? false;
        var expiresAt = JwtExpiry(claims);
        if (expiresAt is { } expiry && expiry <= (now ?? DateTimeOffset.UtcNow).AddSeconds(30))
        {
            throw new InvalidOperationException("ChatGPT web session access token is expired or about to expire. Refresh the session page and copy it again.");
        }

        var issuer = ObjectString(claims, "iss");
        var audience = ClaimStringList(claims, "aud");
        var scopes = ClaimStringList(claims, "scope", splitWhitespace: true)
            .Concat(ClaimStringList(claims, "scp", splitWhitespace: true))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new ChatGptSessionImport(
            accessToken,
            accountId,
            chatGptUserId,
            email,
            planType.Trim().ToLowerInvariant(),
            isFedRamp,
            expiresAt,
            issuer,
            audience,
            scopes);
    }

    private static JsonDocument ParseJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("ChatGPT session accessToken is not a JWT.");
        }
        try
        {
            var payload = Base64UrlDecode(parts[1]);
            return JsonDocument.Parse(payload);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new InvalidOperationException("ChatGPT session accessToken payload is invalid.", ex);
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static DateTimeOffset? JwtExpiry(JsonElement claims)
    {
        if (!claims.TryGetProperty("exp", out var value)) return null;
        long seconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return null;
    }

    private static string RequiredString(JsonElement value, string name) =>
        ObjectString(value, name) ?? throw new InvalidOperationException($"ChatGPT session is missing '{name}'.");

    private static string? NestedString(JsonElement root, string objectName, string name)
    {
        if (!root.TryGetProperty(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object) return null;
        return ObjectString(nested, name);
    }

    private static string? ObjectString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var text = property.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool? ObjectBoolean(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static IReadOnlyList<string> ClaimStringList(JsonElement claims, string name, bool splitWhitespace = false)
    {
        if (claims.ValueKind != JsonValueKind.Object || !claims.TryGetProperty(name, out var property))
        {
            return Array.Empty<string>();
        }

        IEnumerable<string> values = property.ValueKind switch
        {
            JsonValueKind.String => new[] { property.GetString() ?? string.Empty },
            JsonValueKind.Array => property.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty),
            _ => Array.Empty<string>()
        };

        if (splitWhitespace)
        {
            values = values.SelectMany(static value => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        return values
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
