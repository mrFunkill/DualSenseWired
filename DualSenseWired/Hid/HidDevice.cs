// Minimal Windows HID access: enumerate HID interfaces, open one, read/write reports, get/set
// feature reports (SetupAPI + hid.dll).

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DualSenseWired.Hid;

public sealed class HidDevice : IDisposable
{
    public string Path { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public int InputReportLength { get; }
    public int OutputReportLength { get; }
    public int FeatureReportLength { get; }
    /// <summary>Connected over Bluetooth (the interface path names the HID-over-BT service).</summary>
    public bool IsBluetooth => Path.Contains("{00001124-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase);

    readonly SafeFileHandle _handle;
    readonly FileStream _stream;

    HidDevice(string path, SafeFileHandle handle, HIDD_ATTRIBUTES attributes, HIDP_CAPS caps)
    {
        Path = path;
        _handle = handle;
        VendorId = attributes.VendorID;
        ProductId = attributes.ProductID;
        InputReportLength = caps.InputReportByteLength;
        OutputReportLength = caps.OutputReportByteLength;
        FeatureReportLength = caps.FeatureReportByteLength;
        _stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
    }

    public static IEnumerable<string> EnumeratePaths()
    {
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1))
            yield break;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(detail + 4)!;
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>Open a HID interface, or null if it cannot be opened (in use exclusively, no access).</summary>
    public static HidDevice? Open(string path)
    {
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            handle.Dispose();
            return null;
        }
        try
        {
            HidP_GetCaps(preparsed, out HIDP_CAPS caps);
            return new HidDevice(path, handle, attributes, caps);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>Next input report (first byte = report ID).</summary>
    public async Task<byte[]> ReadAsync(CancellationToken ct)
    {
        var buffer = new byte[InputReportLength];
        int n = await _stream.ReadAsync(buffer, ct);
        if (n == 0)
            throw new IOException("device closed");
        return n == buffer.Length ? buffer : buffer[..n];
    }

    /// <summary>Write an output report; it is padded to the device's output report length.</summary>
    public Task WriteAsync(byte[] report, CancellationToken ct)
    {
        byte[] buffer = report;
        if (report.Length < OutputReportLength)
        {
            buffer = new byte[OutputReportLength];
            report.CopyTo(buffer, 0);
        }
        return _stream.WriteAsync(buffer, ct).AsTask();
    }

    /// <summary>GET_REPORT of a feature report; returns the full buffer (report ID first).</summary>
    public byte[] GetFeature(byte reportId)
    {
        var buffer = new byte[FeatureReportLength];
        buffer[0] = reportId;
        if (!HidD_GetFeature(_handle, buffer, buffer.Length))
            throw new Win32Exception();
        return buffer;
    }

    public void SetFeature(byte[] report)
    {
        byte[] buffer = report;
        if (report.Length < FeatureReportLength)
        {
            buffer = new byte[FeatureReportLength];
            report.CopyTo(buffer, 0);
        }
        if (!HidD_SetFeature(_handle, buffer, buffer.Length))
            throw new Win32Exception();
    }

    /// <summary>
    /// Size of Windows' input report queue for this handle (default 32). Reports are full state
    /// snapshots: a long queue only means reading old ones once we fell behind for a moment.
    /// </summary>
    public bool SetInputBuffers(int count) => HidD_SetNumInputBuffers(_handle, count);

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }

    // -- interop --

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
            NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetNumInputBuffers(SafeFileHandle device, int count);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail,
                                                       int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
                                            uint flags, IntPtr template);
}
