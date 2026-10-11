using System.Runtime.InteropServices;

namespace DuoSeat.Service;

internal static class Program
{
    private const string Usage = @"DuoSeat.Service - user-mode half of the DuoSeat driver-based multiseat (run as Administrator)

  DuoSeat.Service list                         Show input devices known to the driver
  DuoSeat.Service monitors                     Show monitors and their indexes
  DuoSeat.Service identify                     Press keys/move mice to see which device is which (Ctrl+C to stop)
  DuoSeat.Service assign <id|instanceId> 2|1   Put a device on station 2 (or back on station 1)
  DuoSeat.Service set monitor1|monitor2|speed|lease <value>
  DuoSeat.Service run [--legacy]               Start routing with the session-2 agent (Ctrl+C or Ctrl+Alt+F12 stops it)
                                               --legacy: no agent, inject into the RDP window (single shared cursor)
  DuoSeat.Service run --vm                     Station 2 is a Hyper-V (or other) virtual machine: agent connects over TCP
  DuoSeat.Service agent-cmdline                Print the command line / reg command to autostart the agent INSIDE the VM
  DuoSeat.Service install-agent [path]         Make DuoSeat.Agent.exe start at logon of the station-2 user
  DuoSeat.Service uninstall-agent              Remove that autostart entry
  DuoSeat.Service set user <windowsUser>       Station-2 Windows user (default DuoSeat2)

Config: %ProgramData%\DuoSeat\service.json";

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));   // PerMonitorV2: real pixel coordinates

        if (args.Length == 0) { Console.WriteLine(Usage); return 1; }

        try
        {
            var cfg = ServiceConfig.Load();
            switch (args[0].ToLowerInvariant())
            {
                case "monitors": return Monitors(cfg);
                case "set": return Set(cfg, args);
                case "list":
                    using (var drv = DriverClient.Open()) return List(drv, cfg);
                case "assign":
                    using (var drv = DriverClient.Open()) return Assign(drv, cfg, args);
                case "identify":
                    using (var drv = DriverClient.Open()) return Identify(drv);
                case "run":
                    using (var drv = DriverClient.Open()) return Run(drv, cfg, args.Contains("--legacy"), args.Contains("--vm"));
                case "agent-cmdline": return AgentCmdLine(cfg);
                case "install-agent": return InstallAgent(cfg, args);
                case "uninstall-agent": return UninstallAgent();
                default:
                    Console.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 2;
        }
    }

    private static int Monitors(ServiceConfig cfg)
    {
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string role = i == cfg.Monitor1 ? "station 1" : i == cfg.Monitor2 ? "station 2" : "";
            Console.WriteLine($"[{i}] {s.DeviceName}  {s.Bounds.Width}x{s.Bounds.Height} at ({s.Bounds.X},{s.Bounds.Y}){(s.Primary ? "  primary" : "")}  {role}");
        }
        return 0;
    }

    private static int Set(ServiceConfig cfg, string[] args)
    {
        if (args.Length < 3) { Console.WriteLine(Usage); return 1; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        switch (args[1].ToLowerInvariant())
        {
            case "monitor1": cfg.Monitor1 = int.Parse(args[2], inv); break;
            case "monitor2": cfg.Monitor2 = int.Parse(args[2], inv); break;
            case "speed": cfg.MouseSpeed = double.Parse(args[2], inv); break;
            case "lease": cfg.FocusLeaseMs = int.Parse(args[2], inv); break;
            case "user": cfg.Station2User = args[2]; break;
            default: Console.WriteLine(Usage); return 1;
        }
        cfg.Save();
        Console.WriteLine("Saved to " + ServiceConfig.FilePath);
        return 0;
    }

    private static int List(DriverClient drv, ServiceConfig cfg)
    {
        var devices = drv.GetDevices();
        if (devices.Count == 0) Console.WriteLine("No devices yet. Did you enable the filter and reboot?");
        foreach (var d in devices)
        {
            string seat = cfg.IsStation2(d.InstanceId) ? "station 2" : "station 1";
            Console.WriteLine($"#{d.Id,-3} {d.TypeName,-9} {seat}  {d.InstanceId}");
        }
        return 0;
    }

    private static int Assign(DriverClient drv, ServiceConfig cfg, string[] args)
    {
        if (args.Length < 3 || (args[2] != "1" && args[2] != "2")) { Console.WriteLine(Usage); return 1; }

        string instance = args[1];
        if (uint.TryParse(args[1], out uint id))
        {
            var dev = drv.GetDevices().FirstOrDefault(d => d.Id == id);
            if (dev == null) { Console.Error.WriteLine("No device with id " + id); return 1; }
            instance = dev.InstanceId;
        }
        if (string.IsNullOrWhiteSpace(instance)) { Console.Error.WriteLine("This device has no instance id; cannot save it."); return 1; }

        cfg.Station2Devices.RemoveAll(x => string.Equals(x, instance, StringComparison.OrdinalIgnoreCase));
        if (args[2] == "2") cfg.Station2Devices.Add(instance);
        cfg.Save();
        Console.WriteLine($"{instance} -> station {args[2]} (applied when 'run' starts)");
        return 0;
    }

    private static int Identify(DriverClient drv)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var names = drv.GetDevices().ToDictionary(d => d.Id);
        foreach (var d in names.Values) drv.SetStation(d.Id, DriverClient.StationMonitor);
        drv.SetRouting(true);
        Console.WriteLine("Identify mode: use each keyboard/mouse; its id is printed. Input still works normally. Ctrl+C to stop.");

        try
        {
            var buf = new DuoEvent[64];
            uint last = 0;
            while (!cts.IsCancellationRequested)
            {
                int n = drv.ReadEvents(buf, cts.Token);
                for (int i = 0; i < n; i++)
                {
                    if (buf[i].DeviceId == last) continue;
                    last = buf[i].DeviceId;
                    names = drv.GetDevices().ToDictionary(d => d.Id);
                    Console.WriteLine(names.TryGetValue(last, out var d)
                        ? $"#{d.Id}  {d.TypeName}  {d.InstanceId}"
                        : $"#{last}  (device not listed)");
                }
            }
        }
        finally
        {
            try { drv.SetRouting(false); } catch { }
            try { foreach (var d in drv.GetDevices()) drv.SetStation(d.Id, DriverClient.StationNormal); } catch { }
        }
        return 0;
    }

    private static int Run(DriverClient drv, ServiceConfig cfg, bool legacy, bool vm)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        IAgentLink? agent = null;
        if (vm)
        {
            var addr = AgentTcp.FindHostAddress(cfg);
            if (addr == null) { Console.Error.WriteLine("No Hyper-V virtual switch address found. Set one: edit VmListenAddress in " + ServiceConfig.FilePath); return 1; }
            agent = new AgentTcp(addr, cfg.TcpPort, cfg.EnsureToken());
            agent.ConnectionChanged += c => Console.WriteLine(c ? "VM agent connected." : "VM agent disconnected - station-2 input is dropped until it reconnects.");
            Console.WriteLine($"VM mode: listening on {addr}:{cfg.TcpPort} for DuoSeat.Agent.exe inside the VM.");
            Console.WriteLine($"The station-1 mouse is confined to monitor {cfg.Monitor1} while running.");
        }
        else if (!legacy)
        {
            agent = new AgentPipe(cfg.Station2User);
            agent.ConnectionChanged += c => Console.WriteLine(c ? "Agent connected." : "Agent disconnected - station-2 input is dropped until it reconnects.");
            Console.WriteLine($"Agent mode: waiting for DuoSeat.Agent.exe running as '{cfg.Station2User}' in the station-2 session.");
            Console.WriteLine($"The station-1 mouse is confined to monitor {cfg.Monitor1} while running.");
        }
        else
        {
            Console.WriteLine($"Legacy mode: injecting into the RDP window on monitor {cfg.Monitor2} (one shared cursor).");
        }
        Console.WriteLine($"{cfg.Station2Devices.Count} device(s) on station 2. Stop with Ctrl+C or Ctrl+Alt+F12.");

        try { new Router(drv, cfg, agent).Run(cts); }
        finally { agent?.Dispose(); }
        Console.WriteLine("Stopped. All devices are back on normal Windows input.");
        return 0;
    }

    private static int AgentCmdLine(ServiceConfig cfg)
    {
        var addr = AgentTcp.FindHostAddress(cfg);
        if (addr == null) { Console.Error.WriteLine("No Hyper-V virtual switch address found (is Hyper-V enabled and a switch up?)."); return 1; }
        string args = $"--host {addr} --port {cfg.TcpPort} --token {cfg.EnsureToken()}";
        Console.WriteLine("Copy DuoSeat.Agent.exe into the VM (e.g. C:\\DuoSeat) and run it there with:");
        Console.WriteLine("  DuoSeat.Agent.exe " + args);
        Console.WriteLine();
        Console.WriteLine("To start it at every logon inside the VM (run in an elevated prompt in the VM):");
        Console.WriteLine($"  reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run /v DuoSeatAgent /t REG_SZ /d \"\\\"C:\\DuoSeat\\DuoSeat.Agent.exe\\\" {args}\" /f");
        Console.WriteLine();
        Console.WriteLine("The token is a shared secret; do not publish it. It is stored in " + ServiceConfig.FilePath);
        return 0;
    }

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static int InstallAgent(ServiceConfig cfg, string[] args)
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string path = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(dir, "DuoSeat.Agent.exe");
        if (!File.Exists(path)) { Console.Error.WriteLine("Agent not found: " + path); return 1; }

        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(RunKey);
        key.SetValue("DuoSeatAgent", $"\"{path}\" --only-user {cfg.Station2User}");
        Console.WriteLine($"Agent will start at logon of '{cfg.Station2User}': {path}");
        Console.WriteLine("(It exits immediately for any other user.) Log that user in through the RDP session to start it.");
        return 0;
    }

    private static int UninstallAgent()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue("DuoSeatAgent", throwOnMissingValue: false);
        Console.WriteLine("Agent autostart removed.");
        return 0;
    }
}
