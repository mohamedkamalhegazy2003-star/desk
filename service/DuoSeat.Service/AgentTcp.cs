using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DuoSeat.Service;

/// <summary>
/// TCP transport for an agent running inside a virtual machine (Hyper-V "Default Switch" etc.),
/// where a named pipe cannot reach. The agent must send the shared token as its first line.
/// The listener binds to the host's virtual-switch address only (never to your LAN).
/// </summary>
internal sealed class AgentTcp : IAgentLink
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly byte[] _token;
    private readonly object _writeLock = new();
    private TcpListener? _listener;
    private volatile NetworkStream? _stream;
    private volatile bool _connected;

    public bool Connected => _connected;
    public event Action<bool>? ConnectionChanged;

    public AgentTcp(IPAddress address, int port, string token)
    {
        _address = address;
        _port = port;
        _token = Encoding.UTF8.GetBytes(token);
    }

    /// <summary>First IPv4 address of the Hyper-V virtual switch adapter ("Default Switch" preferred), or the configured one.</summary>
    public static IPAddress? FindHostAddress(ServiceConfig cfg)
    {
        if (IPAddress.TryParse(cfg.VmListenAddress, out var configured)) return configured;

        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Name.Contains("Default Switch", StringComparison.OrdinalIgnoreCase));

        foreach (var n in candidates)
        {
            var ip = n.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (ip != null) return ip.Address;
        }
        return null;
    }

    public void Start(CancellationToken ct)
    {
        _listener = new TcpListener(_address, _port);
        _listener.Start();
        ct.Register(() => { try { _listener.Stop(); } catch { } });
        Task.Run(() => AcceptLoop(ct));
    }

    private void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = _listener!.AcceptTcpClient();
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var stream = client.GetStream();
                stream.ReadTimeout = 3000;
                if (!Authenticate(stream))
                {
                    Console.Error.WriteLine("Rejected a connection to the agent port (wrong token).");
                    continue;                                   // finally disposes the client
                }
                stream.ReadTimeout = Timeout.Infinite;

                _stream = stream;
                _connected = true;
                ConnectionChanged?.Invoke(true);

                var one = new byte[1];
                while (stream.Read(one, 0, 1) > 0) { }          // agent sends nothing more; 0/throw = it left
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* broken connection: wait for the next agent */ }
            finally
            {
                bool was = _connected;
                _connected = false;
                _stream = null;
                try { client?.Dispose(); } catch { }
                if (was) ConnectionChanged?.Invoke(false);
            }
        }
    }

    private bool Authenticate(NetworkStream s)
    {
        var buf = new byte[128];
        int n = 0;
        while (n < buf.Length)
        {
            int b = s.ReadByte();
            if (b < 0) return false;
            if (b == '\n') break;
            buf[n++] = (byte)b;
        }
        return CryptographicOperations.FixedTimeEquals(buf.AsSpan(0, n), _token);
    }

    public bool Send(DuoEvent e)
    {
        var s = _stream;
        if (s == null || !_connected) return false;
        try
        {
            lock (_writeLock)
            {
                var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref e, 1));
                s.Write(bytes);
            }
            return true;
        }
        catch (Exception)
        {
            _connected = false;
            try { s.Dispose(); } catch { }
            return false;
        }
    }

    public void Dispose()
    {
        _connected = false;
        try { _listener?.Stop(); } catch { }
        try { _stream?.Dispose(); } catch { }
    }
}
