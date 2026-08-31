using System.Text.RegularExpressions;

namespace CodexRouter.Domain;

public static class DiagnosticRedaction
{
    private static readonly Regex Bearer = new(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{12,}", RegexOptions.Compiled);
    private static readonly Regex SensitiveJson = new(
        """(?i)("(?:access_token|refresh_token|id_token|token|password|cookie|authorization|api_key|secret)"\s*:\s*")[^"]*(")""",
        RegexOptions.Compiled);
    private static readonly Regex Email = new(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex LongSecret = new(@"\b[A-Za-z0-9_-]{48,}\b", RegexOptions.Compiled);

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var value = Bearer.Replace(input, "Bearer [REDACTED]");
        value = SensitiveJson.Replace(value, "$1[REDACTED]$2");
        value = Email.Replace(value, "[EMAIL]");
        value = LongSecret.Replace(value, "[LONG_SECRET_REDACTED]");
        return value;
    }
}
