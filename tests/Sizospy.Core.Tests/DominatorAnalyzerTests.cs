using Sizospy.Graph;

namespace Sizospy.Core.Tests;

public sealed class DominatorAnalyzerTests
{
    [Fact]
    public void ChainRetainsEntireSuffix()
    {
        var metrics = Analyze(
            Nodes((1, 10, true), (2, 20, false), (3, 30, false)),
            Edges((1, 2), (2, 3)));

        Assert.Equal(60, metrics[1].RetainedSize);
        Assert.Equal(50, metrics[2].RetainedSize);
        Assert.Equal(30, metrics[3].RetainedSize);
        Assert.Null(metrics[1].ImmediateDominatorId);
        Assert.Equal(1, metrics[2].ImmediateDominatorId);
        Assert.Equal(2, metrics[3].ImmediateDominatorId);
        Assert.Equal(2, metrics[1].DominatedNodeCount);
    }

    [Fact]
    public void DiamondJoinIsDominatedByBranchPoint()
    {
        var metrics = Analyze(
            Nodes((1, 1, true), (2, 2, false), (3, 3, false), (4, 4, false)),
            Edges((1, 2), (1, 3), (2, 4), (3, 4)));

        Assert.Equal(1, metrics[4].ImmediateDominatorId);
        Assert.Equal(10, metrics[1].RetainedSize);
        Assert.Equal(2, metrics[2].RetainedSize);
        Assert.Equal(3, metrics[3].RetainedSize);
        Assert.Equal(4, metrics[4].RetainedSize);
    }

    [Fact]
    public void MultipleRootsHaveNoPersistedSuperRootDominator()
    {
        var metrics = Analyze(
            Nodes((1, 5, true), (2, 7, true), (3, 11, false), (4, 13, false)),
            Edges((1, 3), (2, 4)));

        Assert.Null(metrics[1].ImmediateDominatorId);
        Assert.Null(metrics[2].ImmediateDominatorId);
        Assert.Equal(16, metrics[1].RetainedSize);
        Assert.Equal(20, metrics[2].RetainedSize);
        Assert.Equal(0, metrics[1].RootDistance);
    }

    [Fact]
    public void CycleComputesStableImmediateDominators()
    {
        var metrics = Analyze(
            Nodes((1, 1, true), (2, 2, false), (3, 3, false)),
            Edges((1, 2), (2, 3), (3, 2)));

        Assert.Equal(1, metrics[2].ImmediateDominatorId);
        Assert.Equal(2, metrics[3].ImmediateDominatorId);
        Assert.Equal(5, metrics[2].RetainedSize);
    }

    [Fact]
    public void DisconnectedNodesAreConservativelyAttachedToSuperRoot()
    {
        var metrics = Analyze(
            Nodes((1, 1, true), (2, 2, false), (3, 3, false)),
            Edges((2, 3)));

        Assert.True(metrics[1].GraphReachable);
        Assert.False(metrics[2].GraphReachable);
        Assert.False(metrics[3].GraphReachable);
        Assert.Null(metrics[2].ImmediateDominatorId);
        Assert.Null(metrics[3].ImmediateDominatorId);
        Assert.Equal(2, metrics[2].RetainedSize);
        Assert.Equal(3, metrics[3].RetainedSize);
    }

    [Fact]
    public void ZeroSizeNodesHaveNullLeverage()
    {
        var metrics = Analyze(
            Nodes((1, 0, true), (2, 10, false)),
            Edges((1, 2)));

        Assert.Null(metrics[1].Leverage);
        Assert.Equal(10, metrics[1].RetainedSize);
        Assert.Equal(10, metrics[1].MarginalRetainedSize);
    }

    [Fact]
    public void SharedNodePhysicalSizeIsAggregatedOnce()
    {
        var metrics = Analyze(
            Nodes((1, 10, true), (2, 20, false), (3, 30, false), (4, 40, false)),
            Edges((1, 2), (1, 3), (2, 4), (3, 4)));

        Assert.Equal(100, metrics[1].RetainedSize);
    }

    private static IReadOnlyDictionary<long, DominatorMetric> Analyze(
        IReadOnlyList<SizeNode> nodes,
        IReadOnlyList<DependencyEdge> edges) =>
        DominatorAnalyzer.Analyze(nodes, edges);

    private static IReadOnlyList<SizeNode> Nodes(params (long Id, long Size, bool Root)[] values) =>
        values.Select(v => new SizeNode(v.Id, $"n{v.Id}", $"n{v.Id}", $"node {v.Id}", NodeKind.Method, v.Size, null, "test", v.Root)).ToArray();

    private static IReadOnlyList<DependencyEdge> Edges(params (long Source, long Target)[] values) =>
        values.Select((v, i) => new DependencyEdge(i + 1, v.Source, v.Target, "test", null, null, "test")).ToArray();
}
