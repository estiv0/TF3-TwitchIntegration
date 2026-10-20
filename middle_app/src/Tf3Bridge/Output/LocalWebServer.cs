using System.Net;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace Tf3Bridge.Output;

/// <summary>
/// The local website at http://localhost:WebPort/, complete without the repository: /data/ is OutputDir (where
/// web/config.js reads on localhost), everything else is looked up in WebDir on disk (site.json, the logo, or
/// the whole site in a checkout), then in the game icons (<see cref="GameIcons"/>), then in the site built
/// into the exe. HttpListener only, so no extra dependencies; a localhost prefix needs no admin rights.
/// </summary>
public sealed class LocalWebServer : IDisposable
{
    private const string DataPrefix = "/data/";
    private const string ResourcePrefix = "web/";

    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> BuiltIn = new(LoadBuiltIn);

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

    private readonly ILogger<LocalWebServer> _logger;
    private readonly string _webDir;
    private readonly string _iconDir;
    private readonly string _dataDir;
    private readonly int _port;
    private readonly IReadOnlyDictionary<string, byte[]> _builtIn;
    private HttpListener? _listener;

    public LocalWebServer(IOptions<BridgeOptions> options, ILogger<LocalWebServer> logger) : this(options, logger, BuiltIn.Value) { }

    /// <param name="builtIn">the site built into the exe: path relative to web/ ("js/main.js") → contents.</param>
    internal LocalWebServer(IOptions<BridgeOptions> options, ILogger<LocalWebServer> logger, IReadOnlyDictionary<string, byte[]> builtIn)
    {
        _logger = logger;
        _webDir = options.Value.WebDir;
        _iconDir = options.Value.IconCacheDir;
        _dataDir = options.Value.OutputDir;
        _port = options.Value.WebPort;
        _builtIn = builtIn;
    }

    /// <summary>Address of the running site, or null (turned off or the port is taken).</summary>
    public string? Url { get; private set; }

    public WebServerState State { get; private set; } = WebServerState.Off;

    public int Port => _port;

    /// <summary>The website folder on disk that wins over the built-in copy; "" when there is none.</summary>
    public string WebDir => Directory.Exists(_webDir) ? _webDir : "";

    public void Start()
    {
        if (_port <= 0)
        {
            State = WebServerState.Off;
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
            _logger.LogWarning("Local website off: cannot listen on {Url}: {Message}", url, ex.Message);
            return;
        }
        _listener = listener;
        Url = url;
        State = WebServerState.Running;
        _logger.LogInformation("Local website: {Url} (data from {DataDir}, files from {WebDir}, game icons from {IconDir} and the built-in site)",
            url, _dataDir, WebDir.Length > 0 ? WebDir : "-", _iconDir);
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
            var file = Resolve(Uri.UnescapeDataString(request.Url!.AbsolutePath));
            if (file is null)
            {
                response.StatusCode = 404;
                return;
            }

            // The data changes every few seconds and the site is edited during development: never cache.
            response.Headers["Cache-Control"] = "no-store";
            response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file.Name), "application/octet-stream");
            if (file.BuiltIn is { } bytes)
            {
                response.ContentLength64 = bytes.Length;
                if (request.HttpMethod == "GET") await response.OutputStream.WriteAsync(bytes);
                return;
            }
            // Files are replaced atomically (LocalFolderPublisher), so a reader sees the old or the new one whole.
            await using var stream = new FileStream(file.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
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
            _logger.LogWarning(ex, "Local website request failed");
            TrySetStatus(response, 500);
        }
        finally
        {
            try { response.Close(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
    }

    /// <summary>A file to send: on disk (Name = its path) or built in (Name = its path in web/, BuiltIn = contents).</summary>
    internal sealed record WebFile(string Name, byte[]? BuiltIn = null);

    /// <summary>
    /// URL path → file: /data/... from OutputDir; anything else from WebDir, the game icons, or the built-in
    /// site, the first that has it. "/" and folders → index.html. Null when nobody has it or the path leaves its root (../).
    /// </summary>
    internal WebFile? Resolve(string urlPath)
    {
        if (urlPath.Contains('\0') || urlPath.Contains('\\')) return null; // only "/" separates folders
        if (urlPath.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase))
            return FileIn(_dataDir, urlPath[DataPrefix.Length..]) is { } data ? new WebFile(data) : null;

        var rest = urlPath.TrimStart('/');
        if ((FileIn(_webDir, rest) ?? FileIn(_iconDir, rest)) is { } file) return new WebFile(file);

        var segments = rest.Split('/');
        if (segments.Any(p => p is "." or "..")) return null;
        var key = rest.Length == 0 || rest.EndsWith('/') ? rest + "index.html" : rest;
        return _builtIn.TryGetValue(key, out var bytes) ? new WebFile(key, bytes) : null;
    }

    // A file under root, or null when it doesn't exist or the path leaves root.
    private static string? FileIn(string root, string rest)
    {
        if (root.Length == 0) return null;
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, rest.Replace('/', Path.DirectorySeparatorChar)));
        var inside = full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(Path.TrimEndingDirectorySeparator(rootFull) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside) return null;
        if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
        return File.Exists(full) ? full : null;
    }

    // %(RecursiveDir) from MSBuild has "\"
    private static IReadOnlyDictionary<string, byte[]> LoadBuiltIn()
    {
        var asm = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[name[ResourcePrefix.Length..].Replace('\\', '/')] = ms.ToArray();
        }
        return files;
    }

    private static void TrySetStatus(HttpListenerResponse response, int status)
    {
        try { response.StatusCode = status; }
        catch (InvalidOperationException) { } // headers already sent
    }
}

public enum WebServerState { Off, PortTaken, Running }
