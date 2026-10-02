using Tf3Bridge.Output;

namespace Tf3Bridge.Cloud;

/// <summary>Writes locally, then marks the path for upload by <see cref="AzureMirror"/>.</summary>
public sealed class MirroringPublisher(LocalFolderPublisher local, AzureMirror mirror) : IPublisher
{
    public async Task PublishAsync(string path, string json, CancellationToken ct)
    {
        await local.PublishAsync(path, json, ct);
        mirror.MarkDirty(path);
    }

    public async Task PublishBytesAsync(string path, byte[] data, CancellationToken ct)
    {
        await local.PublishBytesAsync(path, data, ct);
        mirror.MarkDirty(path);
    }
}
