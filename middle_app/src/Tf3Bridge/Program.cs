using Tf3Bridge;
using Tf3Bridge.Output;
using Tf3Bridge.Processing;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.Section));
builder.Services.AddSingleton<IPublisher, LocalFolderPublisher>();
builder.Services.AddSingleton<ExportProcessor>();
builder.Services.AddHostedService<BridgeWorker>();

builder.Build().Run();
