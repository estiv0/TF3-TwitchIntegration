using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tf3Bridge.Output;
using Tf3Bridge.Processing;

namespace Tf3Bridge.Tests;

public sealed class ExportProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tf3bridge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    private readonly string _output;
    private readonly ExportProcessor _processor;

    public ExportProcessorTests()
    {
        _input = Path.Combine(_root, "in");
        _output = Path.Combine(_root, "out");
        Directory.CreateDirectory(_input);

        var options = Options.Create(new BridgeOptions { InputDir = _input, OutputDir = _output });
        _processor = new ExportProcessor(options, new LocalFolderPublisher(options), NullLogger<ExportProcessor>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Writes a file the way app.saveUserdata does: JSON as an escaped Lua string literal.</summary>
    private void WriteGameFile(string name, string json, DateTime? lastWriteUtc = null)
    {
        var literal = json.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var path = Path.Combine(_input, name + ".lua");
        File.WriteAllText(path, $"function data()\r\nreturn {{ \r\n\t\tjson = \"{literal}\",\r\n\t}}\r\nend\r\n", new UTF8Encoding(false));
        if (lastWriteUtc is not null) File.SetLastWriteTimeUtc(path, lastWriteUtc.Value);
    }

    private JsonObject ReadOutput(string path) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(_output, path)))!.AsObject();

    [Fact]
    public async Task AssemblesNetworkPublishesNewestDynamicAndCleansUp()
    {
        WriteGameFile("network_aa_000001",
            """{"v":5,"kind":"network","session":"aa","seq":1,"gameTime":1,"fields":["id","x0"],"edges":[[1,0],[2,0]]}""");
        WriteGameFile("networkDelta_aa_000002",
            """{"v":5,"kind":"networkDelta","session":"aa","seq":2,"base":1,"gameTime":2,"fields":["id","x0"],"changed":[[3,1.5]],"removed":[1]}""");
        WriteGameFile("dynamic_aa_000001", """{"kind":"dynamic","session":"aa","seq":1,"gameTime":5,"towns":[]}""");
        WriteGameFile("dynamic_aa_000002", """{"kind":"dynamic","session":"aa","seq":2,"gameTime":6,"date":{"year":1918},"towns":[{"name":"Bochnia"}]}""");

        await _processor.ProcessOnceAsync(CancellationToken.None);

        var network = ReadOutput(ExportProcessor.NetworkPath);
        Assert.Equal(2, network["seq"]!.GetValue<long>());
        Assert.Equal([2L, 3L], network["edges"]!.AsArray().Select(e => e![0]!.GetValue<long>()));

        var dynamic = ReadOutput(ExportProcessor.DynamicPath);
        Assert.Equal(2, dynamic["seq"]!.GetValue<long>());
        Assert.Equal("Bochnia", dynamic["towns"]![0]!["name"]!.GetValue<string>());

        var meta = ReadOutput(ExportProcessor.MetaPath);
        Assert.Equal("aa", meta["session"]!.GetValue<string>());
        Assert.Equal(2, meta["dynamicSeq"]!.GetValue<long>());
        Assert.Equal(2, meta["networkSeq"]!.GetValue<long>());
        Assert.Equal(1918, meta["date"]!["year"]!.GetValue<int>());

        Assert.Empty(Directory.GetFiles(_input, "*.lua"));
    }

    [Fact]
    public async Task RetiresOlderSessions()
    {
        var old = DateTime.UtcNow.AddMinutes(-5);
        WriteGameFile("dynamic_0a_000007", """{"kind":"dynamic","session":"0a","seq":7}""", old);
        WriteGameFile("dynamic_0b_000001", """{"kind":"dynamic","session":"0b","seq":1}""");

        await _processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal("0b", ReadOutput(ExportProcessor.MetaPath)["session"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(_input, "*.lua"));
    }

    [Fact]
    public async Task LeavesIncompleteFileForNextPass()
    {
        File.WriteAllText(Path.Combine(_input, "dynamic_aa_000001.lua"), "function data()\r\nreturn { \r\n\t\tjson = \"{\\\"kin");

        await _processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Single(Directory.GetFiles(_input, "*.lua"));
        Assert.False(File.Exists(Path.Combine(_output, ExportProcessor.DynamicPath)));
    }

    [Fact]
    public async Task IgnoresUnrelatedFiles()
    {
        WriteGameFile("bench_A", """{"x":1}""");

        await _processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Single(Directory.GetFiles(_input, "*.lua"));
    }
}
