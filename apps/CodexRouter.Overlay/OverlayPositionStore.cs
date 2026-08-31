using System.IO;
using System.Text.Json;
using CodexRouter.Host;

namespace CodexRouter.Overlay;

public sealed record OverlayPositionPreference(double OffsetXDip, double OffsetYDip, int Version = 2)
{
    public bool IsValid => Version == 2 && double.IsFinite(OffsetXDip) && double.IsFinite(OffsetYDip);
}

public sealed class OverlayPositionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public OverlayPositionStore(string? path = null)
    {
        _path = path ?? Path.Combine(RouterPaths.Default.Root, "overlay-position.json");
    }

    public OverlayPositionPreference? TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var value = JsonSerializer.Deserialize<OverlayPositionPreference>(File.ReadAllText(_path), JsonOptions);
            return value is { IsValid: true } ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(OverlayPositionPreference value)
    {
        if (!value.IsValid) throw new ArgumentOutOfRangeException(nameof(value));

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }
}
