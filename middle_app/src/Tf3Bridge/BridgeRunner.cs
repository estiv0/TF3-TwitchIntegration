using Microsoft.Extensions.Options;
using Tf3Bridge.Cloud;
using Tf3Bridge.Processing;

namespace Tf3Bridge;

/// <summary>
/// Runs <see cref="ExportProcessor"/> every PollIntervalMs, then uploads changes with <see cref="AzureMirror"/>. Can be started and stopped
/// repeatedly from the UI; processor state (e.g. the assembled network) survives a restart.
/// </summary>
public sealed class BridgeRunner(ExportProcessor processor, AzureMirror mirror, IOptions<BridgeOptions> options, ILogger<BridgeRunner> logger)
{
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Raised on the thread that changed the state (UI for Start/Stop).</summary>
    public event Action? StateChanged;

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loop is null) return;
        await _cts.CancelAsync();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        logger.LogInformation("Stopped");
        StateChanged?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await Task.Yield(); // pętla poza wątkiem UI
        var o = options.Value;
        logger.LogInformation("Watching {Input}, publishing to {Output}", o.InputDir, o.OutputDir);
        if (o.InputDir.Length == 0)
            logger.LogWarning("Transport Fever 3 data folder not found in Steam - run the game once with the mod, "
                + "or set it with --Bridge:InputDir=<Steam>\\userdata\\<id>\\3493540\\local\\towns_industries");
        else if (!Directory.Exists(o.InputDir))
            logger.LogWarning("Input folder does not exist yet (it appears after the mod writes its first file)");

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(o.PollIntervalMs));
        do
        {
            try
            {
                await processor.ProcessOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Processing pass failed");
            }
            // osobno: błąd przetwarzania nie wstrzymuje dosyłania tego, co już jest lokalnie
            try
            {
                await mirror.FlushAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Azure upload pass failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
