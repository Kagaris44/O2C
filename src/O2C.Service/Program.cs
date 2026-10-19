using O2C.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = ServicePaths.ServiceName);
builder.Logging.ClearProviders();
builder.Logging.AddEventLog(settings => settings.SourceName = ServicePaths.ProductName);
builder.Logging.AddProvider(new FileLoggerProvider(ServicePaths.ServiceLogFile));

builder.Services.AddSingleton<LogBuffer>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionManager>());
builder.Services.AddSingleton<ExclusionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExclusionManager>());
builder.Services.AddHostedService<PipeServer>();
builder.Services.AddHostedService<NetConfigLogTailer>();

ServicePaths.EnsureDataDirectory();

var host = builder.Build();
host.Run();
