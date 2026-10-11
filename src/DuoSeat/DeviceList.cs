using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DuoSeat;

public sealed record DeviceInfo(string Path, bool IsKeyboard)
{
    public string TypeLabel => IsKeyboard ? "Keyboard" : "Mouse";

    public string Friendly => FriendlyName(Path);

    public static string FriendlyName(string path)
    {
        var m = Regex.Match(path, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        var parts = path.Split('#');
        string instance = parts.Length >= 3 ? parts[2] : "";
        if (m.Success)
            return $"USB {m.Groups[1].Value}:{m.Groups[2].Value}  [{instance}]";
        if (path.Contains("ACPI", StringComparison.OrdinalIgnoreCase) || path.Contains("PNP0", StringComparison.OrdinalIgnoreCase))
            return $"Built-in  [{instance}]";
        return path.Length > 60 ? path[..60] + "…" : path;
    }
}

internal static class DeviceList
{
    public static string GetDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, null, ref size);
        if (size == 0) return string.Empty;
        var sb = new StringBuilder((int)size + 1);
        uint r = Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, sb, ref size);
        return r == uint.MaxValue ? string.Empty : sb.ToString();
    }

    public static List<DeviceInfo> Enumerate()
    {
        var result = new List<DeviceInfo>();
        uint count = 0;
        uint stride = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        if (Native.GetRawInputDeviceList(null, ref count, stride) != 0 || count == 0)
            return result;

        var list = new Native.RAWINPUTDEVICELIST[count];
        uint got = Native.GetRawInputDeviceList(list, ref count, stride);
        if (got == uint.MaxValue) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < got; i++)
        {
            var d = list[i];
            if (d.dwType != Native.RIM_TYPEKEYBOARD && d.dwType != Native.RIM_TYPEMOUSE) continue;
            string name = GetDeviceName(d.hDevice);
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            result.Add(new DeviceInfo(name, d.dwType == Native.RIM_TYPEKEYBOARD));
        }
        return result.OrderBy(x => x.IsKeyboard ? 0 : 1).ThenBy(x => x.Path).ToList();
    }
}
