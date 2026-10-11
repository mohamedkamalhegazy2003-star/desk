using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DuoSeat;

internal static class SystemTools
{
    public static (int ExitCode, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode, o.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Human-readable summary of whether this Windows can host a second interactive session.</summary>
    public static (bool Ok, string Text) CheckSystem()
    {
        string product = "Windows";
        string edition = "";
        string type = "";
        try
        {
            using var v = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            product = v?.GetValue("ProductName") as string ?? product;
            edition = v?.GetValue("EditionID") as string ?? "";
            using var o = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            type = o?.GetValue("ProductType") as string ?? "";
        }
        catch { /* ignore */ }

        if (edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
            return (false, $"{product}: نسخة Home لا تدعم استقبال اتصالات RDP، فلن تعمل المحطة 2 عليها.");

        bool client = type.Equals("WinNT", StringComparison.OrdinalIgnoreCase);
        if (client)
            return (false, $"{product}: نسخة عميل (Client). ويندوز العميل يسمح بجلسة تفاعلية واحدة في نفس الوقت، " +
                           "فاتصال المحطة 2 سيفصل المستخدم الحالي. المطلوب Windows Server مع دور Remote Desktop Services.");

        return (true, $"{product}: تدعم عدة جلسات متزامنة (تأكد من تثبيت دور Remote Desktop Services وترخيصه).");
    }

    /// <summary>Enables RDP, opens loopback firewall rule, creates the station-2 local user. Returns a log.</summary>
    public static string PrepareStation2(string user, string password)
    {
        var log = new StringBuilder();

        try
        {
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server",
                "fDenyTSConnections", 0, RegistryValueKind.DWord);
            log.AppendLine("تم تفعيل Remote Desktop.");
        }
        catch (Exception ex)
        {
            log.AppendLine("تعذر تفعيل RDP: " + ex.Message);
        }

        var fw = Run("netsh", "advfirewall", "firewall", "add", "rule",
            "name=DuoSeat RDP loopback", "dir=in", "action=allow", "protocol=TCP",
            "localport=3389", "remoteip=127.0.0.0/8");
        log.AppendLine(fw.ExitCode == 0 ? "تمت إضافة قاعدة جدار الحماية (loopback فقط)." : "جدار الحماية: " + fw.Output);

        var add = Run("net", "user", user, password, "/add");
        log.AppendLine(add.ExitCode == 0 ? $"تم إنشاء المستخدم {user}." : $"إنشاء المستخدم: {add.Output}");

        string group = "Remote Desktop Users";
        try
        {
            var acct = (NTAccount)new SecurityIdentifier("S-1-5-32-555").Translate(typeof(NTAccount));
            group = acct.Value.Split('\\').Last();
        }
        catch { /* keep English default */ }

        var grp = Run("net", "localgroup", group, user, "/add");
        log.AppendLine(grp.ExitCode == 0 ? $"تمت إضافة {user} إلى مجموعة {group}." : $"المجموعة: {grp.Output}");

        return log.ToString().Trim();
    }

    /// <summary>Stores credentials for the loopback connection and starts mstsc fullscreen on the given monitor.</summary>
    public static void ConnectStation2(Config cfg, string password, System.Drawing.Rectangle bounds)
    {
        string target = "TERMSRV/" + cfg.Station2Address;
        string account = Environment.MachineName + "\\" + cfg.Station2User;

        var key = Run("cmdkey", "/generic:" + target, "/user:" + account, "/pass:" + password);
        if (key.ExitCode != 0)
            throw new InvalidOperationException("cmdkey: " + key.Output);

        var rdp = new StringBuilder();
        rdp.AppendLine("full address:s:" + cfg.Station2Address);
        rdp.AppendLine("username:s:" + account);
        rdp.AppendLine("screen mode id:i:2");
        rdp.AppendLine("use multimon:i:0");
        rdp.AppendLine($"desktopwidth:i:{bounds.Width}");
        rdp.AppendLine($"desktopheight:i:{bounds.Height}");
        rdp.AppendLine("session bpp:i:32");
        rdp.AppendLine($"winposstr:s:0,1,{bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}");
        rdp.AppendLine("authentication level:i:0");
        rdp.AppendLine("prompt for credentials:i:0");
        rdp.AppendLine("enablecredsspsupport:i:1");
        rdp.AppendLine("redirectclipboard:i:0");

        string file = Path.Combine(Path.GetTempPath(), "duoseat-station2.rdp");
        File.WriteAllText(file, rdp.ToString());

        var psi = new ProcessStartInfo("mstsc.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(file);
        Process.Start(psi);
    }

    public static void ForgetCredentials(Config cfg)
    {
        Run("cmdkey", "/delete:TERMSRV/" + cfg.Station2Address);
    }
}
