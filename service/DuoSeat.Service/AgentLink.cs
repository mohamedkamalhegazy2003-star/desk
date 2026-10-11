namespace DuoSeat.Service;

/// <summary>Transport between the service and an agent running in the station-2 environment.</summary>
internal interface IAgentLink : IDisposable
{
    bool Connected { get; }
    event Action<bool>? ConnectionChanged;
    void Start(CancellationToken ct);
    /// <summary>Returns false (and drops the event) when no agent is connected.</summary>
    bool Send(DuoEvent e);
}
