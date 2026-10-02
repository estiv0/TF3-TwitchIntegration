using Microsoft.Extensions.Options;
using Tf3Bridge.Processing;

namespace Tf3Bridge;

/// <summary>Runs <see cref="ExportProcessor"/> every PollIntervalMs.</summary>
public sealed class BridgeWorker(ExportProcessor processor, IOptions<BridgeOptions> options, ILogger<BridgeWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        logger.LogInformation("Watching {Input}, publishing to {Output}", o.InputDir, Path.GetFullPath(o.OutputDir));
        if (!Directory.Exists(o.InputDir))
            logger.LogWarning("Input folder does not exist yet (it appears after the mod writes its first file)");

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(o.PollIntervalMs));
        do
        {
            try
            {
                await processor.ProcessOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Processing pass failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
