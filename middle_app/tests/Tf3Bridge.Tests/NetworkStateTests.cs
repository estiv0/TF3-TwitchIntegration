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

    private static JsonArray Footprint(long id, string type, double x) =>
        new(id, type, new JsonArray(new JsonArray(x, 0.0, x + 10, 0.0, x + 10, 10.0)));

    [Fact]
    public void FootprintsFollowFullAndDelta()
    {
        var state = new NetworkState();
        var full = Full("s1", 1, 10);
        full["footprints"] = new JsonArray(Footprint(100, "RAIL", 0), Footprint(200, "WAREHOUSE", 50));
        state.ApplyFull(full);

        var delta = Delta("s1", 2, 1, [], []);
        delta["footprints"] = new JsonArray(Footprint(200, "WAREHOUSE", 60), Footprint(300, "DEPOT", 90));
        delta["footprintsRemoved"] = new JsonArray(100);
        state.ApplyDelta(delta);

        var footprints = state.ToJson()["footprints"]!.AsArray();
        Assert.Equal([200L, 300L], footprints.Select(f => f![0]!.GetValue<long>()));
        Assert.Equal(60.0, footprints[0]![2]![0]![0]!.GetValue<double>());
        Assert.Equal(1, state.EdgeCount);
    }

    [Fact]
    public void RoutesFollowFullAndDelta()
    {
        var state = new NetworkState();
        var full = Full("s1", 1, 10);
        full["routes"] = new JsonArray(new JsonArray(7L, new JsonArray(new JsonArray(10L, -11L), false)));
        state.ApplyFull(full);

        // a routes-only delta (no edge changes), as the mod writes after reading the vehicles' paths
        var delta = Delta("s1", 2, 1, [], []);
        delta["routes"] = new JsonArray(new JsonArray(8L, new JsonArray(new JsonArray(12L))));
        delta["routesRemoved"] = new JsonArray(7L);
        Assert.Equal(DeltaResult.Applied, state.ApplyDelta(delta));

        var routes = state.ToJson()["routes"]!.AsArray();
        Assert.Equal([8L], routes.Select(r => r![0]!.GetValue<long>()));
        Assert.Equal(1, state.EdgeCount);
    }

    [Fact]
    public void SnapshotWithoutFootprintsHasEmptyList()
    {
        var state = new NetworkState();
        state.ApplyFull(Full("s1", 1, 10));

        Assert.Equal(0, state.FootprintCount);
        Assert.Empty(state.ToJson()["footprints"]!.AsArray());
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
