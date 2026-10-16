// The main window: the HTML interface (App/ui/index.html, embedded) in WebView2. The page sends
// commands as web messages; the window answers with settings and, every second, the live state.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Button = DualSenseWired.Bridge.Button;

namespace DualSenseWired.App;

public sealed class MainForm : Form
{
    readonly Settings _settings;
    readonly BridgeHost _host;
    readonly Action<bool> _setEmulation;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    bool _ready;

    static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public MainForm(Settings settings, BridgeHost host, Action<bool> setEmulation)
    {
        _settings = settings;
        _host = host;
        _setEmulation = setEmulation;
        Text = Strings.Current.AppName;
        Icon = TrayApp.MakeIcon(true);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 800);
        MinimumSize = new Size(760, 560);
        BackColor = Color.FromArgb(32, 32, 32);
        Controls.Add(_web);
        _refresh.Tick += (_, _) => PostState();
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        // The app is installed under Program Files: keep WebView2's data in the user's profile.
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseWired", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, data);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DualSenseWired.ui.index.html")!;
        using var reader = new StreamReader(stream);
        core.NavigateToString(await reader.ReadToEndAsync());
    }

    void Post(object message)
    {
        if (_ready && !IsDisposed)
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    object SettingsMessage() => new { type = "settings", settings = _settings, autostart = Autostart.Enabled };

    void PostState() => Post(new { type = "state", state = State() });

    object State()
    {
        var link = _host.Link;
        bool connected = link?.Connected == true;
        var battery = connected ? link!.Battery : null;
        return new
        {
            emulation = _settings.Emulation,
            connected,
            model = link?.ProductId == 0x0DF2 ? "DualSense Edge" : "DualSense",
            battery = battery is { } b ? new { percent = b.Percent, charging = b.Charging, full = b.Full } : null,
            audio = connected && link!.Stream.Streaming,
            hidhideMissing = Hid.HidHide.CliPath == null,
        };
    }

    void OnMessage(string json)
    {
        JsonObject? m;
        try
        {
            m = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        switch ((string?)m?["cmd"])
        {
            case "ready":
                _ready = true;
                Post(new
                {
                    type = "init",
                    lang = Strings.Current.IsRussian ? "ru" : "en",
                    theme = _settings.Theme,
                    version = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"),
                    settings = _settings,
                    autostart = Autostart.Enabled,
                    state = State(),
                });
                _refresh.Start();
                break;
            case "set":
                SetSetting((string?)m!["key"], m["value"]);
                break;
            case "emulation":
                bool on = m!["value"]?.GetValue<bool>() == true;
                _settings.Emulation = on;
                _settings.Save();
                _setEmulation(on);
                break;
            case "autostart":
                Autostart.Set(m!["value"]?.GetValue<bool>() == true);
                Post(SettingsMessage());
                break;
            case "remap":
                if (Enum.TryParse<Button>((string?)m!["from"], out var from))
                {
                    string? to = (string?)m["to"];
                    if (to == null)
                        _settings.Remap.Remove(from);
                    else
                        _settings.Remap[from] = to;
                    Changed();
                }
                break;
            case "resetRemap":
                _settings.Remap.Clear();
                Changed();
                break;
            case "resetSettings":
                var defaults = new Settings();
                _settings.Speaker = defaults.Speaker;
                _settings.Volume = defaults.Volume;
                _settings.Haptics = defaults.Haptics;
                _settings.HapticsGain = defaults.HapticsGain;
                _settings.Latency = defaults.Latency;
                _settings.AdaptiveTriggers = defaults.AdaptiveTriggers;
                _settings.LowBatteryNotifications = defaults.LowBatteryNotifications;
                Changed();
                Post(SettingsMessage());
                break;
            case "openLog":
                System.Diagnostics.Process.Start("notepad.exe", $"\"{Logger.FilePath}\"");
                break;
        }
    }

    void SetSetting(string? key, JsonNode? value)
    {
        if (key == null || value == null)
            return;
        switch (key)
        {
            case nameof(Settings.Speaker): _settings.Speaker = value.GetValue<bool>(); break;
            case nameof(Settings.Haptics): _settings.Haptics = value.GetValue<bool>(); break;
            case nameof(Settings.AdaptiveTriggers): _settings.AdaptiveTriggers = value.GetValue<bool>(); break;
            case nameof(Settings.LowBatteryNotifications): _settings.LowBatteryNotifications = value.GetValue<bool>(); break;
            case nameof(Settings.InvertRightY): _settings.InvertRightY = value.GetValue<bool>(); break;
            case nameof(Settings.Volume): _settings.Volume = (int)value.GetValue<double>(); break;
            case nameof(Settings.HapticsGain): _settings.HapticsGain = (int)value.GetValue<double>(); break;
            case nameof(Settings.Latency): _settings.Latency = (int)value.GetValue<double>(); break;
            case nameof(Settings.DeadzoneLeft): _settings.DeadzoneLeft = (int)value.GetValue<double>(); break;
            case nameof(Settings.DeadzoneRight): _settings.DeadzoneRight = (int)value.GetValue<double>(); break;
            case nameof(Settings.Theme): _settings.Theme = value.GetValue<string>(); break;
            default: return;
        }
        Changed();
    }

    void Changed()
    {
        _settings.Save();
        _host.Apply(_settings);
    }

    public void ShowPage(string page) => Post(new { type = "page", page });

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Dispose();
        base.OnFormClosed(e);
    }
}
