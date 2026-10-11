using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DuoSeat.Service;

// ---- mirrors of include/duoseat_shared.h (keep in sync!) ----

[StructLayout(LayoutKind.Sequential)]
internal struct DuoEvent                       // DUOSEAT_EVENT, 28 bytes
{
    public uint DeviceId;
    public uint Type;
    public ushort MakeCode;
    public ushort KeyFlags;
    public ushort MouseFlags;
    public ushort ButtonFlags;
    public ushort ButtonData;
    public ushort Reserved;
    public int LastX;
    public int LastY;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DuoDeviceInfo           // DUOSEAT_DEVICE_INFO, 272 bytes
{
    public uint DeviceId;
    public uint Type;
    public uint Station;
    public uint Reserved;
    public fixed char InstanceId[128];
}

[StructLayout(LayoutKind.Sequential)]
internal struct DuoSetStation                  // DUOSEAT_SET_STATION, 8 bytes
{
    public uint DeviceId;
    public uint Station;
}

internal sealed record SeatDevice(uint Id, uint Type, uint Station, string InstanceId)
{
    public string TypeName => Type switch { 1 => "Keyboard", 2 => "Mouse", _ => "Unknown" };
}

/// <summary>Talks to \\.\DuoSeatCtl (the control device of DuoSeatFilter.sys).</summary>
internal sealed unsafe class DriverClient : IDisposable
{
    public const int ProtocolVersion = 1;
    public const uint StationNormal = 1, StationSeat2 = 2, StationMonitor = 3;

    private const uint IOCTL_GET_VERSION = 0x80002000;
    private const uint IOCTL_GET_DEVICES = 0x80002004;
    private const uint IOCTL_SET_STATION = 0x80002008;
    private const uint IOCTL_SET_ROUTING = 0x8000200C;
    private const uint IOCTL_READ_EVENTS = 0x80002010;

    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_OPERATION_ABORTED = 995;
    private const uint WAIT_TIMEOUT = 0x102;

    private readonly SafeFileHandle _handle;

    private DriverClient(SafeFileHandle handle) => _handle = handle;

    public static DriverClient Open()
    {
        SafeFileHandle h = CreateFile(@"\\.\DuoSeatCtl", 0xC0000000 /*GENERIC_READ|WRITE*/, 3 /*share r/w*/,
            IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x40000000 /*FILE_FLAG_OVERLAPPED*/, IntPtr.Zero);
        if (h.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Cannot open \\\\.\\DuoSeatCtl. Is DuoSeatFilter.sys installed and loaded (and are you Administrator)?");

        var client = new DriverClient(h);
        uint version = client.GetVersion();
        if (version != ProtocolVersion)
            throw new InvalidOperationException($"Driver protocol {version} does not match service protocol {ProtocolVersion}.");
        return client;
    }

    public uint GetVersion()
    {
        uint v = 0;
        Ioctl(IOCTL_GET_VERSION, null, 0, &v, sizeof(uint));
        return v;
    }

    public List<SeatDevice> GetDevices()
    {
        var buf = new DuoDeviceInfo[32];
        var list = new List<SeatDevice>();
        fixed (DuoDeviceInfo* p = buf)
        {
            int bytes = Ioctl(IOCTL_GET_DEVICES, null, 0, p, sizeof(DuoDeviceInfo) * buf.Length);
            int count = bytes / sizeof(DuoDeviceInfo);
            for (int i = 0; i < count; i++)
                list.Add(new SeatDevice(p[i].DeviceId, p[i].Type, p[i].Station, new string(p[i].InstanceId)));
        }
        return list;
    }

    public void SetStation(uint deviceId, uint station)
    {
        var req = new DuoSetStation { DeviceId = deviceId, Station = station };
        Ioctl(IOCTL_SET_STATION, &req, sizeof(DuoSetStation), null, 0);
    }

    public void SetRouting(bool enabled)
    {
        uint v = enabled ? 1u : 0u;
        Ioctl(IOCTL_SET_ROUTING, &v, sizeof(uint), null, 0);
    }

    /// <summary>Blocks until at least one event is available (or the token is cancelled). Returns the number of events.</summary>
    public int ReadEvents(DuoEvent[] buffer, CancellationToken ct)
    {
        fixed (DuoEvent* p = buffer)
        {
            int bytes = Ioctl(IOCTL_READ_EVENTS, null, 0, p, sizeof(DuoEvent) * buffer.Length, ct);
            return bytes / sizeof(DuoEvent);
        }
    }

    private int Ioctl(uint code, void* input, int inputLength, void* output, int outputLength, CancellationToken ct = default)
    {
        IntPtr evt = CreateEvent(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            NativeOverlapped ov = default;
            ov.EventHandle = evt;

            if (!DeviceIoControl(_handle, code, input, (uint)inputLength, output, (uint)outputLength, out uint bytes, &ov))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) throw new Win32Exception(err);

                while (WaitForSingleObject(evt, 200) == WAIT_TIMEOUT)
                {
                    if (ct.IsCancellationRequested)
                    {
                        CancelIoEx(_handle, &ov);
                        WaitForSingleObject(evt, uint.MaxValue);   // the cancelled request completes promptly
                        break;
                    }
                }
                if (!GetOverlappedResult(_handle, &ov, out bytes, false))
                {
                    err = Marshal.GetLastWin32Error();
                    if (err == ERROR_OPERATION_ABORTED) return 0;
                    throw new Win32Exception(err);
                }
            }
            return (int)bytes;
        }
        finally
        {
            CloseHandle(evt);
        }
    }

    public void Dispose() => _handle.Dispose();   // driver switches routing off when the last handle closes

    // ---- P/Invoke ----
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint bytesReturned, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle device, NativeOverlapped* overlapped, out uint bytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle device, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
