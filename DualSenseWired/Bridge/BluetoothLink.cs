// The real controller behind the virtual wired DualSense: a DualSense connected over Bluetooth.
// Input reports are translated BT 0x31 -> USB 0x01; game output and the host's audio go to the
// controller through ControllerStream; feature reports are forwarded where Bluetooth has them.
// The virtual controller stays while the Bluetooth one is away; it is re-attached when it returns.

using System.Threading.Channels;
using DualSenseWired.Device;
using DualSenseWired.Hid;
using DualSenseWired.Protocol;

namespace DualSenseWired.Bridge;

public sealed class BluetoothLink : IControllerLink, IDisposable
{
    static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);
    static readonly HashSet<ushort> DualSensePids = new() { 0x0CE6, 0x0DF2 };

    readonly Action<string> _log;
    readonly CancellationTokenSource _cts = new();
    readonly Channel<byte[]> _inputs = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly OutputState _history = new();     // everything the game set, re-sent after a reconnect
    readonly ControllerStream _stream;
    readonly object _deviceLock = new();
    HidDevice? _device;
    readonly HashSet<string> _hidden = new();      // instance IDs we hid with HidHide
    readonly HashSet<string> _hideFailed = new();
    byte[]? _lastInput;
    long _btReports, _delivered, _outputs;
    DateTime _lastModeSwitch;

    public BluetoothLink(Action<string> log)
    {
        _log = log;
        _stream = new ControllerStream(log);
        HidHide.RegisterSelf(log);
        _ = Task.Run(() => ConnectionLoopAsync(_cts.Token));
        _ = Task.Run(() => StatsLoopAsync(_cts.Token));
    }

    public bool Connected => _device != null;
    /// <summary>Audio/haptics stream to the controller (options and state).</summary>
    public ControllerStream Stream => _stream;
    /// <summary>Button remapping applied to what games see, or null.</summary>
    public ButtonMap? ButtonMap { get; set; }
    /// <summary>USB product ID of the connected controller (0x0CE6 DualSense, 0x0DF2 DualSense Edge).</summary>
    public ushort? ProductId => _device?.ProductId;

    /// <summary>Pass the games' adaptive trigger effects to the controller.</summary>
    public bool AdaptiveTriggers
    {
        get => _adaptiveTriggers;
        set
        {
            if (_adaptiveTriggers == value)
                return;
            _adaptiveTriggers = value;
            if (!value)
            {
                // Release whatever effect is active now: both triggers set to "off".
                var reset = new byte[DualSenseProtocol.CommonSize];
                reset[0] = TriggerFlags;
                _stream.SubmitOutput(reset);
            }
            else if (_history.Snapshot() is { } restore)
                _stream.SubmitOutput(restore);
        }
    }
    bool _adaptiveTriggers = true;
    const byte TriggerFlags = 0x0C;     // valid_flag0: right (0x04) and left (0x08) trigger effects
    /// <summary>Battery level 0..100 and charging state from the last input report, if known.</summary>
    public (int Percent, bool Charging, bool Full)? Battery
    {
        get
        {
            var r = _lastInput;
            if (r == null || !Connected)
                return null;
            int level = r[53] & 0x0F, status = r[53] >> 4;
            return (Math.Min(100, level * 10 + 5), status == 1, status == 2);
        }
    }

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
            string? instanceId = HidHide.InstanceIdOf(device.Path);
            if (instanceId != null && !_hidden.Contains(instanceId) && !_hideFailed.Contains(instanceId))
            {
                // Hiding restarts the device: open it again afterwards.
                device.Dispose();
                if (HidHide.Hide(instanceId, _log))
                    _hidden.Add(instanceId);
                else
                    _hideFailed.Add(instanceId);
                await Task.Delay(RescanInterval, ct);
                continue;
            }
            try
            {
                // Reading calibration switches the controller to the full 0x31 input report.
                device.GetFeature(DualSenseProtocol.FeatureCalibration);
                lock (_deviceLock)
                    _device = device;
                _stream.Device = device;
                _log($"Bluetooth DualSense connected (input {device.InputReportLength} B, output {device.OutputReportLength} B, " +
                     $"feature {device.FeatureReportLength} B)");
                var restore = _history.Snapshot();
                if (restore != null)
                {
                    if (!_adaptiveTriggers)
                        restore[0] &= unchecked((byte)~TriggerFlags);
                    _stream.SubmitOutput(restore);    // the controller forgot what the game had set
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
                _stream.Device = null;
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
            _stream.InputReport(report);
            var usb = DualSenseProtocol.BtInputToUsb(report);
            if (usb != null)
            {
                _lastInput = (byte[])usb.Clone();
                ButtonMap?.Apply(usb);
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
        var common = report.AsSpan(1, Math.Min(DualSenseProtocol.CommonSize, report.Length - 1)).ToArray();
        _history.Update(common);
        if (!_adaptiveTriggers)
            common[0] &= unchecked((byte)~TriggerFlags);
        _stream.SubmitOutput(common);
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
        if (reportId == DualSenseProtocol.FeaturePairing && _hidden.Count == 0)
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

    public void AudioOut(ReadOnlySpan<byte> pcm) => _stream.AudioIn(pcm);

    public void SpeakerVolume(bool mute, double decibels)
    {
        _stream.SpeakerVolume(mute, decibels);
        _log($"speaker volume {decibels:0.#} dB{(mute ? ", muted" : "")}");
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

    int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        _stream.Dispose();
        lock (_deviceLock)
            _device?.Dispose();
        foreach (string instanceId in _hidden)
            HidHide.Unhide(instanceId, _log);
        _hidden.Clear();
    }
}
