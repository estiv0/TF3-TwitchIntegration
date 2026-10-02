using Microsoft.Extensions.Options;
using Tf3Bridge;
using Tf3Bridge.Output;
using Tf3Bridge.Processing;
using Tf3Bridge.Ui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Jedna instancja: dwa mosty czytające ten sam folder kasowałyby sobie pliki.
        using var mutex = new Mutex(true, @"Local\Tf3Bridge", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Tf3Bridge już działa (sprawdź ikonę w zasobniku systemowym).", "Tf3Bridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // ContentRoot = folder exe, żeby appsettings.json się znalazł niezależnie od katalogu startowego.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        var uiLog = new UiLogProvider();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(uiLog);
        builder.Logging.AddDebug();

        builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.Section));
        builder.Services.PostConfigure<BridgeOptions>(o => o.OutputDir = BridgeOptions.ResolveOutputDir(o.OutputDir));
        builder.Services.AddSingleton<IPublisher, LocalFolderPublisher>();
        builder.Services.AddSingleton<ExportProcessor>();
        builder.Services.AddSingleton<BridgeRunner>();

        using var host = builder.Build();
        var runner = host.Services.GetRequiredService<BridgeRunner>();
        var options = host.Services.GetRequiredService<IOptions<BridgeOptions>>().Value;

        Application.Run(new MainForm(runner, options, uiLog));
    }
}
