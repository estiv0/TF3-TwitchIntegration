using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tf3Bridge.Output;

namespace Tf3Bridge.Tests;

public sealed class LocalWebServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tf3bridge-web-" + Guid.NewGuid().ToString("N"));
    private string Web => Path.Combine(_root, "web");
    private string Data => Path.Combine(_root, "out");

    public LocalWebServerTests()
    {
        Directory.CreateDirectory(Path.Combine(Web, "js"));
        Directory.CreateDirectory(Path.Combine(Data, "latest"));
        File.WriteAllText(Path.Combine(Web, "index.html"), "<!doctype html>site");
        File.WriteAllText(Path.Combine(Web, "js", "app.js"), "export {};");
        File.WriteAllText(Path.Combine(Data, "latest", "meta.json"), """{"seq":1}""");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "outside");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalWebServer Server(int port = 0, string? web = null) =>
        new(Options.Create(new BridgeOptions { WebPort = port, WebDir = web ?? Web, OutputDir = Data }), NullLogger<LocalWebServer>.Instance);

    [Theory]
    [InlineData("/", "web/index.html")]
    [InlineData("/js/app.js", "web/js/app.js")]
    [InlineData("/js/", null)]
    [InlineData("/data/latest/meta.json", "out/latest/meta.json")]
    [InlineData("/DATA/latest/meta.json", "out/latest/meta.json")]
    [InlineData("/missing.js", null)]
    [InlineData("/../secret.txt", null)]
    [InlineData("/js/../../secret.txt", null)]
    [InlineData("/data/../../secret.txt", null)]
    [InlineData("/data/..\\..\\secret.txt", null)]
    public void Maps_url_paths_to_files_inside_their_root(string url, string? expected)
    {
        var file = Server().MapPath(url);
        Assert.Equal(expected is null ? null : Path.Combine(_root, expected.Replace('/', Path.DirectorySeparatorChar)), file);
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
    public void A_folder_without_index_html_is_reported()
    {
        using var server = Server(port: FreePort(), web: Data);
        server.Start();
        Assert.Equal(WebServerState.NoWebDir, server.State);
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
        Assert.Equal("<!doctype html>site", await page.Content.ReadAsStringAsync());

        using var meta = await http.GetAsync("/data/latest/meta.json");
        Assert.Equal("""{"seq":1}""", await meta.Content.ReadAsStringAsync());
        Assert.Equal("application/json", meta.Content.Headers.ContentType?.MediaType);
        Assert.True(meta.Headers.CacheControl?.NoStore);

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
