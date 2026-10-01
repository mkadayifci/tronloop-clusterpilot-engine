using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace Tronloop.ClusterPilot.Engine.Scenarios;

public sealed class RamProfileReader(IMqttClient client, IConfiguration configuration, ILogger<RamProfileReader> logger) : BackgroundService
{
    private readonly string clusterPilotId = configuration["ClusterPilot:Id"]!;
    private readonly ConcurrentDictionary<string, IScenarioTransport> devices = new();
    private readonly ConcurrentDictionary<Guid, (string VertexId, TaskCompletionSource<RamProfileResponse> Completion)> pending = new();
    private readonly Channel<RamProfileQuery> requests = Channel.CreateBounded<RamProfileQuery>(16);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Attach(string vertexId, IScenarioTransport transport)
    {
        devices[vertexId] = transport;
        transport.ScenarioAck += packet =>
        {
            if (RamProfileProtocol.TryResponse(packet, out var response) && response is not null &&
                pending.TryGetValue(response.RequestId, out var request) && request.VertexId == vertexId)
                request.Completion.TrySetResult(response);
        };
    }

    public void Receive(string topic, byte[] payload, bool retained)
    {
        if (retained || payload.Length is 0 or > 4096) return;
        try
        {
            var request = JsonSerializer.Deserialize<RamProfileQuery>(payload, Json);
            if (request is null || request.RequestId == Guid.Empty || request.ClusterPilotId != clusterPilotId ||
                topic != $"tronloop/{clusterPilotId}/{request.VertexId}/scenario/profile/query" ||
                request.ExpiresAt <= DateTimeOffset.UtcNow || request.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30)) return;
            if (!requests.Writer.TryWrite(request)) logger.LogWarning("RAM profile query queue is full");
        }
        catch (JsonException exception) { logger.LogWarning(exception, "Invalid RAM profile query"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in requests.Reader.ReadAllAsync(stoppingToken))
        {
            var remaining = request.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(remaining);
            var completion = new TaskCompletionSource<RamProfileResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[request.RequestId] = (request.VertexId, completion);
            var result = new RamProfileResult(request.RequestId, clusterPilotId, request.VertexId,
                "unavailable", null, null, null, null, DateTimeOffset.UtcNow);
            try
            {
                if (devices.TryGetValue(request.VertexId, out var transport))
                {
                    RamProfileResponse? response = null;
                    for (var attempt = 0; attempt < 2 && response is null; attempt++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        await Task.Run(() => transport.Send(RamProfileProtocol.QueryPacket(request.RequestId)), deadline.Token);
                        try { response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token); }
                        catch (TimeoutException) { }
                    }
                    result = response is null
                        ? result with { State = "timeout" }
                        : result with {
                            State = response.HasProfile ? "loaded" : "empty",
                            UploadCommandId = response.HasProfile ? response.UploadCommandId : null,
                            Crc32 = response.HasProfile ? response.Crc32 : null,
                            StepCount = response.HasProfile ? response.StepCount : null,
                            EntryStep = response.HasProfile ? response.EntryStep : null,
                            ObservedAtUtc = DateTimeOffset.UtcNow
                        };
                }
                if (client.IsConnected && DateTimeOffset.UtcNow < request.ExpiresAt)
                {
                    var expiry = (uint)Math.Max(1, Math.Floor((request.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
                    await client.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic($"tronloop/{clusterPilotId}/{request.VertexId}/scenario/profile/result")
                        .WithPayload(JsonSerializer.Serialize(result, Json))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithMessageExpiryInterval(expiry).Build(), deadline.Token);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "RAM profile query failed for {VertexId}", request.VertexId); }
            finally { pending.TryRemove(request.RequestId, out _); }
        }
    }
}
