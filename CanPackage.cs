using Tronloop.ClusterPilot.Engine.Models;
namespace Tronloop.ClusterPilot.Engine;

public enum PayloadType : byte
{
    FastTelemetry = 0x01,
    Heartbeat = 0x02,
    VertexStatus = 0x03
}

public static class CanPackage
{
    public static bool TryParse(ReadOnlySpan<byte> payload, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (payload.IsEmpty)
        {
            error = "Missing payload_type byte.";
            return false;
        }

        switch ((PayloadType)payload[0])
        {
            case PayloadType.FastTelemetry:
                if (payload.Length == FastTelemetryPayload.WireSize)
                    value = FastTelemetryPayload.Parse(payload);
                else
                    error = $"FastTelemetry requires {FastTelemetryPayload.WireSize} bytes; received {payload.Length}.";
                break;
            case PayloadType.VertexStatus:
                if (payload.Length == VertexStatusPayload.WireSize)
                    value = VertexStatusPayload.Parse(payload);
                else
                    error = $"VertexStatus requires {VertexStatusPayload.WireSize} bytes; received {payload.Length}.";
                break;
            case PayloadType.Heartbeat:
                error = "Heartbeat (0x02) recognized, but its wire layout is not configured.";
                break;
            default:
                error = $"Unknown payload_type 0x{payload[0]:X2}.";
                break;
        }
        return value is not null;
    }
}
