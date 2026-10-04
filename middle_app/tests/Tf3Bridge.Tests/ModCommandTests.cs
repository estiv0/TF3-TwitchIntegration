using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tf3Bridge.Commands;

namespace Tf3Bridge.Tests;

public sealed class ModCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ModCommandQueue Queue() => new(_dir, NullLogger.Instance, _time);

    private string QueueText => File.ReadAllText(Path.Combine(_dir, "queue.lua"));

    // Tak jak zapisuje app.saveUserdata: JSON jako escapowany literał Lua.
    private void WriteResults(string json)
    {
        Directory.CreateDirectory(_dir);
        var escaped = json.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var path = Path.Combine(_dir, "results.lua");
        File.WriteAllText(path, $"function data()\r\nreturn {{ \r\n\t\tdone = \"x\",\r\n\t\tjson = \"{escaped}\",\r\n\t}}\r\nend");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000))); // nowy znacznik czasu
    }

    [Fact]
    public void Viewer_text_cannot_break_out_of_the_lua_string()
    {
        var lua = ModCommandLua.Str("a\"b\\c\nd\re\u0001f]]--}g Łódź");

        Assert.Equal("\"a\\\"b\\\\c\\nd\\re\\001f]]--}g Łódź\"", lua);
    }

    [Fact]
    public void Queue_file_has_the_saveUserdata_layout()
    {
        var c = new ModCommand("abc", "setName", DateTimeOffset.FromUnixTimeSeconds(1_759_590_000),
            new Dictionary<string, object> { ["entity"] = 1234L, ["name"] = "Kraków | widz" }, "test", "widz");

        var lua = ModCommandLua.ToLua([c]);

        Assert.StartsWith("-- ", lua);
        Assert.Contains("function data()\r\nreturn {\r\n\tcommands = {\r\n", lua);
        Assert.Contains("{ id = \"abc\", kind = \"setName\", created = 1759590000, entity = 1234, name = \"Kraków | widz\" },", lua);
        Assert.EndsWith("}\r\nend\r\n", lua);
        Assert.DoesNotContain("widz\", ", lua.Replace("| widz\"", "")); // źródło i użytkownik nie idą do gry
    }

    [Fact]
    public void Rejects_argument_names_that_are_not_identifiers()
    {
        var c = new ModCommand("abc", "setName", DateTimeOffset.UtcNow,
            new Dictionary<string, object> { ["x = 1, y"] = 1L }, "test", "-");

        Assert.Throws<ArgumentException>(() => ModCommandLua.ToLua([c]));
    }

    [Fact]
    public void Removes_a_command_from_the_queue_once_the_mod_reports_it()
    {
        var queue = Queue();
        var a = queue.Enqueue(ModCommand.SetName(1, "A | x", "test", "x"));
        var b = queue.Enqueue(ModCommand.SetName(2, "B | x", "test", "x"));
        var completed = new List<(ModCommand, ModCommandResult)>();
        queue.Completed += (c, r) => completed.Add((c, r));
        Assert.Contains(a.Id, QueueText);

        WriteResults($$"""{"results":[{"id":"old","status":"ok"},{"id":"{{a.Id}}","status":"ok"}]}""");
        queue.Poll();

        Assert.True(queue.ResultOf(a.Id)!.Ok);
        Assert.Null(queue.ResultOf(b.Id));
        Assert.Equal(1, queue.PendingCount);
        Assert.DoesNotContain(a.Id, QueueText);
        Assert.Contains(b.Id, QueueText);
        Assert.Equal(a, Assert.Single(completed).Item1);
    }

    [Fact]
    public void Reports_errors_from_the_mod()
    {
        var queue = Queue();
        var a = queue.Enqueue(ModCommand.SetName(1, "A", "test", "x"));

        WriteResults($$"""{"results":[{"id":"{{a.Id}}","status":"invalid","error":"entity not found"}]}""");
        queue.Poll();

        var r = queue.ResultOf(a.Id)!;
        Assert.Equal("invalid", r.Status);
        Assert.Equal("entity not found", r.Error);
    }

    [Fact]
    public void Gives_up_when_the_game_does_not_answer()
    {
        var queue = Queue();
        var a = queue.Enqueue(ModCommand.SetName(1, "A", "test", "x"));

        _time.Now = DateTimeOffset.UtcNow + ModCommandQueue.GiveUpAfter + TimeSpan.FromSeconds(1);
        queue.Poll();

        Assert.Equal("expired", queue.ResultOf(a.Id)!.Status);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Ignores_a_half_written_results_file()
    {
        var queue = Queue();
        var a = queue.Enqueue(ModCommand.SetName(1, "A", "test", "x"));
        File.WriteAllText(Path.Combine(_dir, "results.lua"), "function data()\r\nreturn {\r\n\t\tjson = \"{\\\"res");

        queue.Poll();

        Assert.Null(queue.ResultOf(a.Id));
        Assert.Equal(1, queue.PendingCount);
    }

    [Theory]
    [InlineData("Kraków", "Kraków", null)]
    [InlineData("Kraków | estivo", "Kraków", "estivo")]
    [InlineData("A | B | estivo", "A | B", "estivo")]
    [InlineData("Kraków |  ", "Kraków", null)]
    public void Splits_owner_like_the_site(string full, string name, string? owner) =>
        Assert.Equal((name, owner), OwnerName.Split(full));

    [Fact]
    public void Replaces_and_removes_the_owner()
    {
        Assert.Equal("Kraków | nowy", OwnerName.WithOwner("Kraków | stary", "nowy"));
        Assert.Equal("Kraków | ab", OwnerName.WithOwner("Kraków", " a|b\n"));
        Assert.Equal("Kraków", OwnerName.WithOwner("Kraków | stary", null));
        Assert.True(OwnerName.WithOwner("Kraków", new string('x', 500)).Length <= OwnerName.MaxLength);
    }

    [Fact]
    public void Reads_targets_from_dynamic_json()
    {
        using var doc = JsonDocument.Parse("""
            {"towns":[{"id":10,"name":"Zabrze | ala"},{"id":11,"name":"Bytom"}],
             "industries":[{"id":20,"name":"Kopalnia"}],
             "stations":[{"id":30,"name":"Dworzec","groups":[31,32]},{"id":40,"name":"Przystanek"}],
             "lines":[{"id":50,"name":"Linia 1"},{"id":51}]}
            """);

        var list = GameObjects.Parse(doc.RootElement);

        Assert.Equal(["Bytom", "Zabrze", "Kopalnia", "Dworzec", "Przystanek", "Linia 1"], list.Select(o => o.Name));
        Assert.Equal("ala", list[1].Owner);
        Assert.Equal(31, list.Single(o => o.Name == "Dworzec").Entity); // nazwa stacji siedzi w grupie
        Assert.Equal(40, list.Single(o => o.Name == "Przystanek").Entity);
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset? Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now ?? DateTimeOffset.UtcNow;
    }
}
