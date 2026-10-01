using Tronloop.ClusterPilot.Engine;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<SqliteTelemetryStore>();
builder.Services.AddSingleton<MQTTnet.IMqttClient>(_ => new MQTTnet.MqttClientFactory().CreateMqttClient());
builder.Services.AddSingleton<MqttMessagePublisher>();
builder.Services.AddSingleton<Tronloop.ClusterPilot.Engine.Scenarios.ScenarioUploader>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Tronloop.ClusterPilot.Engine.Scenarios.ScenarioUploader>());
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
