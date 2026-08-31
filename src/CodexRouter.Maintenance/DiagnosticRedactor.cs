using CodexRouter.Domain;

namespace CodexRouter.Maintenance;

public sealed class DiagnosticRedactor
{
    public string Redact(string input) => DiagnosticRedaction.Redact(input);
}
