// The virtual wired DualSense behind the USB/IP server: answers control requests (standard, HID,
// USB Audio 1.0), produces input reports on the interrupt IN endpoint and consumes output reports
// and the isochronous audio stream at the pace of a real USB bus.

using System.Buffers.Binary;
using System.Diagnostics;
using DualSenseWired.UsbIp;

namespace DualSenseWired.Device;

public interface IControllerLink
{
    /// <summary>Next USB input report 0x01 (64 bytes); completes when the controller sends one.</summary>
    Task<byte[]> NextInputReportAsync(CancellationToken ct);
    /// <summary>USB output report 0x02 from the host (rumble, triggers, LEDs).</summary>
    void OutputReport(byte[] report);
    /// <summary>GET_REPORT for a feature report (including its ID byte), or null to stall.</summary>
    byte[]? GetFeature(byte reportId, int length);
    /// <summary>SET_REPORT of a feature report (including its ID byte).</summary>
    void SetFeature(byte[] report);
    /// <summary>One millisecond of audio from the host: 48 frames x 4 channels x 16 bit.</summary>
    void AudioOut(ReadOnlySpan<byte> pcm);
    /// <summary>The host's volume control for the audio output (master mute and volume in dB).</summary>
    void SpeakerVolume(bool mute, double decibels);
}

public sealed class VirtualDualSense : IUsbDevice
{
    const int AudioOutFrameBytes = 4 * 2;       // 4 channels, 16 bit
    const int AudioInFrameBytes = 2 * 2;        // 2 channels, 16 bit
    const int SampleRate = 48000;

    readonly IControllerLink _link;
    readonly Action<string> _log;
    readonly int[] _altSetting = new int[4];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<int, double> _isoNextMs = new();
    readonly HashSet<string> _loggedRequests = new();
    byte _configuration;
    bool _speakerMute;
    short _speakerVolume;

    public VirtualDualSense(IControllerLink link, Action<string> log)
    {
        _link = link;
        _log = log;
    }

    public string BusId => "1-1";
    public ushort VendorId => DualSenseDescriptors.VendorId;
    public ushort ProductId => DualSenseDescriptors.ProductId;
    public ushort BcdDevice => DualSenseDescriptors.BcdDevice;
    public byte DeviceClass => 0;
    public byte DeviceSubClass => 0;
    public byte DeviceProtocol => 0;
    public byte ConfigurationValue => _configuration;
    public byte NumConfigurations => 1;
    public uint Speed => 3;     // USB_SPEED_HIGH, like the real controller

    public IReadOnlyList<(byte Class, byte SubClass, byte Protocol)> Interfaces { get; } = new[]
    {
        ((byte)1, (byte)1, (byte)0), ((byte)1, (byte)2, (byte)0), ((byte)1, (byte)2, (byte)0), ((byte)3, (byte)0, (byte)0),
    };

    public void Attached()
    {
        _isoNextMs.Clear();
    }

    public void Detached()
    {
        Array.Clear(_altSetting);
        _configuration = 0;
    }

    public Task<UrbResult> SubmitAsync(Urb urb)
    {
        if (urb.Endpoint == 0)
            return Task.FromResult(Control(urb));
        if (urb.Endpoint == DualSenseDescriptors.HidInEndpoint && urb.In)
            return InterruptInAsync(urb);
        if (urb.Endpoint == DualSenseDescriptors.HidOutEndpoint && !urb.In)
        {
            _link.OutputReport(urb.OutData);
            return Task.FromResult(new UrbResult { ActualLength = urb.OutData.Length });
        }
        if (urb.Endpoint == DualSenseDescriptors.AudioOutEndpoint && !urb.In)
            return AudioOutAsync(urb);
        if (urb.Endpoint == DualSenseDescriptors.AudioInEndpoint && urb.In)
            return AudioInAsync(urb);
        _log($"URB to unexpected endpoint {urb.Endpoint} {(urb.In ? "IN" : "OUT")}");
        return Task.FromResult(UrbResult.Stall());
    }

    // -- interrupt and isochronous endpoints --

    async Task<UrbResult> InterruptInAsync(Urb urb)
    {
        byte[] report = await _link.NextInputReportAsync(urb.Cancel);
        return UrbResult.Ok(report);
    }

    /// <summary>Wait until the bus would have carried these packets (1 ms each at high speed, interval 4).</summary>
    async Task PaceIsoAsync(Urb urb)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double start = _isoNextMs.TryGetValue(urb.Endpoint, out var next) && next > now - 20 ? next : now;
        double due = start + urb.NumberOfPackets;
        _isoNextMs[urb.Endpoint] = due;
        int wait = (int)(due - now);
        if (wait > 0)
            await Task.Delay(wait, urb.Cancel);
    }

    int _loggedAudioUrbs;

    async Task<UrbResult> AudioOutAsync(Urb urb)
    {
        if (_loggedAudioUrbs++ < 3)
            _log($"audio URB: {urb.NumberOfPackets} packets, lengths {string.Join(",", urb.IsoPackets.Take(4).Select(p => p.Length))}, " +
                 $"{urb.OutData.Length} bytes");
        await PaceIsoAsync(urb);
        var packets = new IsoPacket[urb.IsoPackets.Length];
        int total = 0;
        for (int i = 0; i < packets.Length; i++)
        {
            var p = urb.IsoPackets[i];
            if (p.Length > 0 && p.Offset + p.Length <= urb.OutData.Length)
                _link.AudioOut(urb.OutData.AsSpan(p.Offset, p.Length));
            packets[i] = p with { ActualLength = p.Length, Status = 0 };
            total += p.Length;
        }
        return new UrbResult { ActualLength = total, IsoPackets = packets };
    }

    async Task<UrbResult> AudioInAsync(Urb urb)
    {
        await PaceIsoAsync(urb);
        // Microphone: silence, 48 frames per 1 ms packet.
        int perPacket = SampleRate / 1000 * AudioInFrameBytes;
        var packets = new IsoPacket[urb.IsoPackets.Length];
        int total = 0;
        for (int i = 0; i < packets.Length; i++)
        {
            int len = Math.Min(perPacket, urb.IsoPackets[i].Length);
            packets[i] = urb.IsoPackets[i] with { ActualLength = len, Status = 0 };
            total += len;
        }
        return new UrbResult { InData = new byte[total], ActualLength = total, IsoPackets = packets };
    }

    // -- control endpoint --

    UrbResult Control(Urb urb)
    {
        byte requestType = urb.Setup[0];
        byte request = urb.Setup[1];
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(urb.Setup.AsSpan(2));
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(urb.Setup.AsSpan(4));
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(urb.Setup.AsSpan(6));
        int type = (requestType >> 5) & 3;
        int recipient = requestType & 0x1F;

        UrbResult? result = type switch
        {
            0 => Standard(request, recipient, value, index, length, urb),
            1 when recipient == 1 && (index & 0xFF) == DualSenseDescriptors.HidInterface => Hid(request, value, length, urb),
            1 when recipient == 1 => AudioControl(request, value, index, length, urb),
            1 when recipient == 2 => AudioEndpoint(request, value, length, urb),
            _ => null,
        };
        if (result == null)
        {
            string key = $"{requestType:x2} {request:x2} {value:x4} {index:x4}";
            if (_loggedRequests.Add(key))
                _log($"control request not handled (stall): {key} len {length}");
            return UrbResult.Stall();
        }
        if (result.InData.Length > length)
            result.InData = result.InData[..length];
        if ((requestType & 0x80) != 0)
            result.ActualLength = result.InData.Length;
        return result;
    }

    UrbResult? Standard(byte request, int recipient, ushort value, ushort index, ushort length, Urb urb)
    {
        switch (request)
        {
            case 0x00:  // GET_STATUS
                return UrbResult.Ok(new byte[2]);
            case 0x01:  // CLEAR_FEATURE (e.g. endpoint halt)
            case 0x03:  // SET_FEATURE
            case 0x05:  // SET_ADDRESS
                return UrbResult.Ok();
            case 0x06:  // GET_DESCRIPTOR
                return GetDescriptor(recipient, value, index);
            case 0x08:  // GET_CONFIGURATION
                return UrbResult.Ok(new[] { _configuration });
            case 0x09:  // SET_CONFIGURATION
                _configuration = (byte)value;
                _log($"configuration {value} selected");
                return UrbResult.Ok();
            case 0x0A:  // GET_INTERFACE
                return UrbResult.Ok(new[] { (byte)_altSetting[Math.Min(index & 0xFF, 3)] });
            case 0x0B:  // SET_INTERFACE
                int iface = index & 0xFF;
                if (iface < _altSetting.Length)
                {
                    _altSetting[iface] = value;
                    _isoNextMs.Clear();
                    _log($"interface {iface} alt setting {value}{(iface == 1 && value == 1 ? " (audio output starts)" : "")}");
                }
                return UrbResult.Ok();
        }
        return null;
    }

    UrbResult? GetDescriptor(int recipient, ushort value, ushort index)
    {
        int type = value >> 8, idx = value & 0xFF;
        if (recipient == 0)
        {
            return type switch
            {
                1 => UrbResult.Ok(DualSenseDescriptors.Device),
                2 => UrbResult.Ok(DualSenseDescriptors.Configuration),
                3 => idx switch
                {
                    0 => UrbResult.Ok(new byte[] { 0x04, 0x03, 0x09, 0x04 }),   // English (US)
                    1 => UrbResult.Ok(DualSenseDescriptors.StringDescriptor(DualSenseDescriptors.Manufacturer)),
                    2 => UrbResult.Ok(DualSenseDescriptors.StringDescriptor(DualSenseDescriptors.Product)),
                    _ => null,
                },
                _ => null,      // device qualifier, BOS ...: stall, as a high-speed-only device
            };
        }
        if (recipient == 1 && (index & 0xFF) == DualSenseDescriptors.HidInterface)
        {
            return type switch
            {
                0x21 => UrbResult.Ok(DualSenseDescriptors.HidDescriptor),
                0x22 => UrbResult.Ok(DualSenseDescriptors.HidReport),
                _ => null,
            };
        }
        return null;
    }

    UrbResult? Hid(byte request, ushort value, ushort length, Urb urb)
    {
        int reportType = value >> 8;
        byte reportId = (byte)value;
        switch (request)
        {
            case 0x01:  // GET_REPORT
                if (reportType != 3)
                    return null;
                byte[]? data = _link.GetFeature(reportId, DualSenseDescriptors.FeatureSizes.GetValueOrDefault(reportId, length));
                return data == null ? null : UrbResult.Ok(data);
            case 0x09:  // SET_REPORT
                if (reportType == 3)
                    _link.SetFeature(urb.OutData);
                else if (reportType == 2)
                    _link.OutputReport(urb.OutData);
                return new UrbResult { ActualLength = urb.OutData.Length };
            case 0x0A:  // SET_IDLE
            case 0x0B:  // SET_PROTOCOL
                return UrbResult.Ok();
            case 0x02:  // GET_IDLE
                return UrbResult.Ok(new byte[1]);
        }
        return null;
    }

    /// <summary>USB Audio 1.0 feature unit requests (mute, volume) on the audio control interface.</summary>
    UrbResult? AudioControl(byte request, ushort value, ushort index, ushort length, Urb urb)
    {
        int selector = value >> 8;
        if (selector == 1)  // mute
        {
            if (request == 0x01)
            {
                _speakerMute = urb.OutData.Length > 0 && urb.OutData[0] != 0;
                _link.SpeakerVolume(_speakerMute, _speakerVolume / 256.0);
                return new UrbResult { ActualLength = urb.OutData.Length };
            }
            return request == 0x81 ? UrbResult.Ok(new[] { (byte)(_speakerMute ? 1 : 0) }) : null;
        }
        if (selector == 2)  // volume, 1/256 dB
        {
            short v = request switch
            {
                0x81 => _speakerVolume,
                0x82 => unchecked((short)0xC400),   // -60 dB
                0x83 => 0,                          // 0 dB
                0x84 => 0x0100,                     // 1 dB steps
                0x01 => 0,
                _ => short.MinValue,
            };
            if (request == 0x01)
            {
                if (urb.OutData.Length >= 2)
                    _speakerVolume = BinaryPrimitives.ReadInt16LittleEndian(urb.OutData);
                _link.SpeakerVolume(_speakerMute, _speakerVolume / 256.0);
                return new UrbResult { ActualLength = urb.OutData.Length };
            }
            if (v == short.MinValue)
                return null;
            var b = new byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(b, v);
            return UrbResult.Ok(b);
        }
        return null;
    }

    /// <summary>Sampling frequency control on the isochronous endpoints (always 48 kHz).</summary>
    UrbResult? AudioEndpoint(byte request, ushort value, ushort length, Urb urb)
    {
        if (value >> 8 != 1)
            return null;
        if (request == 0x01)
            return new UrbResult { ActualLength = urb.OutData.Length };
        if (request == 0x81)
            return UrbResult.Ok(new byte[] { 0x80, 0xBB, 0x00 });
        return null;
    }
}
