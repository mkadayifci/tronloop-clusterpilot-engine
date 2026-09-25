using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Tronloop.ClusterPilot.Engine.Models;

// STM32 packed wire layout. ChargerMode and BatteryVoltageMv are context values;
// they do not imply hardware mode readback or an implemented voltage measurement.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct VertexStatusPayload
{
    public const int WireSize = 11;

    public readonly byte PayloadType;
    public readonly ushort BatteryVoltageMv;
    public readonly byte ScenarioPlayerState;
    public readonly byte ChargerMode;
    public readonly short BatteryCurrentMa;
    public readonly short BatteryTemperatureDc;
    public readonly short AmbientTemperatureDc;

    private VertexStatusPayload(ReadOnlySpan<byte> payload)
    {
        PayloadType = payload[0];
        BatteryVoltageMv = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..3]);
        ScenarioPlayerState = payload[3];
        ChargerMode = payload[4];
        BatteryCurrentMa = BinaryPrimitives.ReadInt16LittleEndian(payload[5..7]);
        BatteryTemperatureDc = BinaryPrimitives.ReadInt16LittleEndian(payload[7..9]);
        AmbientTemperatureDc = BinaryPrimitives.ReadInt16LittleEndian(payload[9..11]);
    }

    public static VertexStatusPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != WireSize || payload[0] != (byte)Tronloop.ClusterPilot.Engine.PayloadType.VertexStatus)
            throw new ArgumentException("Expected an 11-byte VertexStatus payload with type 0x03.", nameof(payload));
        return new VertexStatusPayload(payload);
    }
}
