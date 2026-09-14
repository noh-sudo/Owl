using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OwlServer.Config;
using OwlServer.Hardware;
using OwlServer.Network;
using OwlServer.Repository;
using OwlServer.Services;
using OwlServer.Storage;
using OwlServer.Utils;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ServerSettings>(builder.Configuration.GetSection(ServerSettings.SectionName));

// Data access
builder.Services.AddSingleton<MySqlConnectionFactory>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<CamLogRepository>();
builder.Services.AddSingleton<ShootLogRepository>();

// Storage / domain services
builder.Services.AddSingleton<ImageStorage>();
builder.Services.AddSingleton<ClientBroadcastService>();
builder.Services.AddSingleton<VideoService>();
builder.Services.AddSingleton<AuthenticationService>();
builder.Services.AddSingleton<LogService>();

// ArduinoBridge is both an injectable singleton (DetectionService calls
// BroadcastStateAsync on it) and a hosted service (it runs its own TCP accept
// loop) - register the concrete type once and wrap the same instance.
builder.Services.AddSingleton<ArduinoBridge>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ArduinoBridge>());

// ArduinoSerialBridge just wraps a COM port (no accept loop needed) - registering
// it as a plain disposable singleton is enough for the host to open it once and
// close it on shutdown.
builder.Services.AddSingleton<ArduinoSerialBridge>();

builder.Services.AddSingleton<DetectionService>();

// Listeners - each owns its own TCP accept loop, nothing else depends on them directly.
builder.Services.AddHostedService<VideoListener>();
builder.Services.AddHostedService<DataListener>();
builder.Services.AddHostedService<WpfListener>();

var host = builder.Build();

Logger.Info("올빼미(Owl-1) C# Main Server starting...");
await host.RunAsync().ConfigureAwait(false);
Logger.Info("올빼미(Owl-1) C# Main Server stopped.");
