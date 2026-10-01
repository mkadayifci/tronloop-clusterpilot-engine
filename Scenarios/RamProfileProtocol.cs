using System.Buffers.Binary;

namespace Tronloop.ClusterPilot.Engine.Scenarios;

public sealed record RamProfileQuery(Guid RequestId, string ClusterPilotId, string VertexId, DateTimeOffset ExpiresAt);
public sealed record RamProfileResult(Guid RequestId, string ClusterPilotId, string VertexId,
    string State, Guid? UploadCommandId, uint? Crc32, int? StepCount, int? EntryStep, DateTimeOffset ObservedAtUtc);
public sealed record RamProfileResponse(Guid RequestId, bool HasProfile, Guid UploadCommandId, uint Crc32, byte StepCount, byte EntryStep);

public static class RamProfileProtocol
{
    public const byte QueryCommand = 0x13;
    public const byte ResponseType = 0x05;

    public static byte[] QueryPacket(Guid requestId)
    {
        var packet = new byte[20];
        packet[0] = QueryCommand;
        packet[1] = 1;
        Convert.FromHexString(requestId.ToString("N")).CopyTo(packet, 4);
        return packet;
    }

    public static bool TryResponse(ReadOnlySpan<byte> packet, out RamProfileResponse? response)
    {
        response = null;
        if (packet.Length != 42 || packet[0] != ResponseType || packet[1] != 1 || packet[2] > 1 || packet[41] != 0)
            return false;
        var requestId = Guid.ParseExact(Convert.ToHexString(packet.Slice(4, 16)), "N");
        var uploadId = Guid.ParseExact(Convert.ToHexString(packet.Slice(20, 16)), "N");
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(packet[36..]);
        if (requestId == Guid.Empty) return false;
        if (packet[2] == 1 && (uploadId == Guid.Empty || packet[3] is 0 or > 64 || packet[40] >= packet[3])) return false;
        if (packet[2] == 0 && (uploadId != Guid.Empty || crc != 0 || packet[3] != 0 || packet[40] != 0)) return false;
        response = new(requestId, packet[2] == 1, uploadId, crc, packet[3], packet[40]);
        return true;
    }
}
