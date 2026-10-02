using System.Text;
using Microsoft.Extensions.Options;

namespace Tf3Bridge.Output;

/// <summary>Writes to OutputDir atomically (temp file + move), so readers never see half a file.</summary>
public sealed class LocalFolderPublisher(IOptions<BridgeOptions> options) : IPublisher
{
    private readonly string _root = Path.GetFullPath(options.Value.OutputDir);

    public Task PublishAsync(string path, string json, CancellationToken ct) =>
        PublishBytesAsync(path, new UTF8Encoding(false).GetBytes(json), ct);

    public async Task PublishBytesAsync(string path, byte[] data, CancellationToken ct)
    {
        var target = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var temp = target + ".tmp";
        await File.WriteAllBytesAsync(temp, data, ct);
        File.Move(temp, target, overwrite: true);
    }
}
