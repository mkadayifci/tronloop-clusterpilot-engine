using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tronloop.ClusterPilot.Engine.Scenarios;

public sealed record UploadCommand(Guid CommandId, string ClusterPilotId, string VertexId, Guid ProfileId,
    int ProfileVersion, int SchemaVersion, string ContentHash, JsonElement Steps, DateTimeOffset ExpiresAt);
public sealed record UploadResult(Guid CommandId, string ClusterPilotId, string VertexId, string State, string Message,
    string ContentHash, DateTimeOffset TimestampUtc);
public sealed record CompiledScenario(byte[] Records, byte Entry, byte Count)
{
    public uint Crc => ScenarioProtocol.Crc32(Records);
}
public static class ScenarioProtocol
{
    public const int MaxSteps = 64;
    public const byte Upload = 0x12, AckType = 0x04;
    public static byte[] Packet(Guid commandId, CompiledScenario program)
    {
        var bytes = new byte[28 + program.Records.Length];
        bytes[0] = Upload; bytes[1] = 1;
        Convert.FromHexString(commandId.ToString("N")).CopyTo(bytes, 4);
        bytes[20] = 1; bytes[21] = program.Count; bytes[22] = program.Entry;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), program.Crc);
        program.Records.CopyTo(bytes, 28);
        return bytes;
    }
    public static bool TryAck(ReadOnlySpan<byte> bytes, out Guid id, out byte result, out uint crc)
    {
        id = default; result = 255; crc = 0;
        if (bytes.Length != 24 || bytes[0] != AckType || bytes[1] != 1 || bytes[3] != 0) return false;
        id = Guid.ParseExact(Convert.ToHexString(bytes.Slice(4,16)), "N"); result = bytes[2];
        crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]); return true;
    }
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte b in bytes) { crc ^= b; for(int i=0;i<8;i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        return ~crc;
    }
    public static string Hash(JsonElement steps)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(steps, writer);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
    static void Write(JsonElement e, Utf8JsonWriter w)
    {
        if(e.ValueKind == JsonValueKind.Object) { w.WriteStartObject(); foreach(var p in e.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)) {w.WritePropertyName(p.Name);Write(p.Value,w);} w.WriteEndObject(); }
        else if(e.ValueKind == JsonValueKind.Array) {w.WriteStartArray();foreach(var v in e.EnumerateArray())Write(v,w);w.WriteEndArray();}
        else if(e.ValueKind == JsonValueKind.Number) w.WriteRawValue(e.GetDecimal().ToString("G29",System.Globalization.CultureInfo.InvariantCulture));
        else e.WriteTo(w);
    }
    public static CompiledScenario Compile(JsonElement steps, int schema)
    {
        if(schema is not (2 or 3)) throw new ArgumentException("Only profile schemas 2 and 3 are supported.");
        List<byte[]> records = []; HashSet<string> ids = []; int total=0;
        byte Add(byte action) { if(records.Count>=MaxSteps)throw new ArgumentException("Compiled scenario exceeds 64 steps (including STOP)."); records.Add(new byte[16]); records[^1][0]=action;return (byte)(records.Count-1); }
        var stop=Add(3);
        byte Walk(JsonElement list, byte next, int depth)
        {
            if(depth>12 || list.ValueKind!=JsonValueKind.Array)throw new ArgumentException("Invalid step array/depth.");
            foreach(var step in list.EnumerateArray().Reverse())
            {
                if(++total>512 || !ids.Add(step.GetProperty("id").GetString() ?? "") || string.IsNullOrEmpty(step.GetProperty("id").GetString()))throw new ArgumentException("Invalid step IDs/count.");
                var kind=step.GetProperty("kind").GetString();
                if(kind is "charge" or "discharge")
                {
                    if(kind=="discharge" && schema<3)throw new ArgumentException("Discharge requires schema 3.");
                    if(step.EnumerateObject().Count()!=3 || step.TryGetProperty("mah",out _))throw new ArgumentException("Invalid current step fields.");
                    int current=step.GetProperty("targetCurrentMa").GetInt32();if(current<=0)throw new ArgumentException("Current must be positive mA.");
                    var idx=Add(kind=="charge"?(byte)1:(byte)2);records[idx][1]=next;
                    BinaryPrimitives.WriteInt32LittleEndian(records[idx].AsSpan(8),current);next=idx;
                }
                else if(kind is "while" or "if")
                {
                    var condition=step.GetProperty("condition");var metric=condition.GetProperty("metric").GetString();
                    byte variable=metric switch {"voltage"=>0,"current"=>1,"temperature"=>2,"elapsedTime"=>3,"capacity"=>4,"always"=>5,_=>throw new ArgumentException("Unsupported condition.")};
                    if(kind=="if" && variable is 3 or 5)throw new ArgumentException("Elapsed/always require While.");
                    byte op=condition.GetProperty("operator").GetString() switch {"=="=>0,"<"=>2,">"=>3,"<="=>4,">="=>5,_=>throw new ArgumentException("Unsupported comparison.")};
                    decimal value=condition.GetProperty("value").GetDecimal();
                    if(variable==3 && value<0)throw new ArgumentException("Negative duration.");
                    decimal scaled=variable==5?0:value*(variable is 0 or 1 or 3?1000:variable==2?10:1);
                    if(decimal.Truncate(scaled)!=scaled || scaled<int.MinValue || scaled>int.MaxValue)throw new ArgumentException("Condition cannot be represented exactly in device units.");
                    byte idx=Add(kind=="while"?(byte)5:(byte)6);var rec=records[idx];rec[4]=variable;rec[5]=op;
                    BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(8),(int)scaled);
                    if(kind=="while") {var body=step.GetProperty("body");if(body.GetArrayLength()==0)throw new ArgumentException("Empty While.");rec[2]=Walk(body,idx,depth+1);rec[3]=next;}
                    else {rec[2]=Walk(step.GetProperty("then"),next,depth+1);rec[3]=Walk(step.GetProperty("else"),next,depth+1);}
                    next=idx;
                }
                else throw new ArgumentException("Unsupported step kind.");
            }
            return next;
        }
        if(steps.GetArrayLength()==0)throw new ArgumentException("Empty scenario.");
        byte entry=Walk(steps,stop,0);
        return new(records.SelectMany(x=>x).ToArray(),entry,(byte)records.Count);
    }
}
