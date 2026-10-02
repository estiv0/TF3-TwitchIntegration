namespace Tf3Bridge.Output;

/// <summary>
/// Destination for the assembled data. Paths are relative and use '/', e.g. "latest/dynamic.json",
/// so the same layout works for a local folder and for Azure Blob Storage.
/// </summary>
public interface IPublisher
{
    Task PublishAsync(string path, string json, CancellationToken ct);

    /// <summary>Binary content, e.g. "latest/terrain.png".</summary>
    Task PublishBytesAsync(string path, byte[] data, CancellationToken ct);
}
