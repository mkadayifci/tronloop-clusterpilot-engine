using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using Tronloop.ClusterPilot.Engine.Models;

namespace Tronloop.ClusterPilot.Engine;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly MqttMessagePublisher _mqttMessagePublisher;
    private readonly IMqttClient _mqttClient;
    private readonly string _clusterPilotId;

    private readonly List<CanDeviceStatus> _deviceStatuses = [];
    private TimeSpan _staleAfter;

    private const string NodeId = "A0";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Worker(ILogger<Worker> logger, IConfiguration configuration, MqttMessagePublisher mqttMessagePublisher, IMqttClient mqttClient)
    {
        _logger = logger;
        _configuration = configuration;
        _mqttMessagePublisher = mqttMessagePublisher;
        _mqttClient = mqttClient;
        var clusterPilotId = configuration["ClusterPilot:Id"];
        if (string.IsNullOrWhiteSpace(clusterPilotId) || clusterPilotId.IndexOfAny(['/', '+', '#', '\0']) >= 0)
        {
            throw new InvalidOperationException("ClusterPilot:Id must be a non-empty MQTT topic segment. Configure ClusterPilot:Id or the ClusterPilot__Id environment variable.");
        }
        _clusterPilotId = clusterPilotId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var canCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        List<CanIsoTpListener> canListeners = [];
        List<Task> canTasks = [];

        var staleSeconds = _configuration.GetValue<int>("Can:StaleAfterSeconds", 30);
        if (staleSeconds <= 0)
            throw new InvalidOperationException("Can:StaleAfterSeconds must be positive.");
        _staleAfter = TimeSpan.FromSeconds(staleSeconds);

        var canInterface = _configuration["Can:Interface"] ?? "can0";
        var devices = _configuration.GetSection("Can:Devices").GetChildren()
            .Select(device => (
                VertexId: device["VertexId"] ?? "",
                IsInstalled: device.GetValue<bool>("IsInstalled", true),
                RxId: ParseCanId(device["RxId"]),
                TxId: ParseCanId(device["TxId"])))
            .ToList();
        var vertexIds = new HashSet<string>(StringComparer.Ordinal);
        var canIds = new HashSet<uint>();
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.VertexId) ||
                device.VertexId.IndexOfAny(['/', '+', '#', '\0']) >= 0 ||
                !vertexIds.Add(device.VertexId) ||
                !canIds.Add(device.RxId) || !canIds.Add(device.TxId))
            {
                throw new InvalidOperationException("Can:Devices requires unique VertexId values (MQTT topic segments) and distinct RX/TX CAN IDs.");
            }
        }

        foreach (var device in devices)
        {
            var status = new CanDeviceStatus(device.VertexId, device.IsInstalled);
            _deviceStatuses.Add(status);
            if (!device.IsInstalled)
            {
                _logger.LogInformation("CAN listener inactive for {VertexId}", device.VertexId);
                continue;
            }
            var deviceLabel = $"{device.VertexId} {canInterface} rx=0x{device.RxId:X} tx=0x{device.TxId:X}";
            try
            {
                var canListener = new CanIsoTpListener(canInterface, device.RxId, device.TxId,
                    device.VertexId, _logger, _mqttMessagePublisher, status);
                canListeners.Add(canListener);
                canTasks.Add(canListener.ListenAsync(canCancellation.Token));
                _logger.LogInformation("CAN ISO-TP listener started on {Device}", deviceLabel);
            }
            catch (Exception ex)
            {
                status.SetState("faulted", ex.Message);
                _logger.LogError(ex, "Failed to start CAN ISO-TP listener on {Device}", deviceLabel);
            }
        }

        var client = _mqttClient;

        client.ApplicationMessageReceivedAsync += async e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

            _logger.LogInformation("MQTT RX Topic={Topic} Payload={Payload}", topic, payload);

            try
            {
                var command = JsonSerializer.Deserialize<NodeCommand>(payload, _jsonOptions);

                if (command is null)
                {
                    await PublishAck(client, "unknown", false, "Invalid command payload", stoppingToken);
                    return;
                }

                await HandleCommand(client, command, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command processing failed");

                await PublishAck(
                    client,
                    "unknown",
                    false,
                    ex.Message,
                    stoppingToken);
            }
        };

        var options = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithTcpServer("mqtt.tronloop-lab.com", 1883)
            .WithClientId($"engine-{_clusterPilotId}")
            .Build();

        using var heartbeatTimer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!client.IsConnected)
                    {
                        await client.ConnectAsync(options, stoppingToken);
                        await client.SubscribeAsync($"tronloop/node/{NodeId}/cmd", cancellationToken: stoppingToken);
                        await client.SubscribeAsync("tronloop/broadcast/cmd", cancellationToken: stoppingToken);
                        _logger.LogInformation("MQTT Connected-Subscribed");
                    }

                    await PublishStatus(client, "online", stoppingToken);
                    await PublishHeartbeat(client, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MQTT unavailable; CAN reception continues with SQLite fallback.");
                }

                if (!await heartbeatTimer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker failed");
        }
        finally
        {
            // MQTT failures can reach here without the host's stoppingToken being cancelled.
            // Stop CAN listeners and wait for in-flight I/O before disposing their sockets.
            canCancellation.Cancel();

            foreach (var canTask in canTasks)
            {
                try
                {
                    await canTask;
                }
                catch (OperationCanceledException) when (canCancellation.IsCancellationRequested)
                {
                    // Normal shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CAN task stopped with error");
                }
            }

            foreach (var canListener in canListeners)
            {
                canListener.Dispose();
            }

            try
            {
                if (client.IsConnected)
                {
                    await PublishStatus(client, "offline", CancellationToken.None);
                    await client.DisconnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed while disconnecting MQTT client");
            }
        }
    }

    private static uint ParseCanId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Each Can:Devices entry requires RxId and TxId.");
        }

        var trimmed = value.Trim();

        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(trimmed, CultureInfo.InvariantCulture);
    }

    private async Task HandleCommand(
        IMqttClient client,
        NodeCommand command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling command {Type} (Id: {Id}, Value: {Value})", command.Type, command.Id, command.Value);
        switch (command.Type)
        {
            case "start_charge":
                _logger.LogInformation("Start charge requested ");
                await PublishAck(client, command.Id, true, "Charge started", cancellationToken);
                break;

            case "stop_charge":
                _logger.LogInformation("Stop charge requested");
                await PublishAck(client, command.Id, true, "Charge stopped", cancellationToken);
                break;

            case "set_current":
                _logger.LogInformation("Set current requested: {Value}", command.Value);
                await PublishAck(client, command.Id, true, $"Current set to {command.Value}", cancellationToken);
                break;

            case "ping":
                await PublishAck(client, command.Id, true, "pong", cancellationToken);
                break;

            default:
                await PublishAck(client, command.Id, false, $"Unknown command: {command.Type}", cancellationToken);
                break;
        }
    }

    private static async Task PublishAck(
        IMqttClient client,
        string commandId,
        bool success,
        string message,
        CancellationToken cancellationToken)
    {
        var ack = new CommandAck
        {
            CommandId = commandId,
            Success = success,
            Message = message,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic($"tronloop/orchestrator/{NodeId}/ack")
            .WithPayload(JsonSerializer.Serialize(ack))
            .Build();

        await client.PublishAsync(mqttMessage, cancellationToken);
    }

    private CanDeviceSnapshot[] GetDeviceSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        return _deviceStatuses.Select(status => status.Snapshot(now, _staleAfter)).ToArray();
    }

    private async Task PublishStatus(
        IMqttClient client,
        string state,
        CancellationToken cancellationToken)
    {
        var status = new NodeStatus
        {
            NodeId = NodeId,
            State = state,
            Devices = GetDeviceSnapshots(),
            TimestampUtc = DateTimeOffset.UtcNow
        };

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"tronloop/orchestrator/{NodeId}/status")
            .WithPayload(JsonSerializer.Serialize(status))
            .WithMessageExpiryInterval(MqttMessagePublisher.StatusExpirySeconds)
            .Build();

        await client.PublishAsync(message, cancellationToken);
    }

    private async Task PublishHeartbeat(
        IMqttClient client,
        CancellationToken cancellationToken)
    {
        var heartbeat = new ClusterPilotHeartbeat(
            _clusterPilotId, "alive", GetDeviceSnapshots(), DateTimeOffset.UtcNow);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"tronloop/{_clusterPilotId}/heartbeat")
            .WithPayload(JsonSerializer.Serialize(heartbeat))
            .Build();

        await client.PublishAsync(message, cancellationToken);
    }
}

public sealed class NodeCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public double? Value { get; set; }
}

public sealed class CommandAck
{
    public string CommandId { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
}

public sealed class NodeStatus
{
    public CanDeviceSnapshot[] Devices { get; set; } = [];
    public string NodeId { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
}
