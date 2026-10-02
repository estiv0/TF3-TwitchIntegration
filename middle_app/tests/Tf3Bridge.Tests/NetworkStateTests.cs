using System.Text.Json.Nodes;
using Tf3Bridge.Processing;

namespace Tf3Bridge.Tests;

public class NetworkStateTests
{
    private static JsonObject Full(string session, long seq, params long[] ids) => new()
    {
        ["v"] = 5, ["kind"] = "network", ["session"] = session, ["seq"] = seq, ["gameTime"] = 100,
        ["fields"] = new JsonArray("id", "x0"),
        ["edges"] = new JsonArray(ids.Select(id => (JsonNode)new JsonArray(id, 0.0)).ToArray()),
    };

    private static JsonObject Delta(string session, long seq, long baseSeq, (long id, double x)[] changed, long[] removed) => new()
    {
        ["v"] = 5, ["kind"] = "networkDelta", ["session"] = session, ["seq"] = seq, ["base"] = baseSeq, ["gameTime"] = 200,
        ["fields"] = new JsonArray("id", "x0"),
        ["changed"] = new JsonArray(changed.Select(c => (JsonNode)new JsonArray(c.id, c.x)).ToArray()),
        ["removed"] = new JsonArray(removed.Select(r => (JsonNode)r).ToArray()),
    };

    [Fact]
    public void AppliesFullThenDelta()
    {
        var state = new NetworkState();
        state.ApplyFull(Full("s1", 1, 10, 20, 30));

        var result = state.ApplyDelta(Delta("s1", 2, 1, [(20, 5.5), (40, 1.0)], [30]));

        Assert.Equal(DeltaResult.Applied, result);
        Assert.Equal(2, state.Seq);
        Assert.Equal(200, state.GameTime);
        var edges = state.ToJson()["edges"]!.AsArray();
        Assert.Equal([10L, 20L, 40L], edges.Select(e => e![0]!.GetValue<long>()));
        Assert.Equal(5.5, edges[1]![1]!.GetValue<double>());
    }

    [Fact]
    public void DeltaWithoutFullSnapshotIsRejected()
    {
        var state = new NetworkState();

        Assert.Equal(DeltaResult.NoBase, state.ApplyDelta(Delta("s1", 2, 1, [(1, 0)], [])));
        Assert.False(state.HasData);
    }

    [Fact]
    public void DeltaFromOtherSessionIsRejected()
    {
        var state = new NetworkState();
        state.ApplyFull(Full("s1", 1, 10));

        Assert.Equal(DeltaResult.NoBase, state.ApplyDelta(Delta("s2", 2, 1, [(1, 0)], [])));
    }

    [Fact]
    public void OldDeltaIsStale()
    {
        var state = new NetworkState();
        state.ApplyFull(Full("s1", 3, 10));

        Assert.Equal(DeltaResult.Stale, state.ApplyDelta(Delta("s1", 2, 1, [(1, 0)], [])));
    }

    [Fact]
    public void DeltaAfterGapIsStillApplied()
    {
        var state = new NetworkState();
        state.ApplyFull(Full("s1", 1, 10));

        var result = state.ApplyDelta(Delta("s1", 3, 2, [(11, 0)], []));

        Assert.Equal(DeltaResult.AppliedAfterGap, result);
        Assert.Equal(3, state.Seq);
        Assert.Equal(2, state.EdgeCount);
    }
}
