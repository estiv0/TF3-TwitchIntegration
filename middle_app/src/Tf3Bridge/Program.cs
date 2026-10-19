using Microsoft.Extensions.Options;
using Tf3Bridge;
using Tf3Bridge.Chat;
using Tf3Bridge.Cloud;
using Tf3Bridge.Commands;
using Tf3Bridge.Output;
using Tf3Bridge.Processing;
using Tf3Bridge.Rewards;
using Tf3Bridge.Twitch;
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
        // Ustawienia konkretnego streamera (np. Twitch:SiteUrl) poza repozytorium; argumenty dalej wygrywają.
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
        builder.Configuration.AddCommandLine(args);

        var uiLog = new UiLogProvider();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(uiLog);
        builder.Logging.AddDebug();

        builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.Section));
        builder.Services.PostConfigure<BridgeOptions>(o =>
        {
            o.InputDir = BridgeOptions.ResolveInputDir(o.InputDir);
            o.OutputDir = BridgeOptions.ResolveOutputDir(o.OutputDir);
            if (string.IsNullOrWhiteSpace(o.TerrainCacheFile))
                o.TerrainCacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tf3Bridge", "terrain-cache.json");
        });
        builder.Services.Configure<TwitchOptions>(builder.Configuration.GetSection(TwitchOptions.Section));
        builder.Services.AddSingleton<TwitchSession>();
        builder.Services.AddSingleton<EventSubClient>();
        builder.Services.AddSingleton<ModCommandQueue>();
        builder.Services.AddSingleton<IndustryZoneRules>();
        builder.Services.AddSingleton<RewardService>();
        builder.Services.AddSingleton<ChatCommandService>();
        builder.Services.AddSingleton<LocalFolderPublisher>();
        builder.Services.AddSingleton<AzureMirror>();
        builder.Services.AddSingleton<IPublisher, MirroringPublisher>();
        builder.Services.AddSingleton<ExportProcessor>();
        builder.Services.AddSingleton<BridgeRunner>();

        using var host = builder.Build();
        var runner = host.Services.GetRequiredService<BridgeRunner>();
        var options = host.Services.GetRequiredService<IOptions<BridgeOptions>>().Value;
        var mirror = host.Services.GetRequiredService<AzureMirror>();
        mirror.Configure(AzureSettingsStore.Load());
        var mod = ModInstaller.Sync(options.InputDir, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ModInstaller)));
        ModSettingsStore.EnsureExists(options.InputDir);
        host.Services.GetRequiredService<ChatCommandService>(); // komendy z czatu (!...): nasłuchuje EventSub od utworzenia

        Application.Run(new MainForm(runner, mirror, host.Services.GetRequiredService<TwitchSession>(), host.Services.GetRequiredService<EventSubClient>(),
            host.Services.GetRequiredService<ModCommandQueue>(), host.Services.GetRequiredService<RewardService>(), options, mod, uiLog));
    }
}
