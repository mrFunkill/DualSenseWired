// DualSense report formats over USB and Bluetooth, as worked out for the Steam Deck plugin
// (decky-dualsense-wired/py_modules/dualsense_bridge.py, research/FINDINGS.md).

namespace DualSenseWired.Protocol;

public static class DualSenseProtocol
{
    public const byte UsbInputReport = 0x01;     // 64 bytes
    public const byte UsbOutputReport = 0x02;    // 48 bytes
    public const byte BtInputReport = 0x31;      // 78 bytes
    public const byte BtOutputReport = 0x31;     // 78 bytes
    public const int BtOutputReportSize = 78;
    public const int CommonSize = 47;            // common part of output reports
    public const byte BtOutputTag = 0x10;

    public const byte OutputCrcSeed = 0xA2;      // Bluetooth output reports
    public const byte FeatureSetCrcSeed = 0x53;  // Bluetooth SET_REPORT(feature)

    public const byte FeatureCalibration = 0x05;
    public const byte FeaturePairing = 0x09;

    /// <summary>Feature reports that exist on Bluetooth; the USB-only ones are never asked for over BT
    /// (BlueZ timed out and the controller dropped the link on the Deck).</summary>
    public static readonly HashSet<byte> BtFeatureReports = new() { 0x05, 0x08, 0x09, 0x20, 0x22, 0x80, 0x81, 0x82, 0x83, 0xF0, 0xF1, 0xF2 };

    /// <summary>SET feature 0x08 ("08 02 ...", sent by games at start-up) powers the controller off.</summary>
    public const byte BlockedSetFeature = 0x08;

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>CRC-32 (zlib) over the seed byte followed by the data.</summary>
    public static uint Crc32(byte seed, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        crc = CrcTable[(crc ^ seed) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>BT input 0x31 -> USB input 0x01 (the same state, two header bytes shorter); null otherwise.</summary>
    public static byte[]? BtInputToUsb(byte[] bt)
    {
        if (bt.Length < 65 || bt[0] != BtInputReport)
            return null;
        var usb = new byte[64];
        usb[0] = UsbInputReport;
        bt.AsSpan(2, 63).CopyTo(usb.AsSpan(1));
        return usb;
    }

    /// <summary>Common output part (47 bytes) -> BT output report 0x31.</summary>
    public static byte[] CommonToBt(ReadOnlySpan<byte> common, int seq)
    {
        var bt = new byte[BtOutputReportSize];
        bt[0] = BtOutputReport;
        bt[1] = (byte)((seq & 0x0F) << 4);
        bt[2] = BtOutputTag;
        common[..Math.Min(common.Length, CommonSize)].CopyTo(bt.AsSpan(3));
        WriteCrc(bt, OutputCrcSeed);
        return bt;
    }

    /// <summary>Put the CRC32 of everything before it into the last 4 bytes.</summary>
    public static void WriteCrc(byte[] report, byte seed)
    {
        uint crc = Crc32(seed, report.AsSpan(0, report.Length - 4));
        BitConverter.TryWriteBytes(report.AsSpan(report.Length - 4), crc);   // little-endian
    }
}

/// <summary>
/// Merges the common part of output reports the way the controller applies them: each field group
/// only when its valid flag is set. Several game reports can then be sent as one (inside an audio
/// stream report, or paced) without losing a trigger or LED change from an earlier one.
/// </summary>
public sealed class OutputState
{
    // (flag byte index, flag bits, byte offsets of the field group)
    static readonly (int Flag, byte Bits, int[] Offsets)[] Groups =
    {
        (0, 0x03, new[] { 2, 3 }),                              // rumble motors
        (0, 0x04, Enumerable.Range(10, 11).ToArray()),           // right trigger effect
        (0, 0x08, Enumerable.Range(21, 11).ToArray()),           // left trigger effect
        (0, 0x10, new[] { 4 }),                                  // headphone volume
        (0, 0x20, new[] { 5 }),                                  // speaker volume
        (0, 0x40, new[] { 6 }),                                  // microphone volume
        (0, 0x80, new[] { 7 }),                                  // audio control
        (1, 0x01, new[] { 8 }),                                  // mute button LED
        (1, 0x02, new[] { 9 }),                                  // power save control
        (1, 0x04, new[] { 44, 45, 46 }),                         // lightbar colour
        (1, 0x10, new[] { 43 }),                                 // player LEDs
        (38, 0x02, new[] { 41 }),                                // lightbar setup
        (38, 0x01, new[] { 42 }),                                // LED brightness
    };
    static readonly int[] FlagBytes = { 0, 1, 38 };
    static readonly HashSet<int> Grouped = Groups.SelectMany(g => g.Offsets).Concat(FlagBytes).ToHashSet();

    readonly byte[] _state = new byte[DualSenseProtocol.CommonSize];
    readonly Dictionary<int, byte> _pending = FlagBytes.ToDictionary(i => i, _ => (byte)0);
    readonly Dictionary<int, byte> _ever = FlagBytes.ToDictionary(i => i, _ => (byte)0);
    readonly object _lock = new();

    public void Update(ReadOnlySpan<byte> common)
    {
        var c = new byte[DualSenseProtocol.CommonSize];
        common[..Math.Min(common.Length, c.Length)].CopyTo(c);
        lock (_lock)
        {
            foreach (var (flag, bits, offsets) in Groups)
                if ((c[flag] & bits) != 0)
                    foreach (int o in offsets)
                        _state[o] = c[o];
            for (int o = 0; o < c.Length; o++)
                if (!Grouped.Contains(o))
                    _state[o] = c[o];       // fields without a known flag: latest wins
            foreach (int i in FlagBytes)
            {
                _pending[i] |= c[i];
                _ever[i] |= c[i];
            }
        }
    }

    public bool HasPending
    {
        get { lock (_lock) return _pending.Values.Any(v => v != 0); }
    }

    /// <summary>Merged state with the flags accumulated since the last Take(), or null.</summary>
    public byte[]? Take()
    {
        lock (_lock)
        {
            if (!_pending.Values.Any(v => v != 0))
                return null;
            var output = (byte[])_state.Clone();
            foreach (int i in FlagBytes)
            {
                output[i] = _pending[i];
                _pending[i] = 0;
            }
            return output;
        }
    }

    /// <summary>Everything set so far (all flags ever seen), e.g. for a controller that reconnected.</summary>
    public byte[]? Snapshot()
    {
        lock (_lock)
        {
            if (!_ever.Values.Any(v => v != 0))
                return null;
            var output = (byte[])_state.Clone();
            foreach (int i in FlagBytes)
                output[i] = _ever[i];
            return output;
        }
    }
}
