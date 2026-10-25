// User settings, stored as JSON in %APPDATA%\DualSenseWired\settings.json.

using System.Text.Json;
using System.Text.Json.Serialization;
using DualSenseWired.Bridge;
using Button = DualSenseWired.Bridge.Button;

namespace DualSenseWired.App;

public sealed class Settings
{
    public bool Emulation { get; set; } = true;
    public bool Speaker { get; set; } = true;
    public int Volume { get; set; } = 80;              // %, the controller's own speaker volume
    public bool Haptics { get; set; } = true;
    public int HapticsGain { get; set; } = 100;        // %
    public int Latency { get; set; } = ControllerStream.DefaultTargetLevel;   // ms buffered in the controller
    public bool LowBatteryNotifications { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public bool AdaptiveTriggers { get; set; } = true;
    public int DeadzoneLeft { get; set; }               // % of the stick's travel
    public int DeadzoneRight { get; set; }
    public bool InvertRightY { get; set; }
    /// <summary>"light", "dark" or null (follow Windows).</summary>
    public string? Theme { get; set; }
    /// <summary>Physical button -> button games see; "None" disables it. Missing = unchanged.</summary>
    public Dictionary<Button, string> Remap { get; set; } = new();

    public const string Disabled = "None";

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DualSenseWired");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public ButtonMap? BuildButtonMap()
    {
        var map = new Dictionary<Button, Button?>();
        foreach (var (from, to) in Remap)
            map[from] = to == Disabled ? null : Enum.TryParse<Button>(to, out var b) ? b : from;
        var result = new ButtonMap(map, DeadzoneLeft, DeadzoneRight, InvertRightY);
        return result.IsIdentity ? null : result;
    }
}
