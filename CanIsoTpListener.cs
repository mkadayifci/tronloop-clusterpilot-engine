using Tronloop.ClusterPilot.Engine.Models;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Tronloop.ClusterPilot.Engine;

public sealed class CanIsoTpListener : IDisposable
{
    private const int AF_CAN = 29;
    private const int SOCK_DGRAM = 2;
    private const int CAN_ISOTP = 6;
    private const int SOL_SOCKET = 1;
    private const int SO_RCVTIMEO = 20;
    private const int SO_SNDTIMEO = 21;

    private const int EAGAIN = 11;
    private const int EWOULDBLOCK = 11;
    private const int EBADF = 9;
    private const int ENETDOWN = 100;
    private const int ENODEV = 19;
    private const int ETIMEDOUT = 110;

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RtcSyncInterval = TimeSpan.FromMinutes(1);

    private readonly string _interfaceName;
    private readonly string _vertexId;
    private readonly uint _rxId;
    private readonly uint _txId;
    private readonly ILogger _logger;
    private readonly MqttMessagePublisher _mqttMessagePublisher;
    private readonly CanDeviceStatus _status;
    private readonly object _sync = new();

    private int _socketFd = -1;
    private bool _disposed;

    public CanIsoTpListener(string interfaceName, uint rxId, uint txId, string vertexId, ILogger logger, MqttMessagePublisher mqttMessagePublisher, CanDeviceStatus status)
    {
        _status = status;
        _interfaceName = interfaceName;
        _vertexId = vertexId;
        _rxId = rxId;
        _txId = txId;
        _logger = logger;
        _mqttMessagePublisher = mqttMessagePublisher;
    }

    public void Open()
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (_socketFd >= 0)
            {
                return;
            }

            _socketFd = OpenSocket();
            _status.SetState("listening");
        }
    }

    public Task ListenAsync(CancellationToken cancellationToken)
    {
        // The loop body below never hits a real "await" while reads are succeeding or timing
        // out (it just "continue"s), so an ordinary async method would run it synchronously
        // on the caller's thread and never return until cancellation. Task.Run moves the whole
        // loop onto a background thread so ListenAsync itself returns immediately.
        return Task.Run(async () =>
        {
            var buffer = new byte[4096];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        EnsureOpen();

                        var fd = GetSocketFd();
                        var bytesRead = read(fd, buffer, buffer.Length);

                        if (bytesRead > 0)
                        {
                            var receivedAtUtc = DateTimeOffset.UtcNow;
                            _status.RecordReceived(receivedAtUtc);
                            if (!CanPackage.TryParse(buffer.AsSpan(0, (int)bytesRead), out var telemetry, out var error))
                            {
                                _logger.LogWarning("Vertex {VertexId}: {Error} Packet not stored.", _vertexId, error);
                                continue;
                            }
                            if (telemetry is VertexTelemetryPayload vertexTelemetry)
                            {
                                await _mqttMessagePublisher.PublishAsync(
                                    _vertexId, _interfaceName, _rxId, _txId, receivedAtUtc,
                                    vertexTelemetry, cancellationToken);
                                _logger.LogInformation(
                                    "Vertex {VertexId} telemetry: voltage={VoltageMv} mV, current={CurrentMa} mA, measurement time={MeasurementTimeMs} Unix ms",
                                    _vertexId, vertexTelemetry.BatteryVoltageMv,
                                    vertexTelemetry.BatteryCurrentMa,
                                    vertexTelemetry.MeasurementTimeMs);
                            }
                            else if (telemetry is VertexStatusPayload vertexStatus)
                            {
                                await _mqttMessagePublisher.PublishAsync(
                                    _vertexId, _interfaceName, _rxId, _txId, receivedAtUtc,
                                    vertexStatus, cancellationToken);
                                _logger.LogInformation(
                                    "Vertex {VertexId} status: type={PayloadType}, context voltage={VoltageMv} mV, scenario={ScenarioState}, context charger mode={ChargerMode}, current={CurrentMa} mA, battery temp={BatteryTempC:F1} C, ambient temp={AmbientTempC:F1} C",
                                    _vertexId, vertexStatus.PayloadType, vertexStatus.BatteryVoltageMv,
                                    vertexStatus.ScenarioPlayerState, vertexStatus.ChargerMode,
                                    vertexStatus.BatteryCurrentMa, vertexStatus.BatteryTemperatureDc / 10.0,
                                    vertexStatus.AmbientTemperatureDc / 10.0);
                            }
                            else
                            {
                                _logger.LogWarning("Unsupported ISO-TP payload length {Length}; packet not stored.", bytesRead);
                            }

                            continue;
                        }

                        if (bytesRead < 0)
                        {
                            var errno = Marshal.GetLastWin32Error();

                            if (errno is EAGAIN or EWOULDBLOCK or ETIMEDOUT)
                            {
                                continue;
                            }

                            _logger.LogWarning("ISO-TP read error (errno={Errno}); reconnecting socket.", errno);

                            _status.SetState("reconnecting", $"ISO-TP read error: errno={errno}");
                            CloseSocketSafely();

                            if (IsFatalSocketError(errno))
                            {
                                await DelayBeforeReconnect(cancellationToken);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _status.SetState("reconnecting", ex.Message);
                        _logger.LogWarning(ex, "ISO-TP listener failed; will retry.");
                        CloseSocketSafely();
                        await DelayBeforeReconnect(cancellationToken);
                    }
                }
            }
            finally
            {
                CloseSocketSafely();
                _status.SetState("stopped");
            }
        }, CancellationToken.None);
    }

    public Task SynchronizeRtcAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(RtcSyncInterval);
            byte sequence = 0;
            try
            {
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        Send(CanPackage.CreateRtcSet(DateTimeOffset.UtcNow, sequence));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Vertex {VertexId}: RTC_SET send failed; will retry next minute.", _vertexId);
                    }
                    sequence = unchecked((byte)(sequence + 1));
                }
                while (await timer.WaitForNextTickAsync(cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
        }, CancellationToken.None);
    }

    private void EnsureOpen()
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (_socketFd >= 0)
            {
                return;
            }

            _socketFd = OpenSocket();
            _status.SetState("listening");
        }
    }

    private int OpenSocket()
    {
        var ifIndex = if_nametoindex(_interfaceName);
        if (ifIndex == 0)
        {
            throw new InvalidOperationException(
                $"CAN interface '{_interfaceName}' not found or not available (errno={Marshal.GetLastWin32Error()}).");
        }

        var fd = socket(AF_CAN, SOCK_DGRAM, CAN_ISOTP);
        if (fd < 0)
        {
            throw new InvalidOperationException(
                $"Failed to create ISO-TP socket (errno={Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var timeout = new Timeval { Seconds = 1, Microseconds = 0 };
            if (setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, ref timeout, Marshal.SizeOf<Timeval>()) != 0)
            {
                // The listener must periodically return from read to observe cancellation.
                throw new InvalidOperationException(
                    $"Failed to set ISO-TP receive timeout (errno={Marshal.GetLastWin32Error()}).");
            }

            if (setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, ref timeout, Marshal.SizeOf<Timeval>()) != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to set ISO-TP send timeout (errno={Marshal.GetLastWin32Error()}).");
            }

            var addr = new SockaddrCan
            {
                CanFamily = AF_CAN,
                CanIfIndex = (int)ifIndex,
                RxId = _rxId,
                TxId = _txId
            };

            if (bind(fd, ref addr, Marshal.SizeOf<SockaddrCan>()) != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"Failed to bind ISO-TP socket on '{_interfaceName}' " +
                    $"(rx=0x{_rxId:X}, tx=0x{_txId:X}, errno={errno}).");
            }

            _logger.LogInformation(
                "ISO-TP socket bound on {Interface} rx=0x{RxId:X} tx=0x{TxId:X}",
                _interfaceName, _rxId, _txId);

            return fd;
        }
        catch
        {
            close(fd);
            throw;
        }
    }

    public void Send(byte[] data)
    {
        // Keep reconnect/dispose from closing or reusing the descriptor during a write.
        lock (_sync)
        {
            EnsureOpen();
            var bytesWritten = write(_socketFd, data, data.Length);
            if (bytesWritten < 0)
            {
                throw new InvalidOperationException(
                    $"ISO-TP write failed (errno={Marshal.GetLastWin32Error()}).");
            }
            if (bytesWritten != data.Length)
            {
                throw new InvalidOperationException(
                    $"ISO-TP write incomplete: {bytesWritten} of {data.Length} bytes.");
            }

            _logger.LogInformation("Vertex {VertexId}: ISO-TP TX [{Length} bytes]: {Hex}",
                _vertexId, bytesWritten, Convert.ToHexString(data));
        }
    }

    private int GetSocketFd()
    {
        lock (_sync)
        {
            return _socketFd;
        }
    }

    private void CloseSocketSafely()
    {
        lock (_sync)
        {
            if (_socketFd < 0)
            {
                return;
            }

            var fd = _socketFd;
            _socketFd = -1;
            close(fd);
        }
    }

    private static bool IsFatalSocketError(int errno)
    {
        return errno is EBADF or ENETDOWN or ENODEV;
    }

    private static async Task DelayBeforeReconnect(CancellationToken cancellationToken)
    {
        await Task.Delay(ReconnectDelay, cancellationToken);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _status.SetState("stopped");

            if (_socketFd >= 0)
            {
                close(_socketFd);
                _socketFd = -1;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CanIsoTpListener));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrCan
    {
        public ushort CanFamily;
        public int CanIfIndex;
        public uint RxId;
        public uint TxId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timeval
    {
        public long Seconds;
        public long Microseconds;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint if_nametoindex(string ifname);

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int bind(int sockfd, ref SockaddrCan addr, int addrlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int sockfd, int level, int optname, ref Timeval optval, int optlen);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
