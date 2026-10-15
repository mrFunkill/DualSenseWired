// The real controller behind the virtual wired DualSense: a DualSense connected over Bluetooth.
// Input reports are translated BT 0x31 -> USB 0x01; game output is merged (OutputState) and sent as
// BT 0x31 at most every MinOutputInterval; feature reports are forwarded where Bluetooth has them.
// The virtual controller stays while the Bluetooth one is away; it is re-attached when it returns.

using System.Threading.Channels;
using DualSenseWired.Device;
using DualSenseWired.Hid;
using DualSenseWired.Protocol;

namespace DualSenseWired.Bridge;

public sealed class BluetoothLink : IControllerLink, IDisposable
{
    static readonly TimeSpan MinOutputInterval = TimeSpan.FromMilliseconds(8);
    static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);
    static readonly HashSet<ushort> DualSensePids = new() { 0x0CE6, 0x0DF2 };

    readonly Action<string> _log;
    readonly CancellationTokenSource _cts = new();
    readonly Channel<byte[]> _inputs = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly OutputState _direct = new();
    readonly OutputState _history = new();     // everything the game set, re-sent after a reconnect
    readonly SemaphoreSlim _outputSignal = new(0);
    readonly object _deviceLock = new();
    HidDevice? _device;
    byte[]? _lastInput;
    int _seq;
    long _btReports, _delivered, _outputs;
    DateTime _lastModeSwitch;

    public BluetoothLink(Action<string> log)
    {
        _log = log;
        _ = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        _ = Task.Run(() => OutputLoopAsync(_cts.Token));
        _ = Task.Run(() => StatsLoopAsync(_cts.Token));
    }

    public bool Connected => _device != null;

    public static HidDevice? FindController()
    {
        foreach (string path in HidDevice.EnumeratePaths())
        {
            if (!path.Contains("054c", StringComparison.OrdinalIgnoreCase))
                continue;
            var device = HidDevice.Open(path);
            if (device == null)
                continue;
            if (device.VendorId == 0x054C && DualSensePids.Contains(device.ProductId) && device.IsBluetooth)
                return device;
            device.Dispose();
        }
        return null;
    }

    // -- controller side --

    async Task ConnectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var device = FindController();
            if (device == null)
            {
                await Task.Delay(RescanInterval, ct);
                continue;
            }
            try
            {
                // Reading calibration switches the controller to the full 0x31 input report.
                device.GetFeature(DualSenseProtocol.FeatureCalibration);
                lock (_deviceLock)
                    _device = device;
                _log($"Bluetooth DualSense connected (input {device.InputReportLength} B, output {device.OutputReportLength} B, " +
                     $"feature {device.FeatureReportLength} B)");
                var restore = _history.Snapshot();
                if (restore != null)
                {
                    _direct.Update(restore);    // the controller forgot what the game had set
                    _outputSignal.Release();
                }
                await ReadLoopAsync(device, ct);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                if (ct.IsCancellationRequested)
                    break;
                _log($"Bluetooth DualSense gone: {e.Message}");
            }
            finally
            {
                lock (_deviceLock)
                    _device = null;
                device.Dispose();
            }
            // Games keep the virtual controller; release whatever was held.
            _inputs.Writer.TryWrite(NeutralInput(_lastInput));
        }
    }

    async Task ReadLoopAsync(HidDevice device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] report = await device.ReadAsync(ct);
            Interlocked.Increment(ref _btReports);
            var usb = DualSenseProtocol.BtInputToUsb(report);
            if (usb != null)
            {
                _lastInput = usb;
                _inputs.Writer.TryWrite(usb);
            }
            else if (report[0] == DualSenseProtocol.UsbInputReport && DateTime.UtcNow - _lastModeSwitch > TimeSpan.FromSeconds(1))
            {
                // Simple Bluetooth report: ask for the full one again.
                _lastModeSwitch = DateTime.UtcNow;
                try { device.GetFeature(DualSenseProtocol.FeatureCalibration); } catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    async Task OutputLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _outputSignal.WaitAsync(ct);
            await Task.Delay(MinOutputInterval, ct);     // merge whatever else arrives meanwhile
            var device = _device;
            var common = device != null ? _direct.Take() : null;
            if (device == null || common == null)
                continue;
            try
            {
                await device.WriteAsync(DualSenseProtocol.CommonToBt(common, _seq++), ct);
            }
            catch (IOException e)
            {
                _log($"output to the controller failed: {e.Message}");
            }
        }
    }

    // -- virtual controller side (IControllerLink) --

    public async Task<byte[]> NextInputReportAsync(CancellationToken ct)
    {
        byte[] report = await _inputs.Reader.ReadAsync(ct);
        Interlocked.Increment(ref _delivered);
        return report;
    }

    public void OutputReport(byte[] report)
    {
        if (report.Length < 2 || report[0] != DualSenseProtocol.UsbOutputReport)
            return;
        Interlocked.Increment(ref _outputs);
        var common = report.AsSpan(1, Math.Min(DualSenseProtocol.CommonSize, report.Length - 1));
        _history.Update(common);
        _direct.Update(common);
        _outputSignal.Release();
    }

    public byte[]? GetFeature(byte reportId, int length)
    {
        var data = new byte[length];
        data[0] = reportId;
        var device = _device;
        if (device != null && DualSenseProtocol.BtFeatureReports.Contains(reportId))
        {
            try
            {
                var bt = device.GetFeature(reportId);
                bt.AsSpan(0, Math.Min(bt.Length, length)).CopyTo(data);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                _log($"GET feature 0x{reportId:x2} failed: {e.Message}");
            }
        }
        if (reportId == DualSenseProtocol.FeaturePairing)
        {
            // Until the Bluetooth controller is hidden (HidHide), Steam sees both: with the same
            // serial it would treat the "USB" one as the same controller plugged in and power the
            // Bluetooth one off (seen on the Deck). A locally administered MAC keeps them apart.
            data[6] ^= 0x02;
        }
        return data;
    }

    public void SetFeature(byte[] report)
    {
        byte id = report[0];
        if (id == DualSenseProtocol.BlockedSetFeature)
        {
            _log($"SET feature 0x{id:x2} not forwarded: {Convert.ToHexString(report, 0, Math.Min(8, report.Length))}");
            return;
        }
        var device = _device;
        if (device == null || !DualSenseProtocol.BtFeatureReports.Contains(id))
            return;
        int size = DualSenseDescriptors.FeatureSizes.GetValueOrDefault(id, report.Length);
        var bt = new byte[size];
        report.AsSpan(0, Math.Min(report.Length, size)).CopyTo(bt);
        DualSenseProtocol.WriteCrc(bt, DualSenseProtocol.FeatureSetCrcSeed);
        try
        {
            device.SetFeature(bt);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _log($"SET feature 0x{id:x2} failed: {e.Message}");
        }
    }

    public void AudioOut(ReadOnlySpan<byte> pcm)
    {
        // Next milestone: Opus speaker audio and HD haptics to the controller.
    }

    // -- misc --

    static byte[] NeutralInput(byte[]? last)
    {
        var r = last != null ? (byte[])last.Clone() : new byte[64];
        r[0] = DualSenseProtocol.UsbInputReport;
        r[1] = r[2] = r[3] = r[4] = 0x80;   // sticks centred
        r[5] = r[6] = 0;                    // triggers released
        r[8] = 0x08;                        // d-pad released, no face buttons
        r[9] = r[10] = 0;
        r[33] |= 0x80;                      // touch points: no contact
        r[37] |= 0x80;
        return r;
    }

    async Task StatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            long bt = Interlocked.Exchange(ref _btReports, 0);
            long delivered = Interlocked.Exchange(ref _delivered, 0);
            long outputs = Interlocked.Exchange(ref _outputs, 0);
            if (Connected)
                _log($"controller reports {bt}, delivered {delivered}, game outputs {outputs} in 30 s");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_deviceLock)
            _device?.Dispose();
    }
}
