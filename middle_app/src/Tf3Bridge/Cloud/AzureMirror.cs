using System.IO.Compression;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Tf3Bridge.Processing;

namespace Tf3Bridge.Cloud;

public enum AzureState { Off, Incomplete, Ok, Error }

/// <summary>A status whose <see cref="Message"/> is translated when shown, so it follows a language switch.</summary>
public readonly record struct AzureStatus(AzureState State, Func<string> Text, DateTime? LastUploadUtc)
{
    public AzureStatus(AzureState state, string message, DateTime? lastUploadUtc) : this(state, () => message, lastUploadUtc) { }

    public string Message => Text();
}

/// <summary>
/// Copies the local output folder (latest/*) to an Azure Blob container.
/// The local publisher marks written paths as dirty; <see cref="FlushAsync"/> (called by the
/// runner after each pass) uploads them from the local files. A failed upload stays dirty and
/// is retried later, so Azure catches up after an outage even if the data does not change again.
/// meta.json goes last and only when everything else succeeded: the site reads meta first and
/// fetches the rest when its seq changes, so meta must never point at data not uploaded yet.
/// </summary>
public sealed class AzureMirror(IOptions<BridgeOptions> options, ILogger<AzureMirror> logger)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly string _root = options.Value.OutputDir;
    private readonly object _lock = new();
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private BlobContainerClient? _container;
    private DateTime _retryAfterUtc;
    private string? _lastError;

    public AzureStatus Status { get; private set; } = new(AzureState.Off, () => Loc.T("off"), null);

    public void Configure(AzureSettings settings)
    {
        lock (_lock)
        {
            _lastError = null;
            _retryAfterUtc = default;
            if (!settings.Enabled)
            {
                _container = null;
                Status = new AzureStatus(AzureState.Off, () => Loc.T("off"), Status.LastUploadUtc);
                return;
            }
            if (!settings.TryGetContainerUri(out var uri))
            {
                _container = null;
                Status = new AzureStatus(AzureState.Incomplete, () => Loc.T("no container address or SAS token"), Status.LastUploadUtc);
                return;
            }
            _container = new BlobContainerClient(uri);
            Status = new AzureStatus(AzureState.Ok, () => Loc.T("connecting…"), Status.LastUploadUtc);
            MarkAllDirtyLocked(); // new target: send everything that is already local
        }
        logger.LogInformation("Azure upload enabled: {Url}", settings.PublicLatestUrl);
    }

    public void MarkDirty(string path)
    {
        lock (_lock) _dirty.Add(path);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        BlobContainerClient? container;
        List<string> paths;
        lock (_lock)
        {
            container = _container;
            if (container is null || _dirty.Count == 0 || DateTime.UtcNow < _retryAfterUtc) return;
            paths = _dirty.OrderBy(p => p == ExportProcessor.MetaPath ? 1 : 0).ThenBy(p => p, StringComparer.Ordinal).ToList();
        }

        foreach (var path in paths)
        {
            try
            {
                var uploaded = await UploadAsync(container, path, ct);
                lock (_lock)
                {
                    if (!ReferenceEquals(container, _container)) return; // the settings changed in the meantime
                    _dirty.Remove(path);
                    if (uploaded) Status = new AzureStatus(AzureState.Ok, "OK", DateTime.UtcNow);
                    _lastError = null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = Describe(ex);
                lock (_lock)
                {
                    if (!ReferenceEquals(container, _container)) return;
                    _retryAfterUtc = DateTime.UtcNow + RetryDelay;
                    Status = new AzureStatus(AzureState.Error, message, Status.LastUploadUtc);
                    if (message == _lastError) return; // the same error as last time - don't clutter the log
                    _lastError = message;
                }
                logger.LogWarning("Azure upload of {Path} failed: {Message}. Retrying in {Delay} s", path, message, RetryDelay.TotalSeconds);
                return; // the rest (including meta.json) waits for the next attempt
            }
        }
    }

    /// <summary>Uploads one small blob to check URL and SAS. Returns null on success, otherwise a readable error.</summary>
    public static async Task<string?> TestAsync(AzureSettings settings, CancellationToken ct)
    {
        if (!settings.TryGetContainerUri(out var uri))
            return Loc.T("Enter the container address (https://…) and the SAS token.");
        try
        {
            var blob = new BlobContainerClient(uri).GetBlobClient("bridge-test.txt");
            await blob.UploadAsync(BinaryData.FromString($"Tf3Bridge test {DateTime.UtcNow:O}"),
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "text/plain" } }, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Describe(ex);
        }
    }

    // false = the file is no longer there locally (nothing to send)
    private async Task<bool> UploadAsync(BlobContainerClient container, string path, CancellationToken ct)
    {
        var local = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(local)) return false;

        byte[] data;
        await using (var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[fs.Length];
            await fs.ReadExactlyAsync(data, ct);
        }

        var headers = new BlobHttpHeaders { ContentType = ContentType(path), CacheControl = "no-cache" };
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            data = Gzip(data); // the browser unpacks it itself thanks to Content-Encoding
            headers.ContentEncoding = "gzip";
        }

        await container.GetBlobClient(path).UploadAsync(new BinaryData(data), new BlobUploadOptions { HttpHeaders = headers }, ct);
        return true;
    }

    private void MarkAllDirtyLocked()
    {
        var latest = Path.Combine(_root, "latest");
        if (!Directory.Exists(latest)) return;
        foreach (var file in Directory.EnumerateFiles(latest, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            _dirty.Add(Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/'));
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal)) gz.Write(data);
        return output.ToArray();
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static string Describe(Exception ex) => ex switch
    {
        RequestFailedException { Status: 403 } => Loc.T("403: access denied (the SAS token is invalid, expired or can't write)"),
        RequestFailedException { Status: 404 } => Loc.T("404: no such container (check the address)"),
        RequestFailedException r => $"{r.Status}: {r.ErrorCode ?? FirstLine(r.Message)}",
        HttpRequestException or AggregateException => Loc.F("no connection ({0})", FirstLine(ex.Message)),
        _ => FirstLine(ex.Message),
    };

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(['\r', '\n']);
        return i < 0 ? s : s[..i];
    }
}
