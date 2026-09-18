using Sizospy.Graph;

namespace Sizospy.Core.Tests;

[TestClass]
public sealed class DominatorAnalyzerTests
{
    [TestMethod]
    public void ChainRetainsEntireSuffix()
    {
        var metrics = Analyze(
            Nodes((1, 10, true), (2, 20, false), (3, 30, false)),
            Edges((1, 2), (2, 3)));

        Assert.AreEqual(60L, metrics[1].RetainedSize);
        Assert.AreEqual(50L, metrics[2].RetainedSize);
        Assert.AreEqual(30L, metrics[3].RetainedSize);
        Assert.IsNull(metrics[1].ImmediateDominatorId);
        Assert.AreEqual(1L, metrics[2].ImmediateDominatorId);
        Assert.AreEqual(2L, metrics[3].ImmediateDominatorId);
        Assert.AreEqual(2, metrics[1].DominatedNodeCount);
    }

    [TestMethod]
    public void DiamondJoinIsDominatedByBranchPoint()
    {
        var metrics = Analyze(
            Nodes((1, 1, true), (2, 2, false), (3, 3, false), (4, 4, false)),
            Edges((1, 2), (1, 3), (2, 4), (3, 4)));

        Assert.AreEqual(1L, metrics[4].ImmediateDominatorId);
        Assert.AreEqual(10L, metrics[1].RetainedSize);
        Assert.AreEqual(2L, metrics[2].RetainedSize);
        Assert.AreEqual(3L, metrics[3].RetainedSize);
        Assert.AreEqual(4L, metrics[4].RetainedSize);
    }

    [TestMethod]
    public void MultipleRootsHaveNoPersistedSuperRootDominator()
    {
        var metrics = Analyze(
            Nodes((1, 5, true), (2, 7, true), (3, 11, false), (4, 13, false)),
            Edges((1, 3), (2, 4)));

        Assert.IsNull(metrics[1].ImmediateDominatorId);
        Assert.IsNull(metrics[2].ImmediateDominatorId);
        Assert.AreEqual(16L, metrics[1].RetainedSize);
        Assert.AreEqual(20L, metrics[2].RetainedSize);
        Assert.AreEqual(0, metrics[1].RootDistance);
    }

    [TestMethod]
    public void CycleComputesStableImmediateDominators()
    {
        var metrics = Analyze(
            Nodes((1, 1, true), (2, 2, false), (3, 3, false)),
            Edges((1, 2), (2, 3), (3, 2)));

        Assert.AreEqual(1L, metrics[2].ImmediateDominatorId);
        Assert.AreEqual(2L, metrics[3].ImmediateDominatorId);
        Assert.AreEqual(5L, metrics[2].RetainedSize);
    }

    [TestMethod]
    public void DisconnectedNodesAreConservativelyAttachedToSuperRoot()
    {
        var metrics = Analyze(
            Nodes((1, 1, true), (2, 2, false), (3, 3, false)),
            Edges((2, 3)));

        Assert.IsTrue(metrics[1].GraphReachable);
        Assert.IsFalse(metrics[2].GraphReachable);
        Assert.IsFalse(metrics[3].GraphReachable);
        Assert.IsNull(metrics[2].ImmediateDominatorId);
        Assert.IsNull(metrics[3].ImmediateDominatorId);
        Assert.AreEqual(2L, metrics[2].RetainedSize);
        Assert.AreEqual(3L, metrics[3].RetainedSize);
    }

    [TestMethod]
    public void ZeroSizeNodesHaveNullLeverage()
    {
        var metrics = Analyze(
            Nodes((1, 0, true), (2, 10, false)),
            Edges((1, 2)));

        Assert.IsNull(metrics[1].Leverage);
        Assert.AreEqual(10L, metrics[1].RetainedSize);
        Assert.AreEqual(10L, metrics[1].MarginalRetainedSize);
    }

    [TestMethod]
    public void SharedNodePhysicalSizeIsAggregatedOnce()
    {
        var metrics = Analyze(
            Nodes((1, 10, true), (2, 20, false), (3, 30, false), (4, 40, false)),
            Edges((1, 2), (1, 3), (2, 4), (3, 4)));

        Assert.AreEqual(100L, metrics[1].RetainedSize);
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
