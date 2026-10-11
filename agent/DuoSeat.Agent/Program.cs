using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace DuoSeat.Agent;

internal static class Program
{
    private const string PipeName = "DuoSeatAgent";

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>
    /// Usage:
    ///   DuoSeat.Agent.exe [--only-user NAME]                       same machine (named pipe, RDP session)
    ///   DuoSeat.Agent.exe --host IP [--port 47600] --token SECRET  inside a virtual machine (TCP)
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        string? onlyUser = Arg(args, "--only-user");
        string? host = Arg(args, "--host");
        int port = int.TryParse(Arg(args, "--port"), out int p) ? p : 47600;
        string token = Arg(args, "--token") ?? "";

        // Registered under HKLM\...\Run so it starts for every user; exit at once for any other user.
        if (onlyUser != null && !string.Equals(Environment.UserName, onlyUser, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (host != null && token.Length == 0) return 1;

        using var single = new Mutex(true, @"Local\DuoSeatAgent", out bool first);
        if (!first) return 0;

        SetProcessDpiAwarenessContext(new IntPtr(-4));          // PerMonitorV2: real pixel coordinates
        ApplicationConfiguration.Initialize();

        var injector = new Injector();
        var overlay = new CursorOverlay(injector);
        overlay.Start();

        Func<Stream> connect = host != null ? () => ConnectTcp(host, port, token) : ConnectPipe;

        using var cts = new CancellationTokenSource();
        var worker = new Thread(() => LinkLoop(connect, injector, overlay, cts.Token)) { IsBackground = true, Name = "link" };
        worker.Start();

        Application.Run();                                      // message loop for the overlay window
        cts.Cancel();
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static Stream ConnectPipe()
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(2000);
            pipe.WriteByte(1);                                   // hello: lets the service identify our user
            pipe.Flush();
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private static Stream ConnectTcp(string host, int port, string token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            if (!client.ConnectAsync(host, port).Wait(2000)) throw new TimeoutException();
            var stream = new NetworkStream(client.Client, ownsSocket: true);
            var hello = Encoding.UTF8.GetBytes(token + "\n");
            stream.Write(hello, 0, hello.Length);
            stream.Flush();
            return stream;
        }
        catch { client.Dispose(); throw; }
    }

    private static void LinkLoop(Func<Stream> connect, Injector injector, CursorOverlay overlay, CancellationToken ct)
    {
        var frame = new byte[Marshal.SizeOf<DuoEvent>()];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var stream = connect();
                overlay.SetConnected(true);

                while (!ct.IsCancellationRequested)
                {
                    stream.ReadExactly(frame, 0, frame.Length);
                    injector.Handle(MemoryMarshal.Read<DuoEvent>(frame));
                }
            }
            catch (Exception)
            {
                // service not running, rejected us, or it closed the connection: retry
            }
            finally
            {
                injector.ReleaseAll();
                overlay.SetConnected(false);
            }
            ct.WaitHandle.WaitOne(1000);
        }
    }
}
