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
        if (Loc.Load() is { } language) Loc.Language = language;

        // Single instance: two bridges reading the same folder would delete each other's files.
        using var mutex = new Mutex(true, @"Local\Tf3Bridge", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show(Loc.T("Tf3Bridge is already running (check its icon in the system tray)."), "Tf3Bridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // ContentRoot = the exe folder, so appsettings.json is found regardless of the working directory.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        // Settings of a specific streamer (e.g. Twitch:SiteUrl) outside the repository; arguments still win.
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
            o.WebDir = BridgeOptions.ResolveWebDir(o.WebDir);
            o.GameDir = BridgeOptions.ResolveGameDir(o.GameDir);
            if (string.IsNullOrWhiteSpace(o.IconCacheDir))
                o.IconCacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tf3Bridge", "web-icons");
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
        builder.Services.AddSingleton<GameIcons>();
        builder.Services.AddSingleton<LocalWebServer>();
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
        var chatCommands = host.Services.GetRequiredService<ChatCommandService>(); // chat commands (!...): listens to EventSub from creation
        var web = host.Services.GetRequiredService<LocalWebServer>(); // the local website, independent of Start/Stop
        web.Start();
        _ = host.Services.GetRequiredService<GameIcons>().UpdateAsync(CancellationToken.None); // in the background; the site works without the icons meanwhile

        Application.Run(new MainForm(runner, mirror, host.Services.GetRequiredService<TwitchSession>(), host.Services.GetRequiredService<EventSubClient>(),
            host.Services.GetRequiredService<ModCommandQueue>(), host.Services.GetRequiredService<RewardService>(), chatCommands, web, options, mod, uiLog));
    }
}
