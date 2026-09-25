using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MQTTnet;
using MQTTnet.Protocol;
using Tronloop.ClusterPilot.Engine.Models;

namespace Tronloop.ClusterPilot.Engine;

public sealed class MqttMessagePublisher(
    IMqttClient client, SqliteTelemetryStore store,
    IConfiguration configuration, ILogger<MqttMessagePublisher> logger)
{
    private readonly string _topicTemplate = ResolveTopic(configuration);

    private static string ResolveTopic(IConfiguration configuration)
    {
        var topic = configuration["Telemetry:MqttTopic"];
        if (!string.IsNullOrWhiteSpace(topic))
        {
            return topic;
        }

        var clusterPilotId = configuration["ClusterPilot:Id"];
        if (string.IsNullOrWhiteSpace(clusterPilotId) || clusterPilotId.IndexOfAny(['/', '+', '#', '\0']) >= 0)
        {
            throw new InvalidOperationException("ClusterPilot:Id must be a non-empty MQTT topic segment. Configure ClusterPilot:Id or the ClusterPilot__Id environment variable.");
        }

        return $"tronloop/{clusterPilotId}/{{VertexId}}/{{MessageType}}";
    }

    public async Task PublishAsync<T>(
        string vertexId, string canInterface, uint rxId, uint txId, DateTimeOffset receivedAtUtc,
        T payload, CancellationToken cancellationToken) where T : unmanaged
    {
        var binaryPayload = new byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(binaryPayload, in payload);
        var messageType = typeof(T) == typeof(VertexStatusPayload) ? "vertex-status" : "base-telemetry";
        var topic = _topicTemplate
            .Replace("{VertexId}", vertexId, StringComparison.Ordinal)
            .Replace("{MessageType}", messageType, StringComparison.Ordinal);

        try
        {
            if (client.IsConnected)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(binaryPayload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                var result = await client.PublishAsync(message, timeout.Token);
                if (result.IsSuccess)
                {
                    return;
                }

                logger.LogWarning("MQTT telemetry rejected ({ReasonCode}); saving to SQLite.", result.ReasonCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT telemetry publish failed; saving to SQLite.");
        }

        // Give an already received packet a short chance to persist during shutdown.
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.SaveAsync(canInterface, rxId, txId, receivedAtUtc, payload,
            cancellationToken.IsCancellationRequested ? shutdownTimeout.Token : cancellationToken);
    }
}
