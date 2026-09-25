namespace Tronloop.ClusterPilot.Engine.Models;

public sealed record ClusterPilotHeartbeat(
    string ClusterPilotId,
    string State,
    CanDeviceSnapshot[] Devices,
    DateTimeOffset TimestampUtc);
