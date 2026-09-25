using Tronloop.ClusterPilot.Engine;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<SqliteTelemetryStore>();
builder.Services.AddSingleton<MQTTnet.IMqttClient>(_ => new MQTTnet.MqttClientFactory().CreateMqttClient());
builder.Services.AddSingleton<MqttMessagePublisher>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
