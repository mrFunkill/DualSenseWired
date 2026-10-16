// DualSense Wired for Windows: a Bluetooth DualSense appears to games as a wired one (speaker, HD
// haptics, adaptive triggers), through a virtual USB device attached with usbip-win2.
//
//   DualSenseWired.exe              start (or show the running instance's settings)
//   DualSenseWired.exe --minimized  start in the tray only (autostart)
//   DualSenseWired.exe --stop       stop the running instance cleanly

using System.Diagnostics;
using System.Runtime.InteropServices;
using DualSenseWired.App;
using DualSenseWired.Device;

namespace DualSenseWired;

static class Program
{
    const string InstanceName = @"Local\DualSenseWired.Instance";
    const string ShowEventName = @"Local\DualSenseWired.Show";
    const string StopEventName = @"Local\DualSenseWired.Stop";

    [STAThread]
    static int Main(string[] args)
    {
        using var instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
            return SignalRunning(args.Contains("--stop") ? StopEventName : ShowEventName, args.Contains("--stop"));
        if (args.Contains("--stop"))
            return 0;

        ApplicationConfiguration.Initialize();
        if (!DescriptorsConsistent())
            return 1;
        if (!BridgeHost.UsbIpInstalled)
        {
            MessageBox.Show(Strings.Current.MissingUsbIp + "\nhttps://github.com/vadimgrn/usbip-win2/releases",
                            Strings.Current.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // Audio timing must not suffer when another app is in the foreground: no background power
        // throttling (Windows 11 also ignores timer resolution requests of throttled processes).
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        DisablePowerThrottling();
        Logger.Log("DualSense Wired started");

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);
        var app = new TrayApp(Settings.Load(), showSettings: !args.Contains("--minimized"));
        var ui = SynchronizationContext.Current!;
        var signals = new Thread(() =>
        {
            while (true)
            {
                int which = WaitHandle.WaitAny(new WaitHandle[] { showEvent, stopEvent });
                if (which == 0)
                    ui.Post(_ => app.ShowSettings(), null);
                else
                {
                    ui.Post(_ => app.Exit(), null);
                    return;
                }
            }
        }) { IsBackground = true };
        signals.Start();
        // Logoff/shutdown: give the controller back too.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => app.Exit();
        Application.Run(app);
        Logger.Log("DualSense Wired stopped");
        return 0;
    }

    /// <summary>Ask the running instance to show itself or stop; for --stop, wait until it has.</summary>
    static int SignalRunning(string eventName, bool waitForExit)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return 1;
        }
        if (!waitForExit)
            return 0;
        using var instance = new Mutex(false, InstanceName);
        try
        {
            if (instance.WaitOne(TimeSpan.FromSeconds(15)))
                instance.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
        }
        return 0;
    }

    /// <summary>A typo in the descriptor tables would only show up as Windows refusing the device.</summary>
    static bool DescriptorsConsistent()
    {
        int declared = DualSenseDescriptors.Configuration[2] | DualSenseDescriptors.Configuration[3] << 8;
        if (declared == DualSenseDescriptors.Configuration.Length && DualSenseDescriptors.HidDescriptor[1] == 0x21
            && (DualSenseDescriptors.HidDescriptor[7] | DualSenseDescriptors.HidDescriptor[8] << 8) == DualSenseDescriptors.HidReport.Length)
            return true;
        Logger.Log($"descriptor tables are inconsistent: config {DualSenseDescriptors.Configuration.Length}/{declared} bytes, " +
                   $"HID report {DualSenseDescriptors.HidReport.Length} bytes");
        return false;
    }

    static void DisablePowerThrottling()
    {
        const int ProcessPowerThrottling = 4;
        const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;
        // PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask, StateMask = 0 (= not throttled) }
        var state = new uint[] { 1, ExecutionSpeed | IgnoreTimerResolution, 0 };
        var handle = GCHandle.Alloc(state, GCHandleType.Pinned);
        try
        {
            if (!SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, handle.AddrOfPinnedObject(), 12))
                Logger.Log($"could not disable power throttling: error {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            handle.Free();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, IntPtr info, int size);
}
