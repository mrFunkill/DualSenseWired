// Milestone 1: a virtual wired DualSense (HID + USB audio) attached through usbip-win2.
// Run, then check Device Manager / Sound settings / Steam for "DualSense Wireless Controller".

using System.Diagnostics;
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

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var device = new VirtualDualSense(new TestLink(Log), Log);
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
