// Runs the emulation: Bluetooth link + virtual wired DualSense + USB/IP server, attached through
// usbip-win2. Start/Stop are what the "emulation" switch does.

using System.Diagnostics;
using System.Text.RegularExpressions;
using DualSenseWired.Bridge;
using DualSenseWired.Device;
using DualSenseWired.UsbIp;

namespace DualSenseWired.App;

public sealed class BridgeHost : IDisposable
{
    public const string UsbIpExe = @"C:\Program Files\USBip\usbip.exe";

    readonly Action<string> _log;
    readonly object _lock = new();
    CancellationTokenSource? _cts;
    Task? _serverTask;
    VirtualDualSense? _device;
    int? _port;

    public BridgeHost(Action<string> log) => _log = log;

    public BluetoothLink? Link { get; private set; }
    public bool Running => Link != null;
    public static bool UsbIpInstalled => File.Exists(UsbIpExe);

    public void Start(Settings settings)
    {
        lock (_lock)
        {
            if (Link != null)
                return;
            Link = new BluetoothLink(_log);
            Apply(settings);
            _device = new VirtualDualSense(Link, _log);
            var server = new UsbIpServer(_device, _log);
            _cts = new CancellationTokenSource();
            // On the thread pool: started from the UI thread, every URB would otherwise run on it.
            var token = _cts.Token;
            _serverTask = Task.Run(() => server.RunAsync(token));
            _ = Task.Run(AttachAsync);
        }
    }

    /// <summary>Take the settings that can change while running.</summary>
    public void Apply(Settings settings)
    {
        var link = Link;
        if (link == null)
            return;
        var stream = link.Stream;
        stream.SpeakerEnabled = settings.Speaker;
        stream.Volume = Math.Clamp(settings.Volume, 0, 100) * 0x7F / 100;
        stream.HapticsEnabled = settings.Haptics;
        stream.HapticsGain = settings.HapticsGain / 100.0;
        stream.TargetLevel = Math.Clamp(settings.Latency, 16, 56);
        link.ButtonMap = settings.BuildButtonMap();
        link.AdaptiveTriggers = settings.AdaptiveTriggers;
    }

    async Task AttachAsync()
    {
        await Task.Delay(300);
        var (code, output) = await RunUsbIpAsync($"attach -r 127.0.0.1 -b {_device!.BusId}");
        var match = Regex.Match(output, @"port (\d+)");
        if (code == 0 && match.Success)
            _port = int.Parse(match.Groups[1].Value);
        _log($"usbip attach: exit {code} {output}");
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (Link == null)
                return;
            // Unplug first, so Windows and Steam see the controller go away cleanly.
            if (_port != null)
            {
                // Off the UI thread's context: blocking on it here would deadlock (Stop runs on the UI thread).
                var (code, output) = Task.Run(() => RunUsbIpAsync($"detach -p {_port}")).GetAwaiter().GetResult();
                _log($"usbip detach: exit {code} {output}");
                _port = null;
            }
            _cts?.Cancel();
            try
            {
                _serverTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
            Link.Dispose();
            Link = null;
            _device = null;
            _log("emulation stopped");
        }
    }

    static async Task<(int Code, string Output)> RunUsbIpAsync(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(UsbIpExe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            string output = (await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, e.Message);
        }
    }

    public void Dispose() => Stop();
}
