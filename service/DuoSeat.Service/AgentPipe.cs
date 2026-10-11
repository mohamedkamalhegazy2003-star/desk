using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DuoSeat.Service;

/// <summary>Named pipe the station-2 agent connects to. The service sends captured events through it.</summary>
internal sealed class AgentPipe : IAgentLink
{
    public const string PipeName = "DuoSeatAgent";

    private readonly string _allowedUser;
    private readonly object _writeLock = new();
    private NamedPipeServerStream? _pipe;
    private volatile bool _connected;

    public bool Connected => _connected;
    public event Action<bool>? ConnectionChanged;

    public AgentPipe(string allowedUser) => _allowedUser = allowedUser;

    public void Start(CancellationToken ct) => Task.Run(() => AcceptLoop(ct));

    private void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
                    PipeAccessRights.FullControl, AccessControlType.Allow));

                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                using var reg = ct.Register(() => { try { pipe.Dispose(); } catch { } });

                pipe.WaitForConnection();

                // Only the configured station-2 user may receive the keystrokes.
                if (pipe.ReadByte() < 0) continue;
                string user = pipe.GetImpersonationUserName();
                if (!user.EndsWith("\\" + _allowedUser, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(user, _allowedUser, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"Rejected agent connection from '{user}' (expected '{_allowedUser}').");
                    continue;
                }

                _pipe = pipe;
                _connected = true;
                ConnectionChanged?.Invoke(true);

                // The agent never sends more data; a read returning 0/throwing means it went away.
                var one = new byte[1];
                while (pipe.Read(one, 0, 1) > 0) { }
            }
            catch (Exception) when (ct.IsCancellationRequested) { }
            catch (Exception)
            {
                // broken pipe etc.: loop and wait for the next agent
            }
            finally
            {
                bool was = _connected;
                _connected = false;
                _pipe = null;
                try { pipe?.Dispose(); } catch { }
                if (was) ConnectionChanged?.Invoke(false);
            }
        }
    }

    /// <summary>Sends one event; returns false if no agent is connected.</summary>
    public bool Send(DuoEvent e)
    {
        var p = _pipe;
        if (p == null || !_connected) return false;
        try
        {
            lock (_writeLock)
            {
                var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref e, 1));
                p.Write(bytes);
            }
            return true;
        }
        catch (Exception)
        {
            _connected = false;
            try { p.Dispose(); } catch { }
            return false;
        }
    }

    public void Dispose()
    {
        _connected = false;
        try { _pipe?.Dispose(); } catch { }
    }
}
