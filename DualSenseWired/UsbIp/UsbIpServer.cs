// USB/IP server side (protocol 1.1.1, https://docs.kernel.org/usb/usbip_protocol.html) that exports
// one virtual USB device. The usbip-win2 client driver attaches it as a real USB device on its
// emulated host controller, so Windows loads its normal class drivers (HID, USB audio) for it.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DualSenseWired.UsbIp;

/// <summary>One URB submitted by the host (USBIP_CMD_SUBMIT).</summary>
public sealed class Urb
{
    public uint SeqNum;
    public int Endpoint;            // endpoint number without the direction bit
    public bool In;                 // device to host
    public uint TransferFlags;
    public int BufferLength;
    public int StartFrame;
    public int NumberOfPackets;     // > 0 for isochronous transfers
    public int Interval;
    public byte[] Setup = new byte[8];
    public byte[] OutData = Array.Empty<byte>();
    public IsoPacket[] IsoPackets = Array.Empty<IsoPacket>();
    public CancellationToken Cancel;
}

public struct IsoPacket
{
    public int Offset, Length, ActualLength, Status;
}

/// <summary>Completion of a URB (USBIP_RET_SUBMIT).</summary>
public sealed class UrbResult
{
    public int Status;              // 0 or a negative Linux errno (-32 = EPIPE: stall)
    public byte[] InData = Array.Empty<byte>();
    public int ActualLength;        // OUT: bytes taken; IN: InData.Length unless set
    public int StartFrame;
    public IsoPacket[] IsoPackets = Array.Empty<IsoPacket>();

    public static UrbResult Stall() => new() { Status = -32 };
    public static UrbResult Ok(byte[]? data = null) => new() { InData = data ?? Array.Empty<byte>(), ActualLength = data?.Length ?? 0 };
}

public interface IUsbDevice
{
    string BusId { get; }
    ushort VendorId { get; }
    ushort ProductId { get; }
    ushort BcdDevice { get; }
    byte DeviceClass { get; }
    byte DeviceSubClass { get; }
    byte DeviceProtocol { get; }
    byte ConfigurationValue { get; }
    byte NumConfigurations { get; }
    /// <summary>(class, subclass, protocol) per interface, for the device list.</summary>
    IReadOnlyList<(byte Class, byte SubClass, byte Protocol)> Interfaces { get; }
    /// <summary>USB speed as Linux enum usb_device_speed (2 = full, 3 = high).</summary>
    uint Speed { get; }

    /// <summary>Handle a URB; may complete later (interrupt IN waiting for data).</summary>
    Task<UrbResult> SubmitAsync(Urb urb);

    void Attached();
    void Detached();
}

public sealed class UsbIpServer
{
    const ushort Version = 0x0111;
    const ushort OpReqDevlist = 0x8005, OpRepDevlist = 0x0005;
    const ushort OpReqImport = 0x8003, OpRepImport = 0x0003;
    const uint CmdSubmit = 1, CmdUnlink = 2, RetSubmit = 3, RetUnlink = 4;
    const int EConnReset = -104;

    readonly IUsbDevice _device;
    readonly Action<string> _log;
    readonly int _port;

    public UsbIpServer(IUsbDevice device, Action<string> log, int port = 3240)
    {
        _device = device;
        _log = log;
        _port = port;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        _log($"USB/IP server on 127.0.0.1:{_port}, exporting {_device.BusId}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                try
                {
                    await HandleClientAsync(client.GetStream(), ct);
                }
                catch (Exception e) when (e is IOException or SocketException or EndOfStreamException)
                {
                    _log($"client disconnected: {e.Message}");
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    async Task HandleClientAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await ReadExactAsync(stream, header, ct);
        ushort code = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (code == OpReqDevlist)
        {
            await stream.WriteAsync(BuildDevlistReply(), ct);
            return;
        }
        if (code != OpReqImport)
        {
            _log($"unknown operation 0x{code:x4}");
            return;
        }
        var busId = new byte[32];
        await ReadExactAsync(stream, busId, ct);
        string requested = System.Text.Encoding.ASCII.GetString(busId).TrimEnd('\0');
        bool ok = requested == _device.BusId;
        var reply = new byte[8 + (ok ? 312 : 0)];
        BinaryPrimitives.WriteUInt16BigEndian(reply, Version);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), OpRepImport);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), ok ? 0u : 1u);
        if (ok)
            WriteDeviceInfo(reply.AsSpan(8));
        await stream.WriteAsync(reply, ct);
        if (!ok)
        {
            _log($"import of unknown bus id '{requested}' refused");
            return;
        }
        _log($"device {_device.BusId} attached");
        _device.Attached();
        try
        {
            await UrbLoopAsync(stream, ct);
        }
        finally
        {
            _device.Detached();
            _log($"device {_device.BusId} detached");
        }
    }

    byte[] BuildDevlistReply()
    {
        var ifaces = _device.Interfaces;
        var reply = new byte[8 + 4 + 312 + 4 * ifaces.Count];
        BinaryPrimitives.WriteUInt16BigEndian(reply, Version);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), OpRepDevlist);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8), 1);     // one device
        WriteDeviceInfo(reply.AsSpan(12));
        int off = 12 + 312;
        foreach (var (cls, sub, proto) in ifaces)
        {
            reply[off] = cls;
            reply[off + 1] = sub;
            reply[off + 2] = proto;
            off += 4;                                                     // + 1 padding byte
        }
        return reply;
    }

    void WriteDeviceInfo(Span<byte> b)
    {
        // struct usbip_usb_device: path[256], busid[32], busnum, devnum, speed, ids, classes, counts
        System.Text.Encoding.ASCII.GetBytes("/sys/devices/virtual/dualsense-wired/" + _device.BusId).CopyTo(b);
        System.Text.Encoding.ASCII.GetBytes(_device.BusId).CopyTo(b[256..]);
        BinaryPrimitives.WriteUInt32BigEndian(b[288..], 1);              // busnum
        BinaryPrimitives.WriteUInt32BigEndian(b[292..], 1);              // devnum
        BinaryPrimitives.WriteUInt32BigEndian(b[296..], _device.Speed);
        BinaryPrimitives.WriteUInt16BigEndian(b[300..], _device.VendorId);
        BinaryPrimitives.WriteUInt16BigEndian(b[302..], _device.ProductId);
        BinaryPrimitives.WriteUInt16BigEndian(b[304..], _device.BcdDevice);
        b[306] = _device.DeviceClass;
        b[307] = _device.DeviceSubClass;
        b[308] = _device.DeviceProtocol;
        b[309] = _device.ConfigurationValue;
        b[310] = _device.NumConfigurations;
        b[311] = (byte)_device.Interfaces.Count;
    }

    async Task UrbLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var pending = new ConcurrentDictionary<uint, CancellationTokenSource>();
        var writeLock = new SemaphoreSlim(1, 1);
        var header = new byte[48];
        while (!ct.IsCancellationRequested)
        {
            await ReadExactAsync(stream, header, ct);
            uint command = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint seqnum = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            uint direction = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
            uint ep = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));

            if (command == CmdUnlink)
            {
                uint victim = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
                int status = 0;
                if (pending.TryRemove(victim, out var cts))
                {
                    cts.Cancel();
                    status = EConnReset;
                }
                await SendAsync(stream, writeLock, BuildRetUnlink(seqnum, status), ct);
                continue;
            }
            if (command != CmdSubmit)
                throw new IOException($"unknown URB command {command}");

            var urb = new Urb
            {
                SeqNum = seqnum,
                Endpoint = (int)ep,
                In = direction == 1,
                TransferFlags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)),
                BufferLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24)),
                StartFrame = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(28)),
                NumberOfPackets = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(32)),
                Interval = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(36)),
            };
            header.AsSpan(40, 8).CopyTo(urb.Setup);
            if (!urb.In && urb.BufferLength > 0)
            {
                urb.OutData = new byte[urb.BufferLength];
                await ReadExactAsync(stream, urb.OutData, ct);
            }
            if (urb.NumberOfPackets > 0 && urb.NumberOfPackets != -1)  // -1 (0xffffffff) = not isochronous
            {
                var iso = new byte[16 * urb.NumberOfPackets];
                await ReadExactAsync(stream, iso, ct);
                urb.IsoPackets = ParseIsoPackets(iso, urb.NumberOfPackets);
            }

            var urbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            urb.Cancel = urbCts.Token;
            pending[seqnum] = urbCts;
            _ = CompleteAsync(stream, writeLock, urb, pending, ct);
        }
    }

    static IsoPacket[] ParseIsoPackets(byte[] iso, int count)
    {
        var packets = new IsoPacket[count];
        for (int i = 0; i < count; i++)
        {
            var s = iso.AsSpan(16 * i);
            packets[i] = new IsoPacket
            {
                Offset = BinaryPrimitives.ReadInt32BigEndian(s),
                Length = BinaryPrimitives.ReadInt32BigEndian(s[4..]),
            };
        }
        return packets;
    }

    async Task CompleteAsync(NetworkStream stream, SemaphoreSlim writeLock, Urb urb,
                             ConcurrentDictionary<uint, CancellationTokenSource> pending, CancellationToken ct)
    {
        UrbResult result;
        try
        {
            result = await _device.SubmitAsync(urb);
        }
        catch (OperationCanceledException)
        {
            return;     // unlinked: RET_UNLINK already sent, no RET_SUBMIT
        }
        catch (Exception e)
        {
            _log($"URB ep{urb.Endpoint} {(urb.In ? "IN" : "OUT")} failed: {e.Message}");
            result = UrbResult.Stall();
        }
        if (!pending.TryRemove(urb.SeqNum, out var cts))
            return;     // unlinked meanwhile
        cts.Dispose();
        try
        {
            await SendAsync(stream, writeLock, BuildRetSubmit(urb, result), ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    static byte[] BuildRetSubmit(Urb urb, UrbResult r)
    {
        bool iso = urb.NumberOfPackets > 0 && urb.NumberOfPackets != -1;
        int dataLen = urb.In ? (iso ? r.InData.Length : Math.Min(r.InData.Length, urb.BufferLength)) : 0;
        int actual = urb.In ? (iso ? r.ActualLength : dataLen) : r.ActualLength;
        var isoPackets = iso ? (r.IsoPackets.Length > 0 ? r.IsoPackets : DefaultIso(urb)) : Array.Empty<IsoPacket>();
        var b = new byte[48 + dataLen + 16 * isoPackets.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, RetSubmit);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), urb.SeqNum);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), r.Status);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(24), actual);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(28), r.StartFrame);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(32), iso ? isoPackets.Length : 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(36), 0);         // error_count
        r.InData.AsSpan(0, dataLen).CopyTo(b.AsSpan(48));
        int off = 48 + dataLen;
        foreach (var p in isoPackets)
        {
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(off), p.Offset);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(off + 4), p.Length);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(off + 8), p.ActualLength);
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(off + 12), p.Status);
            off += 16;
        }
        return b;
    }

    /// <summary>OUT isochronous: every packet taken in full.</summary>
    static IsoPacket[] DefaultIso(Urb urb)
    {
        var packets = new IsoPacket[urb.IsoPackets.Length];
        for (int i = 0; i < packets.Length; i++)
            packets[i] = urb.IsoPackets[i] with { ActualLength = urb.In ? 0 : urb.IsoPackets[i].Length };
        return packets;
    }

    static byte[] BuildRetUnlink(uint seqnum, int status)
    {
        var b = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(b, RetUnlink);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), seqnum);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), status);
        return b;
    }

    static async Task SendAsync(NetworkStream stream, SemaphoreSlim writeLock, byte[] data, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(data, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                throw new EndOfStreamException("connection closed");
            read += n;
        }
    }
}
