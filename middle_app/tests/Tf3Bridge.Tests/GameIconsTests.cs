using System.Buffers.Binary;
using System.IO.Compression;
using Tf3Bridge.Output;

namespace Tf3Bridge.Tests;

public sealed class GameIconsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tf3bridge-icons-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "Transport Fever 3");
    private string Cache => Path.Combine(_root, "cache");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Uncompressed true-color TGA, top-left origin, BGRA.
    private static byte[] Tga(int w, int h, byte b, byte g, byte r, byte a)
    {
        var data = new byte[18 + w * h * 4];
        data[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)h);
        data[16] = 32;
        data[17] = 0x20;
        for (var i = 0; i < w * h; i++) (data[18 + i * 4], data[19 + i * 4], data[20 + i * 4], data[21 + i * 4]) = (b, g, r, a);
        return data;
    }

    private static void Zip(string path, params (string Entry, byte[] Data)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in files)
        {
            using var s = zip.CreateEntry(entry).Open();
            s.Write(data);
        }
    }

    private void FakeGame()
    {
        var content = Path.Combine(Game, "base", "content");
        Zip(Path.Combine(content, "gui.zip"),
            ("gui/hud/icons/vehicle_bus_front_32@2x.tga", Tga(2, 2, 255, 255, 255, 128)),
            ("gui/hud/icons/vehicle_bus_front_32.tga", Tga(1, 1, 0, 0, 0, 0)), // not @2x
            ("gui/hud/icons/sub/other@2x.tga", Tga(1, 1, 0, 0, 0, 0))); // in a subfolder
        Zip(Path.Combine(content, "cargos", "coal.zip"), ("coal/coal@2x.tga", Tga(3, 3, 10, 20, 30, 255)));
        Zip(Path.Combine(content, "cargos", "classes.zip"), ("classes/readme.txt", [1]));
        Zip(Path.Combine(content, "vehicle", "bus", "bus.zip"),
            ("res/models/bus_a_icon_small@2x.tga", Tga(1024, 2, 0, 0, 255, 255)),
            ("res/models/bad_icon_small@2x.tga", [1, 2, 3])); // broken: skipped
        Zip(Path.Combine(Game, "dlcs", "x", "content", "vehicle.zip"),
            ("res/models/bus_a_icon_small@2x.tga", Tga(4, 4, 0, 255, 0, 255)), // the base game's copy wins
            ("res/models/ship_icon_small@2x.tga", Tga(8, 4, 255, 0, 0, 255)));
    }

    [Fact]
    public void Extracts_the_icons_like_the_script_and_only_once()
    {
        FakeGame();

        Assert.Equal(4, GameIcons.Update(Game, Cache));

        var img = Path.Combine(Cache, "img");
        Assert.Equal(["vehicle_bus_front_32.png"], Directory.GetFiles(Path.Combine(img, "stations")).Select(Path.GetFileName));
        Assert.Equal(["coal.png"], Directory.GetFiles(Path.Combine(img, "resources")).Select(Path.GetFileName));
        Assert.Equal(["bus_a.png", "ship.png"], Directory.GetFiles(Path.Combine(img, "vehicles")).Select(Path.GetFileName).Order());
        var bus = File.ReadAllBytes(Path.Combine(img, "vehicles", "bus_a.png"));
        Assert.Equal(512, (bus[16] << 24) | (bus[17] << 16) | (bus[18] << 8) | bus[19]); // scaled down to 512 px wide
        Assert.False(Directory.Exists(Path.Combine(Cache, "img.new")));

        Assert.Null(GameIcons.Update(Game, Cache)); // nothing changed

        File.SetLastWriteTimeUtc(Path.Combine(Game, "base", "content", "gui.zip"), DateTime.UtcNow.AddMinutes(1)); // a game update
        Assert.Equal(4, GameIcons.Update(Game, Cache));
    }

    [Fact]
    public void Reads_rle_grayscale_and_bottom_up_tga()
    {
        // 2x2 grayscale RLE, bottom-left origin: a run of two 0x40 (bottom row), then two raw 0x80, 0xFF (top row)
        byte[] tga = [0, 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2, 0, 8, 0, 0x81, 0x40, 0x01, 0x80, 0xFF];
        var (w, h, rgba) = GameIcons.ReadTga(tga)!.Value;
        Assert.Equal((2, 2), (w, h));
        Assert.Equal<byte>([255, 255, 255, 0x80, 255, 255, 255, 0xFF, 255, 255, 255, 0x40, 255, 255, 255, 0x40], rgba);
        Assert.Null(GameIcons.ReadTga([0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 8, 0, 0])); // color-mapped: not supported
    }

    [Fact]
    public void Downscaling_ignores_the_color_of_transparent_pixels()
    {
        // red opaque | black fully transparent → one pixel: red, half transparent (no dark rim)
        byte[] rgba = [255, 0, 0, 255, 0, 0, 0, 0];
        var (w, h, result) = GameIcons.Downscale(2, 1, rgba, 1);
        Assert.Equal((1, 1), (w, h));
        Assert.Equal<byte>([255, 0, 0, 128], result);
    }

    [Fact]
    public void Finds_the_game_in_any_steam_library()
    {
        var steam = Path.Combine(_root, "Steam");
        var library = Path.Combine(_root, "Library");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Replace("\\", "\\\\")}}" }
                "1" { "path" "{{library.Replace("\\", "\\\\")}}" }
            }
            """);
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "TF3 custom", "base", "content"));
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_3493540.acf"), "\"AppState\" { \"installdir\" \"TF3 custom\" }");

        Assert.Equal(Path.Combine(library, "steamapps", "common", "TF3 custom"), SteamLocator.FindGameDir([steam]));
        Assert.Null(SteamLocator.FindGameDir([Path.Combine(_root, "nothing")]));
    }
}
