using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using Tronloop.ClusterPilot.Engine.Models;

namespace Tronloop.ClusterPilot.Engine;

public sealed class MqttMessagePublisher(
    IMqttClient client, SqliteTelemetryStore store,
    IConfiguration configuration, ILogger<MqttMessagePublisher> logger)
{
    internal const uint StatusExpirySeconds = 15;

    // CAN payload structs expose public readonly fields instead of properties.
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        IncludeFields = true
    };

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
        var isStatus = typeof(T) == typeof(VertexStatusPayload);
        var messageType = isStatus ? "vertex-status" : "base-telemetry";
        var topic = _topicTemplate
            .Replace("{VertexId}", vertexId, StringComparison.Ordinal)
            .Replace("{MessageType}", messageType, StringComparison.Ordinal);

        try
        {
            if (client.IsConnected)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var builder = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJsonOptions))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
                if (isStatus)
                {
                    builder.WithMessageExpiryInterval(StatusExpirySeconds);
                }
                var result = await client.PublishAsync(builder.Build(), timeout.Token);
                if (result.IsSuccess)
                {
                    return;
                }

                logger.LogWarning("MQTT publish rejected ({ReasonCode}) for {MessageType}.", result.ReasonCode, messageType);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT publish failed for {MessageType}.", messageType);
        }

        // Status describes the present; the next CAN status replaces a missed one.
        if (isStatus)
        {
            logger.LogDebug("Skipping undelivered status for {VertexId}; waiting for the next update.", vertexId);
            return;
        }

        // Give an already received packet a short chance to persist during shutdown.
        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.SaveAsync(canInterface, rxId, txId, receivedAtUtc, payload,
            cancellationToken.IsCancellationRequested ? shutdownTimeout.Token : cancellationToken);
    }
}
