using System.Text.Json;

var mode = args.Length > 0 ? args[0] : "normal";
var overloadCount = 0;
var outputGate = new SemaphoreSlim(1, 1);

async Task WriteAsync(object value)
{
    var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await outputGate.WaitAsync();
    try
    {
        await Console.Out.WriteLineAsync(json);
        await Console.Out.FlushAsync();
    }
    finally
    {
        outputGate.Release();
    }
}

async Task WriteRawAsync(string value)
{
    await outputGate.WaitAsync();
    try
    {
        await Console.Out.WriteLineAsync(value);
        await Console.Out.FlushAsync();
    }
    finally
    {
        outputGate.Release();
    }
}

while (true)
{
    var line = await Console.In.ReadLineAsync();
    if (line is null)
    {
        return;
    }

    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;
    var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
        ? methodElement.GetString()
        : null;
    var hasId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    if (method == "initialize" && hasId)
    {
        if (mode == "init-exit")
        {
            Environment.Exit(13);
        }
        if (mode == "init-timeout")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        await WriteAsync(new { id = idElement.Clone(), result = new { userAgent = "fake-app-server/1.0" } });
        continue;
    }

    if (method == "initialized")
    {
        if (mode == "stderr-flood")
        {
            for (var i = 0; i < 1500; i++)
            {
                await Console.Error.WriteLineAsync($"stderr-{i:D4}");
            }
            await Console.Error.FlushAsync();
        }
        if (mode == "server-request")
        {
            await WriteAsync(new { id = "srv-1", method = "fake/approval", @params = new { question = "allow?" } });
        }
        if (mode == "malformed")
        {
            await WriteRawAsync("{broken-json");
        }
        continue;
    }

    if (hasId && method is null)
    {
        var idText = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.GetRawText();
        if (idText == "srv-1")
        {
            await WriteAsync(new { method = "fake/server-request-completed", @params = new { ok = true } });
        }
        continue;
    }

    if (!hasId || method is null)
    {
        continue;
    }

    if (method == "echo")
    {
        var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : default(JsonElement?);
        await WriteAsync(new { id = idElement.Clone(), result = parameters });
        continue;
    }

    if (method == "fake/environment")
    {
        await WriteAsync(new
        {
            id = idElement.Clone(),
            result = new
            {
                codexHome = Environment.GetEnvironmentVariable("CODEX_HOME"),
                codexCliPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH")
            }
        });
        continue;
    }

    if (method == "overload")
    {
        overloadCount++;
        if (overloadCount <= 2)
        {
            await WriteAsync(new
            {
                id = idElement.Clone(),
                error = new { code = -32001, message = "Server overloaded; retry later" }
            });
        }
        else
        {
            await WriteAsync(new { id = idElement.Clone(), result = new { attempts = overloadCount } });
        }
        continue;
    }

    if (method == "crash")
    {
        Environment.Exit(42);
    }

    if (method == "hang")
    {
        continue;
    }

    if (method == "emit-notification")
    {
        await WriteAsync(new { method = "fake/notification", @params = new { value = 7 } });
        await WriteAsync(new { id = idElement.Clone(), result = new { ok = true } });
        continue;
    }

    await WriteAsync(new
    {
        id = idElement.Clone(),
        error = new { code = -32601, message = $"Unknown method {method}" }
    });
}
