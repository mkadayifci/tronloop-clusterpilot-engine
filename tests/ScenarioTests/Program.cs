using System.Buffers.Binary;
using System.Text.Json;
using Tronloop.ClusterPilot.Engine.Scenarios;
int checks=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
CompiledScenario Compile(string json,int schema=3){using var d=JsonDocument.Parse(json);return ScenarioProtocol.Compile(d.RootElement,schema);}
void Reject(string json,int schema=3){try{Compile(json,schema);}catch(Exception e)when(e is ArgumentException or InvalidOperationException or OverflowException or FormatException){checks++;return;}throw new Exception("Accepted invalid scenario");}
const string simple="""[{"id":"w","kind":"while","condition":{"metric":"elapsedTime","operator":"<","value":3},"body":[{"id":"c","kind":"charge","targetCurrentMa":500}]},{"id":"d","kind":"discharge","targetCurrentMa":250}]""";
var program=Compile(simple);
Check(program.Count==4 && program.Entry==2,"Four records including STOP");
Check(program.Records[2*16]==5 && program.Records[2*16+2]==3 && program.Records[2*16+3]==1,"While true/body and false/discharge edges");
Check(BinaryPrimitives.ReadInt32LittleEndian(program.Records.AsSpan(2*16+8))==3000,"Seconds to milliseconds");
Check(program.Records[3*16]==1 && program.Records[3*16+1]==2,"Charge loops back");
Check(program.Records[16]==2 && program.Records[17]==0,"Discharge then STOP");
Check(ScenarioProtocol.Crc32("123456789"u8)==0xcbf43926,"IEEE CRC32 vector");
var id=Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");var packet=ScenarioProtocol.Packet(id,program);
Check(packet.Length==92 && Convert.ToHexString(packet.AsSpan(4,16))=="00112233445566778899AABBCCDDEEFF","Packet UUID byte order and length");
var ack=new byte[24];ack[0]=4;ack[1]=1;packet.AsSpan(4,16).CopyTo(ack.AsSpan(4));BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(20),program.Crc);
Check(ScenarioProtocol.TryAck(ack,out var ackId,out var code,out var crc)&&ackId==id&&code==0&&crc==program.Crc,"ACK decoding");
Check(!ScenarioProtocol.TryAck(ack.AsSpan(0,23),out _,out _,out _),"Short ACK rejected");ack[1]=2;Check(!ScenarioProtocol.TryAck(ack,out _,out _,out _),"Wrong ACK version rejected");
foreach(var bad in new[]{"0","-1","1.5","2147483648"})Reject(simple.Replace("500",bad));
Reject(simple,2);Reject(simple,1);Reject("[]");Reject(simple.Replace("\"d\"","\"c\""));Reject(simple.Replace("\"body\":[{\"id\":\"c\",\"kind\":\"charge\",\"targetCurrentMa\":500}]","\"body\":[]"));
Reject(simple.Replace("\"value\":3","\"value\":0.0001"));
var many="["+string.Join(',',Enumerable.Range(0,63).Select(i=>$"{{\"id\":\"{i}\",\"kind\":\"charge\",\"targetCurrentMa\":500}}"))+"]";
Check(Compile(many).Count==64,"63 actions plus STOP fit");Reject(many.Replace("]",",{\"id\":\"extra\",\"kind\":\"charge\",\"targetCurrentMa\":500}]"));
Check(Compile(simple.Replace("elapsedTime","always")).Records[2*16+4]==5,"Always metric encoded");
var branch=Compile("""[{"id":"i","kind":"if","condition":{"metric":"voltage","operator":">=","value":3.7},"then":[{"id":"c","kind":"charge","targetCurrentMa":500}],"else":[{"id":"d","kind":"discharge","targetCurrentMa":200}]}]""");
Check(branch.Records[16]==6 && BinaryPrimitives.ReadInt32LittleEndian(branch.Records.AsSpan(24))==3700,"If and voltage scaling");
if(args.Length>0)File.WriteAllBytes(args[0],packet);
var maximumPacket = ScenarioProtocol.Packet(Guid.NewGuid(), Compile(many));
Check(maximumPacket.Length == 1052, "64-record packet is 1052 bytes");
if(args.Length>1)File.WriteAllBytes(args[1],maximumPacket);
var ramQuery = RamProfileProtocol.QueryPacket(id);
Check(ramQuery.Length == 20 && ramQuery[0] == 0x13 && Convert.ToHexString(ramQuery.AsSpan(4)) == "00112233445566778899AABBCCDDEEFF", "RAM query correlation UUID");
var ramResponse = new byte[42];
ramResponse[0] = 5; ramResponse[1] = 1;
ramQuery.AsSpan(4, 16).CopyTo(ramResponse.AsSpan(4));
Check(RamProfileProtocol.TryResponse(ramResponse, out var emptyRam) && emptyRam is { HasProfile: false }, "Empty RAM response");
ramResponse[2] = 1; ramResponse[3] = 64; ramResponse[40] = 63;
packet.AsSpan(4, 16).CopyTo(ramResponse.AsSpan(20));
BinaryPrimitives.WriteUInt32LittleEndian(ramResponse.AsSpan(36), program.Crc);
Check(RamProfileProtocol.TryResponse(ramResponse, out var loadedRam) && loadedRam is { HasProfile: true, StepCount: 64, EntryStep: 63 } && loadedRam.UploadCommandId == id && loadedRam.Crc32 == program.Crc, "Loaded RAM response");
Check(!RamProfileProtocol.TryResponse(ramResponse.AsSpan(0, 41), out _), "Truncated RAM response rejected");
ramResponse[40] = 64;
Check(!RamProfileProtocol.TryResponse(ramResponse, out _), "Invalid RAM entry rejected");
Console.WriteLine($"{checks} protocol/compiler checks passed.");

await UploaderChecks.Run();
