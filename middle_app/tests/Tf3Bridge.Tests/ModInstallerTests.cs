using System.Text;
using System.Text.Json;

namespace Tf3Bridge.Tests;

public sealed class ModInstallerTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "tf3bridge-mod-" + Guid.NewGuid().ToString("N"), "local");

    public void Dispose()
    {
        // junction najpierw osobno: rekurencyjne Delete nie usuwa samego linku
        if (Directory.Exists(Target) && new DirectoryInfo(Target).Attributes.HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(Target);
        var root = Path.GetDirectoryName(_local)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static Dictionary<string, byte[]> Files(params (string Path, string Text)[] extra)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["mod.json"] = Encoding.UTF8.GetBytes("""{ "modId": "test_mod" }"""),
            ["content/m/a.lua"] = Encoding.UTF8.GetBytes("return 1"),
        };
        foreach (var (p, t) in extra) files[p] = Encoding.UTF8.GetBytes(t);
        return files;
    }

    private string Target => Path.Combine(_local, "staging_area", "test_mod");

    [Fact]
    public void Installs_into_staging_area()
    {
        var r = ModInstaller.Sync(_local, Files(("content/m/b.lua", "return 2")));

        Assert.Equal(ModInstallStatus.Installed, r.Status);
        Assert.Equal(Target, r.ModDir);
        Assert.Equal("return 2", File.ReadAllText(Path.Combine(Target, "content", "m", "b.lua")));
        Assert.True(File.Exists(Path.Combine(Target, "mod.json")));
    }

    [Fact]
    public void Second_sync_is_up_to_date()
    {
        ModInstaller.Sync(_local, Files());

        var r = ModInstaller.Sync(_local, Files());

        Assert.Equal(ModInstallStatus.UpToDate, r.Status);
        Assert.Equal(0, r.ChangedFiles);
    }

    [Fact]
    public void Updates_changed_files_and_removes_stale_lua()
    {
        ModInstaller.Sync(_local, Files(("content/m/old.lua", "x")));
        var keep = Path.Combine(Target, "notes.txt");
        File.WriteAllText(keep, "user file");

        var r = ModInstaller.Sync(_local, Files(("content/m/a.lua", "return 42")));

        Assert.Equal(ModInstallStatus.Updated, r.Status);
        Assert.Equal(2, r.ChangedFiles);
        Assert.Equal("return 42", File.ReadAllText(Path.Combine(Target, "content", "m", "a.lua")));
        Assert.False(File.Exists(Path.Combine(Target, "content", "m", "old.lua")));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void Leaves_dev_junction_alone()
    {
        var repo = Path.Combine(Path.GetDirectoryName(_local)!, "repo");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        // junction (jak w lua_mod/README.md); symlink wymagałby uprawnień
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", $"/c mklink /J \"{Target}\" \"{repo}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();

        var r = ModInstaller.Sync(_local, Files());

        Assert.Equal(ModInstallStatus.DevCopy, r.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(repo));
    }

    // _content.json musi wymieniać dokładnie wbudowane pliki .lua (pack.ps1 -ContentOnly je odświeża).
    [Fact]
    public void Embedded_mod_is_complete()
    {
        var files = ModInstaller.EmbeddedFiles;
        Assert.Equal("estiv0_tf3export", ModInstaller.ReadModId(files));
        Assert.Contains("_metadata/modinfo.json", files.Keys);

        using var doc = JsonDocument.Parse(files["_content.json"]);
        var listed = doc.RootElement.GetProperty("files").EnumerateArray().Select(e => "content/" + e.GetString()).Order();
        var embedded = files.Keys.Where(k => k.StartsWith("content/") && k.EndsWith(".lua")).Order();
        Assert.Equal(embedded, listed);
    }
}
