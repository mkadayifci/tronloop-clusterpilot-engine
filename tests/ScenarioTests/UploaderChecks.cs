using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using Tronloop.ClusterPilot.Engine.Scenarios;

static class UploaderChecks
{
    public static async Task Run()
    {
        var dir=Path.Combine(Path.GetTempPath(),"tronloop-upload-test-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ClusterPilot:Id","cp-test"},{"Scenarios:DatabasePath",Path.Combine(dir,"uploads.sqlite")}}).Build();
        using var client=new MqttClientFactory().CreateMqttClient(); // Deliberately offline: no real device/broker traffic.
        var env=new TestEnvironment{ContentRootPath=dir};
        using var uploader=new ScenarioUploader(client,config,env,NullLogger<ScenarioUploader>.Instance);
        var transport=new Transport();uploader.Attach("vertex-test",transport);await uploader.StartAsync(default);
        using var steps=JsonDocument.Parse("""[{"id":"c","kind":"charge","targetCurrentMa":500}]""");
        var command=new UploadCommand(Guid.NewGuid(),"cp-test","vertex-test",Guid.NewGuid(),1,3,ScenarioProtocol.Hash(steps.RootElement),steps.RootElement,DateTimeOffset.UtcNow.AddMinutes(1));
        var jsonOptions=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payload=JsonSerializer.SerializeToUtf8Bytes(command,jsonOptions);const string topic="tronloop/cp-test/vertex-test/scenario/upload";
        await uploader.ReceiveAsync(topic,payload,true);Assert(transport.Sends==0,"Retained not executed");
        await uploader.ReceiveAsync(topic,payload,false);
        await AwaitState("loaded",command.CommandId);Assert(transport.Sends==1,"Only one CAN send");
        await uploader.ReceiveAsync(topic,payload,false);Assert(transport.Sends==1,"Duplicate replays persisted ACK without CAN resend");
        using(var c=Db())using(var q=c.CreateCommand()){q.CommandText="SELECT pending FROM uploads WHERE id=$id";q.Parameters.AddWithValue("$id",command.CommandId.ToString());Assert(Convert.ToInt32(q.ExecuteScalar())==1,"Offline ACK remains persisted");}
        var expired=command with{CommandId=Guid.NewGuid(),ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1)};
        await uploader.ReceiveAsync(topic,JsonSerializer.SerializeToUtf8Bytes(expired,jsonOptions),false);await AwaitState("rejected",expired.CommandId);
        var corrupted=command with{CommandId=Guid.NewGuid(),ContentHash=new string('0',64)};
        await uploader.ReceiveAsync(topic,JsonSerializer.SerializeToUtf8Bytes(corrupted,jsonOptions),false);await AwaitState("rejected",corrupted.CommandId);
        transport.Answer=false;
        var unanswered=command with{CommandId=Guid.NewGuid()};await uploader.ReceiveAsync(topic,JsonSerializer.SerializeToUtf8Bytes(unanswered,jsonOptions),false);
        await AwaitState("timeout",unanswered.CommandId,12000);Assert(transport.Sends==4,"No ACK retries exactly three times; never reports loaded");
        await uploader.StopAsync(default);
        using var restarted=new ScenarioUploader(client,config,env,NullLogger<ScenarioUploader>.Instance);var other=new Transport();restarted.Attach("vertex-test",other);
        await restarted.ReceiveAsync(topic,payload,false);Assert(other.Sends==0,"Completed command survives process restart");
        Console.WriteLine("Uploader checks: retained rejection, CAN ACK, duplicate/restart idempotence, durable offline result, expiry, hash rejection and ACK timeout passed.");
        SqliteConnection Db(){var c=new SqliteConnection("Data Source="+Path.Combine(dir,"uploads.sqlite"));c.Open();return c;}
        async Task AwaitState(string expected,Guid id,int timeout=3000)
        {
            var until=DateTime.UtcNow.AddMilliseconds(timeout);
            while(DateTime.UtcNow<until)
            {
                using(var c=Db())using(var q=c.CreateCommand()){q.CommandText="SELECT result FROM uploads WHERE id=$id";q.Parameters.AddWithValue("$id",id.ToString());var raw=q.ExecuteScalar() as string;if(raw!=null&&JsonDocument.Parse(raw).RootElement.GetProperty("state").GetString()==expected)return;}
                await Task.Delay(20);
            }
            throw new Exception("Missing state: "+expected);
        }
    }
    static void Assert(bool ok,string name){if(!ok)throw new Exception(name);}
    sealed class Transport:IScenarioTransport
    {
        public event Action<byte[]>? ScenarioAck;
        public int Sends;public bool Answer=true;
        public void Send(byte[] packet){Interlocked.Increment(ref Sends);if(!Answer)return;var ack=new byte[24];ack[0]=4;ack[1]=1;packet.AsSpan(4,16).CopyTo(ack.AsSpan(4));packet.AsSpan(24,4).CopyTo(ack.AsSpan(20));ScenarioAck?.Invoke(ack);}
    }
    sealed class TestEnvironment:IHostEnvironment
    {public string EnvironmentName{get;set;}="Test";public string ApplicationName{get;set;}="ScenarioTests";public string ContentRootPath{get;set;}="";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();}
}
