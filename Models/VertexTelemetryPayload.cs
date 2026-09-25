using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Tronloop.ClusterPilot.Engine.Models;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct VertexTelemetryPayload
{
    public const int WireSize = 13;
    public readonly byte PayloadType;
    public readonly ushort BatteryVoltageMv;
    public readonly short BatteryCurrentMa;
    // Unix milliseconds supplied by the Vertex RTC; never replaced with receive time.
    public readonly ulong MeasurementTimeMs;

    private VertexTelemetryPayload(ReadOnlySpan<byte> payload)
    {
        PayloadType = payload[0];
        BatteryVoltageMv = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..3]);
        BatteryCurrentMa = BinaryPrimitives.ReadInt16LittleEndian(payload[3..5]);
        MeasurementTimeMs = BinaryPrimitives.ReadUInt64LittleEndian(payload[5..13]);
    }

    public static VertexTelemetryPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != WireSize || payload[0] != (byte)Tronloop.ClusterPilot.Engine.PayloadType.FastTelemetry)
            throw new ArgumentException("Expected a 13-byte VertexTelemetry payload with type 0x01.", nameof(payload));
        return new VertexTelemetryPayload(payload);
    }
}
