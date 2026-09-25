namespace Tronloop.ClusterPilot.Engine;

public sealed record CanDeviceSnapshot(
    string VertexId, bool IsInstalled, string ListenerState, string ReceptionState,
    DateTimeOffset? LastReceivedAtUtc, long ReceivedPackets, string? LastError);

public sealed class CanDeviceStatus(string vertexId, bool isInstalled)
{
    private readonly object _sync = new();
    private string _state = isInstalled ? "starting" : "inactive";
    private DateTimeOffset? _lastReceived;
    private long _receivedPackets;
    private string? _lastError;

    public void SetState(string state, string? error = null)
    {
        lock (_sync)
        {
            _state = state;
            if (error is not null) _lastError = error;
        }
    }

    public void RecordReceived(DateTimeOffset receivedAt)
    {
        lock (_sync)
        {
            _lastReceived = receivedAt;
            _receivedPackets++;
        }
    }

    public CanDeviceSnapshot Snapshot(DateTimeOffset now, TimeSpan staleAfter)
    {
        lock (_sync)
        {
            var reception = !isInstalled ? "inactive"
                : _lastReceived is null ? "waiting"
                : now - _lastReceived.Value >= staleAfter ? "stale" : "recent";
            return new(vertexId, isInstalled, _state, reception, _lastReceived, _receivedPackets, _lastError);
        }
    }
}
