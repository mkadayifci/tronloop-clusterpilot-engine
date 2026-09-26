using System.Buffers.Binary;
using Tronloop.ClusterPilot.Engine.Models;
namespace Tronloop.ClusterPilot.Engine;

public enum PayloadType : byte
{
    FastTelemetry = 0x01,
    Heartbeat = 0x02,
    VertexStatus = 0x03
}

public enum TlpCommand : byte
{
    RtcSet = 0x01
}

public static class CanPackage
{
    private const byte TlpVersion = 0x01;

    public static byte[] CreateRtcSet(DateTimeOffset timestamp, byte sequence)
    {
        // TlpHeader: command, version, sequence, flags; payload: uint32 Unix seconds.
        byte[] packet = [(byte)TlpCommand.RtcSet, TlpVersion, sequence, 0x00, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4),
            checked((uint)timestamp.ToUnixTimeSeconds()));
        return packet;
    }

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
                if (payload.Length == VertexTelemetryPayload.WireSize)
                    value = VertexTelemetryPayload.Parse(payload);
                else
                    error = $"VertexTelemetry requires {VertexTelemetryPayload.WireSize} bytes; received {payload.Length}.";
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
