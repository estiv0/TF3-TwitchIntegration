using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tf3Bridge.Output;

namespace Tf3Bridge.Tests;

public sealed class LocalWebServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tf3bridge-web-" + Guid.NewGuid().ToString("N"));
    private string Web => Path.Combine(_root, "web");
    private string Icons => Path.Combine(_root, "icons");
    private string Data => Path.Combine(_root, "out");

    private static readonly Dictionary<string, byte[]> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["index.html"] = Encoding.UTF8.GetBytes("<!doctype html>built in"),
        ["js/app.js"] = Encoding.UTF8.GetBytes("export const builtIn = 1;"),
        ["js/only-built-in.js"] = Encoding.UTF8.GetBytes("export {};"),
    };

    public LocalWebServerTests()
    {
        Directory.CreateDirectory(Path.Combine(Web, "js"));
        Directory.CreateDirectory(Path.Combine(Icons, "img", "vehicles"));
        Directory.CreateDirectory(Path.Combine(Data, "latest"));
        File.WriteAllText(Path.Combine(Web, "js", "app.js"), "export const disk = 1;");
        File.WriteAllText(Path.Combine(Web, "site.json"), """{"title":"Mine"}""");
        File.WriteAllText(Path.Combine(Icons, "img", "vehicles", "bus.png"), "png");
        File.WriteAllText(Path.Combine(Data, "latest", "meta.json"), """{"seq":1}""");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "outside");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private BridgeOptions Options(int port = 0, string? web = null) =>
        new() { WebPort = port, WebDir = web ?? Web, IconCacheDir = Icons, OutputDir = Data };

    private LocalWebServer Server(int port = 0, string? web = null) =>
        new(Microsoft.Extensions.Options.Options.Create(Options(port, web)), NullLogger<LocalWebServer>.Instance, BuiltIn);

    [Theory]
    [InlineData("/", "built-in:index.html")]
    [InlineData("/index.html", "built-in:index.html")]
    [InlineData("/js/app.js", "web/js/app.js")] // disk wins over the built-in copy
    [InlineData("/js/only-built-in.js", "built-in:js/only-built-in.js")]
    [InlineData("/JS/ONLY-BUILT-IN.JS", "built-in:JS/ONLY-BUILT-IN.JS")]
    [InlineData("/site.json", "web/site.json")]
    [InlineData("/img/vehicles/bus.png", "icons/img/vehicles/bus.png")]
    [InlineData("/data/latest/meta.json", "out/latest/meta.json")]
    [InlineData("/DATA/latest/meta.json", "out/latest/meta.json")]
    [InlineData("/data/index.html", null)] // /data/ is only the data
    [InlineData("/missing.js", null)]
    [InlineData("/../secret.txt", null)]
    [InlineData("/js/../../secret.txt", null)]
    [InlineData("/data/../../secret.txt", null)]
    [InlineData("/data/..\\..\\secret.txt", null)]
    [InlineData("/js/../index.html", null)]
    public void Resolves_url_paths_in_order_and_inside_their_roots(string url, string? expected)
    {
        var file = Server().Resolve(url);
        var actual = file is null ? null : file.BuiltIn is not null ? "built-in:" + file.Name : Path.GetRelativePath(_root, file.Name).Replace('\\', '/');
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_missing_web_folder_leaves_the_built_in_site()
    {
        var server = Server(web: Path.Combine(_root, "none"));
        Assert.Equal("", server.WebDir);
        Assert.Equal("index.html", server.Resolve("/")?.Name);
        Assert.Equal(Path.Combine(Icons, "img", "vehicles", "bus.png"), server.Resolve("/img/vehicles/bus.png")?.Name);
    }

    [Fact]
    public void The_exe_carries_the_whole_site_but_no_data_settings_or_game_artwork()
    {
        var server = new LocalWebServer(Microsoft.Extensions.Options.Options.Create(Options(web: "")), NullLogger<LocalWebServer>.Instance);
        var site = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "web"));
        var expected = new[] { "index.html", "config.js", "css/style.css" }
            .Concat(Directory.GetFiles(Path.Combine(site, "js")).Select(f => "js/" + Path.GetFileName(f)));
        foreach (var path in expected)
        {
            var file = server.Resolve("/" + path);
            Assert.True(file?.BuiltIn is not null, $"{path} is not built in");
            Assert.Equal(File.ReadAllBytes(Path.Combine(site, path.Replace('/', Path.DirectorySeparatorChar))), file!.BuiltIn);
        }
        foreach (var path in new[] { "site.json", "serve.ps1", "README.md", "site.example.json", "img/stations/vehicle_bus_front_32.png", "data/" })
            Assert.Null(server.Resolve("/" + path));
    }

    [Fact]
    public void Port_zero_turns_it_off()
    {
        using var server = Server(port: 0);
        server.Start();
        Assert.Equal(WebServerState.Off, server.State);
        Assert.Null(server.Url);
    }

    [Fact]
    public async Task Serves_the_site_and_the_data_without_caching()
    {
        using var server = Server(port: FreePort());
        server.Start();
        Assert.Equal(WebServerState.Running, server.State);

        using var http = new HttpClient { BaseAddress = new Uri(server.Url!) };
        using var page = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<!doctype html>built in", await page.Content.ReadAsStringAsync());

        using var script = await http.GetAsync("/js/app.js");
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal("export const disk = 1;", await script.Content.ReadAsStringAsync());

        using var meta = await http.GetAsync("/data/latest/meta.json");
        Assert.Equal("""{"seq":1}""", await meta.Content.ReadAsStringAsync());
        Assert.Equal("application/json", meta.Content.Headers.ContentType?.MediaType);
        Assert.True(meta.Headers.CacheControl?.NoStore);

        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/js/only-built-in.js"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(BuiltIn["js/only-built-in.js"].Length, head.Content.Headers.ContentLength);

        using var missing = await http.GetAsync("/nope.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var post = await http.PostAsync("/", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public void A_taken_port_is_reported()
    {
        using var first = Server(port: FreePort());
        first.Start();
        using var second = Server(port: first.Port);
        second.Start();
        Assert.Equal(WebServerState.PortTaken, second.State);
        Assert.Null(second.Url);
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
