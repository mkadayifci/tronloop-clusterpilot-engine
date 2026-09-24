using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Tronloop.ClusterPilot.Engine;

public sealed class SqliteTelemetryStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteTelemetryStore> _logger;
    private readonly object _initializationLock = new();
    private bool _initialized;

    public SqliteTelemetryStore(
        IConfiguration configuration, IHostEnvironment environment, ILogger<SqliteTelemetryStore> logger)
    {
        _logger = logger;
        _databasePath = Path.GetFullPath(
            configuration["Telemetry:DatabasePath"] ?? "data/telemetry.sqlite",
            environment.ContentRootPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ToString();
    }

    public void Initialize()
    {
        lock (_initializationLock)
        {
            if (_initialized)
            {
                return;
            }

            InitializeDatabase();
            _initialized = true;
        }
    }

    private void InitializeDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS CanTelemetry (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ReceivedAtUtc TEXT NOT NULL,
                CanInterface TEXT NOT NULL,
                RxId INTEGER NOT NULL,
                TxId INTEGER NOT NULL,
                Payload BLOB NOT NULL,
                PayloadLength INTEGER NOT NULL,
                PayloadType TEXT NOT NULL,
                SentAtUtc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_CanTelemetry_Pending
                ON CanTelemetry (Id) WHERE SentAtUtc IS NULL;
            """;
        command.ExecuteNonQuery();
        _logger.LogInformation("CAN telemetry database ready at {DatabasePath}", _databasePath);
    }

    public async Task SaveAsync<T>(
        string canInterface, uint rxId, uint txId, DateTimeOffset receivedAtUtc,
        T payload, CancellationToken cancellationToken) where T : unmanaged
    {
        // Persist the deserialized value using its packed struct layout.
        // The reader must use the same type/layout and byte order.
        var binaryPayload = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(binaryPayload, in payload);

        // Keep the current packet until SQLite accepts it. Do not reconnect the CAN
        // socket or silently discard this packet when the database is busy/unavailable.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Initialize();
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO CanTelemetry (
                        ReceivedAtUtc, CanInterface, RxId, TxId, Payload, PayloadLength,
                        PayloadType)
                    VALUES ($receivedAtUtc, $canInterface, $rxId, $txId, $payload, $length,
                            $type);
                    """;
                command.Parameters.AddWithValue("$receivedAtUtc", receivedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$canInterface", canInterface);
                command.Parameters.AddWithValue("$rxId", (long)rxId);
                command.Parameters.AddWithValue("$txId", (long)txId);
                command.Parameters.Add("$payload", SqliteType.Blob).Value = binaryPayload;
                command.Parameters.AddWithValue("$length", binaryPayload.Length);
                command.Parameters.AddWithValue("$type", typeof(T).Name);
                command.ExecuteNonQuery();
                return;
            }
            catch (SqliteException ex)
            {
                _logger.LogError(ex, "Could not persist CAN packet from {Interface} rx=0x{RxId:X}; retrying in 2 seconds.", canInterface, rxId);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous = FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
