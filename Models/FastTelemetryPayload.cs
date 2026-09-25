using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Tronloop.ClusterPilot.Engine.Models;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct FastTelemetryPayload
{
    public const int WireSize = 8;
    public readonly byte PayloadType;
    public readonly ushort BatteryVoltageMv;
    public readonly short BatteryCurrentMa;
    public readonly short BatteryTempDeciC;
    public readonly byte State;

    private FastTelemetryPayload(ReadOnlySpan<byte> payload)
    {
        PayloadType = payload[0];
        BatteryVoltageMv = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..3]);
        BatteryCurrentMa = BinaryPrimitives.ReadInt16LittleEndian(payload[3..5]);
        BatteryTempDeciC = BinaryPrimitives.ReadInt16LittleEndian(payload[5..7]);
        State = payload[7];
    }

    public static FastTelemetryPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != WireSize || payload[0] != (byte)Tronloop.ClusterPilot.Engine.PayloadType.FastTelemetry)
            throw new ArgumentException("Expected an 8-byte FastTelemetry payload with type 0x01.", nameof(payload));
        return new FastTelemetryPayload(payload);
    }
}
