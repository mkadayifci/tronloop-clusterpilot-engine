using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using MQTTnet;
using MQTTnet.Protocol;

namespace Tronloop.ClusterPilot.Engine.Scenarios;

public interface IScenarioTransport
{
    event Action<byte[]>? ScenarioAck;
    void Send(byte[] data);
}

public sealed class ScenarioUploader : BackgroundService
{
    readonly IMqttClient client; readonly ILogger<ScenarioUploader> log; readonly string cluster; readonly string db;
    readonly object gate=new();
    readonly ConcurrentDictionary<string,IScenarioTransport> devices=new();
    readonly ConcurrentDictionary<Guid,(string Vertex,uint Crc,TaskCompletionSource<byte> Completion)> pending=new();
    readonly Channel<(UploadCommand Command,CompiledScenario Program)> queue=Channel.CreateBounded<(UploadCommand,CompiledScenario)>(16);
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public ScenarioUploader(IMqttClient client,IConfiguration config,IHostEnvironment env,ILogger<ScenarioUploader> log)
    {
        this.client=client;this.log=log;cluster=config["ClusterPilot:Id"] ?? throw new InvalidOperationException("ClusterPilot:Id missing");
        var path=Path.GetFullPath(config["Scenarios:DatabasePath"]??"data/scenario-uploads.sqlite",env.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);db=new SqliteConnectionStringBuilder{DataSource=path,DefaultTimeout=5}.ToString();
        using var c=Open();using var q=c.CreateCommand();q.CommandText="""
          PRAGMA journal_mode=WAL;
          CREATE TABLE IF NOT EXISTS uploads(id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, result TEXT NOT NULL, pending INTEGER NOT NULL);
          """;q.ExecuteNonQuery();
        using var read=c.CreateCommand();read.CommandText="SELECT id,result FROM uploads";
        List<UploadResult> interrupted=[];using(var reader=read.ExecuteReader())while(reader.Read()) {var result=JsonSerializer.Deserialize<UploadResult>(reader.GetString(1),Json)!;if(result.State is "received" or "sending")interrupted.Add(result);}
        foreach(var result in interrupted)Save(result with {State="failed",Message="ClusterPilot yeniden başladı; cihazdaki yükleme sonucu doğrulanamadı. Yeniden gönderin.",TimestampUtc=DateTimeOffset.UtcNow});
    }
    SqliteConnection Open(){var c=new SqliteConnection(db);c.Open();return c;}
    public void Attach(string vertex,IScenarioTransport listener)
    {
        devices[vertex]=listener;
        listener.ScenarioAck += bytes=> {if(ScenarioProtocol.TryAck(bytes,out var id,out var result,out var crc) && pending.TryGetValue(id,out var wait) && wait.Crc==crc && wait.Vertex==vertex)wait.Completion.TrySetResult(result);};
    }
    void Save(UploadResult result)
    {
        lock(gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="UPDATE uploads SET result=$r,pending=1 WHERE id=$id";q.Parameters.AddWithValue("$r",JsonSerializer.Serialize(result,Json));q.Parameters.AddWithValue("$id",result.CommandId.ToString());q.ExecuteNonQuery();}
    }
    public Task ReceiveAsync(string topic,byte[] payload,bool retained)
    {
        if(retained || payload.Length>65536)return Task.CompletedTask;
        try
        {
            var command=JsonSerializer.Deserialize<UploadCommand>(payload,Json) ?? throw new ArgumentException("Empty command");
            if(command.CommandId==Guid.Empty || topic!=$"tronloop/{cluster}/{command.VertexId}/scenario/upload" || command.ClusterPilotId!=cluster)return Task.CompletedTask;
            var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
            lock(gate)
            {
                using var c=Open();using var find=c.CreateCommand();find.CommandText="SELECT fingerprint FROM uploads WHERE id=$id";find.Parameters.AddWithValue("$id",command.CommandId.ToString());
                var previous=find.ExecuteScalar() as string;
                if(previous!=null){if(previous==fingerprint){using var replay=c.CreateCommand();replay.CommandText="UPDATE uploads SET pending=1 WHERE id=$id";replay.Parameters.AddWithValue("$id",command.CommandId.ToString());replay.ExecuteNonQuery();}return Task.CompletedTask;}
                var accepted=new UploadResult(command.CommandId,cluster,command.VertexId,"received","ClusterPilot profili aldı.",command.ContentHash,DateTimeOffset.UtcNow);
                using var insert=c.CreateCommand();insert.CommandText="INSERT INTO uploads VALUES($id,$f,$r,1)";insert.Parameters.AddWithValue("$id",command.CommandId.ToString());insert.Parameters.AddWithValue("$f",fingerprint);insert.Parameters.AddWithValue("$r",JsonSerializer.Serialize(accepted,Json));insert.ExecuteNonQuery();
            }
            var result=new UploadResult(command.CommandId,cluster,command.VertexId,"rejected","",command.ContentHash,DateTimeOffset.UtcNow);
            try
            {
                if(command.ExpiresAt<=DateTimeOffset.UtcNow || command.ExpiresAt>DateTimeOffset.UtcNow.AddMinutes(5))throw new ArgumentException("Gönderim süresi dolmuş/geçersiz.");
                if(!devices.ContainsKey(command.VertexId))throw new ArgumentException("Vertex kurulu değil veya CAN adresi tanımlı değil.");
                if(!string.Equals(ScenarioProtocol.Hash(command.Steps),command.ContentHash,StringComparison.Ordinal))throw new ArgumentException("Profil özeti uyuşmuyor.");
                var compiled=ScenarioProtocol.Compile(command.Steps,command.SchemaVersion);
                if(!queue.Writer.TryWrite((command,compiled)))throw new ArgumentException("Aktarım kuyruğu dolu; yeniden deneyin.");
            }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
            {Save(result with {Message=ex.Message});}
        }
        catch(Exception ex){log.LogWarning(ex,"Scenario command rejected before enqueue");}
        return Task.CompletedTask;
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(Process(ct),Flush(ct));
    }
    async Task Process(CancellationToken ct)
    {
        await foreach(var (command,program) in queue.Reader.ReadAllAsync(ct))
        {
            var result=new UploadResult(command.CommandId,cluster,command.VertexId,"sending","CAN üzerinden Vertex ACK'i bekleniyor.",command.ContentHash,DateTimeOffset.UtcNow);
            try
            {
                if(command.ExpiresAt<=DateTimeOffset.UtcNow)throw new TimeoutException("Kuyrukta beklerken gönderim süresi doldu.");
                Save(result);
                var completion=new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[command.CommandId]=(command.VertexId,program.Crc,completion);
                var packet=ScenarioProtocol.Packet(command.CommandId,program);
                byte? answer=null;
                for(int attempt=0;attempt<3 && answer==null;attempt++)
                {
                    if(command.ExpiresAt<=DateTimeOffset.UtcNow)break;
                    // The existing socket lock also serializes RTC writes with uploads.
                    await Task.Run(()=>devices[command.VertexId].Send(packet),ct);
                    try {answer=await completion.Task.WaitAsync(TimeSpan.FromSeconds(3),ct);}
                    catch(TimeoutException){ }
                }
                if(answer==null)throw new TimeoutException("Vertex ACK'i alınamadı; yüklenmiş olabilir, sonuç doğrulanamadı.");
                Save(result with {State=answer==0?"loaded":"rejected",Message=answer switch {0=>"Vertex profili RAM'e yükledi. Test başlatılmadı.",1=>"Vertex paketi reddetti.",2=>"Vertex içerik CRC kontrolü başarısız.",3=>"Vertex aktarım kimliği çakışması.",_=>$"Vertex yüklemeyi reddetti (kod {answer})."},TimestampUtc=DateTimeOffset.UtcNow});
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){Save(result with{State=ex is TimeoutException?"timeout":"failed",Message=ex.Message,TimestampUtc=DateTimeOffset.UtcNow});}
            finally{pending.TryRemove(command.CommandId,out _);}
        }
    }
    async Task Flush(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                if(client.IsConnected)
                {
                    List<(string Id,string Json)> rows=[];
                    lock(gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT id,result FROM uploads WHERE pending=1 LIMIT 32";using var reader=q.ExecuteReader();while(reader.Read())rows.Add((reader.GetString(0),reader.GetString(1)));}
                    foreach(var row in rows)
                    {
                        var result=JsonSerializer.Deserialize<UploadResult>(row.Json,Json)!;
                        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        var sent=await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"tronloop/{cluster}/{result.VertexId}/scenario/result").WithPayload(row.Json).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),timeout.Token);
                        if(sent.IsSuccess)lock(gate){using var c=Open();using var q=c.CreateCommand();q.CommandText="UPDATE uploads SET pending=0 WHERE id=$id AND result=$r";q.Parameters.AddWithValue("$id",row.Id);q.Parameters.AddWithValue("$r",row.Json);q.ExecuteNonQuery();}
                    }
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){log.LogWarning(ex,"Scenario ACK delivery will retry");}
            await Task.Delay(500,ct);
        }
    }
}
