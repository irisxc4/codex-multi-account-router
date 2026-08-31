using System.Text.Json;

namespace CodexRouter.Workers;

public sealed class WorkerClientContext
{
    private readonly object _gate = new();
    private bool? _experimentalApi;

    public void UpdateFromFrontInitialize(JsonElement initializeParams)
    {
        if (initializeParams.ValueKind != JsonValueKind.Object ||
            !initializeParams.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("experimentalApi", out var experimental))
        {
            return;
        }

        // Desktop often advertises experimentalApi:false to the shim, then still
        // sends thread/resume.path. Official app-server rejects that unless the
        // worker handshake advertised experimentalApi:true. Never copy a front-side
        // false onto workers.
        if (experimental.ValueKind != JsonValueKind.True)
        {
            return;
        }

        lock (_gate)
        {
            _experimentalApi = true;
        }
    }

    public WorkerStartOptions Apply(WorkerStartOptions baseOptions)
    {
        lock (_gate)
        {
            return baseOptions with { ExperimentalApi = _experimentalApi ?? true };
        }
    }
}
