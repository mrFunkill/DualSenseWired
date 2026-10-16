// Hiding the Bluetooth controller from every other program (Steam, games) with HidHide, so only the
// virtual wired one is seen. Uses HidHide's own command line client; restarting the device makes
// programs that already have it open (Steam) drop it.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DualSenseWired.Hid;

public static class HidHide
{
    static readonly string[] CliPaths =
    {
        @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe",
        @"C:\Program Files\Nefarius Software Solutions e.U\HidHide\x64\HidHideCLI.exe",
    };

    public static string? CliPath => CliPaths.FirstOrDefault(File.Exists);

    /// <summary>
    /// Allow this program to see hidden devices. Done at start: a controller left hidden by an
    /// earlier run (crash, kill, or the program moved) would otherwise be invisible to us too.
    /// </summary>
    public static void RegisterSelf(Action<string> log)
    {
        if (CliPath is { } cli)
            Run(cli, $"--app-reg \"{Environment.ProcessPath}\" --app-clean", log);
    }

    /// <summary>Hide the device from everyone but this program; true if it is hidden now.</summary>
    public static bool Hide(string instanceId, Action<string> log)
    {
        string? cli = CliPath;
        if (cli == null)
        {
            log("HidHide is not installed: Steam and games will also see the Bluetooth controller");
            return false;
        }
        string self = Environment.ProcessPath!;
        if (!Run(cli, $"--app-reg \"{self}\" --dev-hide \"{instanceId}\" --cloak-on", log))
            return false;
        // Steam opened the controller before it was hidden: make it let go.
        RestartDevice(instanceId, log);
        log($"Bluetooth controller hidden from other programs ({instanceId})");
        return true;
    }

    /// <summary>Give the device back to everyone (Steam picks it up again after the restart).</summary>
    public static void Unhide(string instanceId, Action<string> log)
    {
        string? cli = CliPath;
        if (cli == null)
            return;
        if (Run(cli, $"--dev-unhide \"{instanceId}\"", log))
        {
            RestartDevice(instanceId, log);
            log($"Bluetooth controller visible to other programs again ({instanceId})");
        }
    }

    static void RestartDevice(string instanceId, Action<string> log) =>
        Run("pnputil.exe", $"/restart-device \"{instanceId}\"", log);

    static bool Run(string exe, string arguments, Action<string> log)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // HidHide's client has been seen to hang (run without administrator rights): never block for good.
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                log($"{Path.GetFileName(exe)} {arguments}: no answer, stopped");
                return false;
            }
            string output = stdout.Result + stderr.Result;
            if (process.ExitCode != 0)
            {
                log($"{Path.GetFileName(exe)} {arguments}: exit {process.ExitCode} {output.Trim()}");
                return false;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            log($"{Path.GetFileName(exe)}: {e.Message}");
            return false;
        }
    }

    /// <summary>Device instance ID (what HidHide and pnputil take) of a HID interface path.</summary>
    public static string? InstanceIdOf(string interfacePath)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
        int size = 512;
        var buffer = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, buffer, ref size, 0) != 0)
            return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_PropertyW(string path, ref DEVPROPKEY key, out uint type, byte[] buffer,
                                                         ref int size, uint flags);
}
