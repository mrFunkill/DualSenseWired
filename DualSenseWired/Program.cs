// A virtual wired DualSense (HID + USB audio) attached through usbip-win2, backed by the real
// controller connected over Bluetooth.

using System.Diagnostics;
using DualSenseWired.Bridge;
using DualSenseWired.Device;
using DualSenseWired.UsbIp;

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

const string UsbIpExe = @"C:\Program Files\USBip\usbip.exe";

// A typo in the descriptor tables would only show up as Windows refusing the device.
int declared = DualSenseDescriptors.Configuration[2] | DualSenseDescriptors.Configuration[3] << 8;
if (declared != DualSenseDescriptors.Configuration.Length || DualSenseDescriptors.HidDescriptor[1] != 0x21
    || (DualSenseDescriptors.HidDescriptor[7] | DualSenseDescriptors.HidDescriptor[8] << 8) != DualSenseDescriptors.HidReport.Length)
{
    Log($"descriptor tables are inconsistent: config {DualSenseDescriptors.Configuration.Length}/{declared} bytes, " +
        $"HID report {DualSenseDescriptors.HidReport.Length} bytes");
    return 1;
}

// Audio timing must not suffer when another app is in the foreground: no background power
// throttling (Windows 11 also ignores timer resolution requests of throttled processes).
Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
DisablePowerThrottling();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// --test: no real controller, a stand-in that only logs what the host sends.
IControllerLink link;
if (args.Contains("--test"))
{
    link = new TestLink(Log);
}
else
{
    using (var found = BluetoothLink.FindController())
    {
        if (found == null)
            Log("no Bluetooth DualSense yet: pair it in Windows Bluetooth settings (hold Create + PS), it will be picked up");
    }
    link = new BluetoothLink(Log);
}

var device = new VirtualDualSense(link, Log);
var server = new UsbIpServer(device, Log);
var serverTask = server.RunAsync(cts.Token);

if (!args.Contains("--no-attach"))
{
    await Task.Delay(500);
    var attach = Process.Start(new ProcessStartInfo(UsbIpExe, $"attach -r 127.0.0.1 -b {device.BusId}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    await attach.WaitForExitAsync();
    Log($"usbip attach: exit {attach.ExitCode} {(await attach.StandardOutput.ReadToEndAsync()).Trim()} {(await attach.StandardError.ReadToEndAsync()).Trim()}");
}

Log("running, Ctrl+C to stop");
try
{
    await serverTask;
}
catch (OperationCanceledException)
{
}
return 0;

static void DisablePowerThrottling()
{
    const int ProcessPowerThrottling = 4;
    const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;
    // PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask, StateMask = 0 (= not throttled) }
    var state = new uint[] { 1, ExecutionSpeed | IgnoreTimerResolution, 0 };
    var handle = System.Runtime.InteropServices.GCHandle.Alloc(state, System.Runtime.InteropServices.GCHandleType.Pinned);
    try
    {
        if (!SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, handle.AddrOfPinnedObject(), 12))
            Log($"could not disable power throttling: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }
    finally
    {
        handle.Free();
    }
}

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern bool SetProcessInformation(IntPtr process, int infoClass, IntPtr info, int size);
