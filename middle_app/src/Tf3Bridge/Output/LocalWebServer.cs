using System.Net;
using Microsoft.Extensions.Options;

namespace Tf3Bridge.Output;

/// <summary>
/// The local website at http://localhost:WebPort/: static files from WebDir and the published data from
/// OutputDir under /data/ (where web/config.js reads on localhost). HttpListener only, so no extra
/// dependencies; a localhost prefix needs no admin rights. Replaces web/serve.ps1 while Tf3Bridge runs.
/// </summary>
public sealed class LocalWebServer(IOptions<BridgeOptions> options, ILogger<LocalWebServer> logger) : IDisposable
{
    private const string DataPrefix = "/data/";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
        [".txt"] = "text/plain; charset=utf-8",
    };

    private readonly string _webDir = options.Value.WebDir;
    private readonly string _dataDir = options.Value.OutputDir;
    private readonly int _port = options.Value.WebPort;
    private HttpListener? _listener;

    /// <summary>Address of the running site, or null (turned off, no web/ folder, or the port is taken).</summary>
    public string? Url { get; private set; }

    public WebServerState State { get; private set; } = WebServerState.Off;

    public int Port => _port;

    public string WebDir => _webDir;

    public void Start()
    {
        if (_port <= 0)
        {
            State = WebServerState.Off;
            return;
        }
        if (_webDir.Length == 0 || !File.Exists(Path.Combine(_webDir, "index.html")))
        {
            State = WebServerState.NoWebDir;
            logger.LogWarning("Local website off: no index.html in {WebDir}", _webDir.Length > 0 ? _webDir : "(web folder not found)");
            return;
        }

        var url = $"http://localhost:{_port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(url);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();
            State = WebServerState.PortTaken;
            logger.LogWarning("Local website off: cannot listen on {Url}: {Message}", url, ex.Message);
            return;
        }
        _listener = listener;
        Url = url;
        State = WebServerState.Running;
        logger.LogInformation("Local website: {Url} (files from {WebDir}, data from {DataDir})", url, _webDir, _dataDir);
        _ = AcceptLoopAsync(listener);
    }

    public void Dispose()
    {
        _listener?.Close();
        _listener = null;
        Url = null;
        State = WebServerState.Off;
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; } // stopped
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            if (request.HttpMethod is not ("GET" or "HEAD"))
            {
                response.StatusCode = 405;
                return;
            }
            var file = MapPath(Uri.UnescapeDataString(request.Url!.AbsolutePath));
            if (file is null)
            {
                response.StatusCode = 404;
                return;
            }

            // The data changes every few seconds and the site is edited during development: never cache.
            response.Headers["Cache-Control"] = "no-store";
            response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
            // Files are replaced atomically (LocalFolderPublisher), so a reader sees the old or the new one whole.
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            response.ContentLength64 = stream.Length;
            if (request.HttpMethod == "GET") await stream.CopyToAsync(response.OutputStream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            TrySetStatus(response, 404); // replaced or removed between the check and the open
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // the browser went away mid-download (e.g. a reload); nothing to do
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local website request failed");
            TrySetStatus(response, 500);
        }
        finally
        {
            try { response.Close(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// URL path → file: /data/... from OutputDir, everything else from WebDir, "/" and folders → index.html.
    /// Null when the file does not exist or the path leaves its root (../).
    /// </summary>
    internal string? MapPath(string urlPath)
    {
        var (root, rest) = urlPath.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase)
            ? (_dataDir, urlPath[DataPrefix.Length..])
            : (_webDir, urlPath.TrimStart('/'));
        if (root.Length == 0 || rest.Contains('\0')) return null;

        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, rest.Replace('/', Path.DirectorySeparatorChar)));
        var inside = full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(Path.TrimEndingDirectorySeparator(rootFull) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside) return null;
        if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
        return File.Exists(full) ? full : null;
    }

    private static void TrySetStatus(HttpListenerResponse response, int status)
    {
        try { response.StatusCode = status; }
        catch (InvalidOperationException) { } // headers already sent
    }
}

public enum WebServerState { Off, NoWebDir, PortTaken, Running }
