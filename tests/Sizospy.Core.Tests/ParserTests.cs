using Sizospy.Import;

namespace Sizospy.Core.Tests;

[TestClass]
public sealed class ParserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DgmlUsesLabelsAsCompilerIdentitiesAndPreservesDirection()
    {
        var builder = new ImportBuilder();

        await DgmlParser.ParseAsync(Fixture("scan.dgml.xml"), builder, TestContext.CancellationToken);

        var root = Single(builder.Nodes, n => n.CompilerIdentity == "RootMethod");
        var dependency = Single(builder.Nodes, n => n.CompilerIdentity == "DependencyMethod");
        Assert.IsTrue(root.IsRoot);
        Assert.IsTrue(builder.Edges.Any(e =>
            e.SourceNodeId == root.Id &&
            e.TargetNodeId == dependency.Id &&
            e.Reason == "direct call"));
        Assert.AreEqual("source-depends-on-target", builder.Metadata["dgml_edge_direction"]);
    }

    [TestMethod]
    public async Task MapParsesOpenEndedObjectKindsAndOptionalNames()
    {
        var builder = new ImportBuilder();
        builder.GetOrAddNode("RootMethod", "RootMethod", NodeKind.Method, 100, null, "mstat");

        await MapXmlParser.ParseAsync(Fixture("map.xml"), builder, TestContext.CancellationToken);

        Assert.AreEqual(3, builder.Ranges.Count);
        Assert.AreEqual(182L, builder.Nodes.Sum(n => n.SelfSize));
        var root = Single(builder.Nodes, n => n.CompilerIdentity == "RootMethod");
        Assert.AreEqual(0L, root.SelfSize);
        Assert.AreEqual(2, builder.Edges.Count(e => e.SourceNodeId == root.Id && e.ReasonKind == "layout"));
        Assert.IsTrue(builder.Nodes.Any(n => n.DisplayName == "RootMethod [MethodCode]" && n.SelfSize == 128));
        Assert.IsTrue(builder.Ranges.Any(r =>
            r.Section == "GCInfo" &&
            r.Size == 12 &&
            r.ObjectName == "RootMethod" &&
            r.ContentHash == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.AreEqual(0, builder.Diagnostics.Count);
    }

    [TestMethod]
    public async Task UnsupportedMstatVersionHasExplicitDiagnosticCode()
    {
        var error = await Assert.ThrowsExactlyAsync<SizospyException>(
            () => MstatParser.ParseAsync(
                typeof(ParserTests).Assembly.Location,
                new ImportBuilder(),
                TestContext.CancellationToken));

        Assert.AreEqual("unsupported-mstat-version", error.Code);
        StringAssert.Contains(error.Message, "supports versions 2.0 through 2.2");
    }

    [TestMethod]
    public async Task GeneratedMstatFixtureParsesRealIlDataStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sizospy-mstat-{Guid.NewGuid():N}.dll");
        try
        {
            MstatFixtureFactory.Write(path);
            var builder = new ImportBuilder();

            await MstatParser.ParseAsync(path, builder, TestContext.CancellationToken);

            var blob = Single(builder.Nodes, n => n.DisplayName == "fixture blob");
            Assert.AreEqual("fixture blob", blob.DisplayName);
            Assert.AreEqual(NodeKind.RuntimeArtifact, blob.Kind);
            Assert.AreEqual(42L, blob.SelfSize);
            Assert.AreEqual(42L, builder.Nodes.Sum(n => n.SelfSize));
            Assert.IsTrue(builder.Nodes.Any(n =>
                n.DisplayName == "Sample: fixture.resources" &&
                n.SelfSize == 0));
            var folded = Single(builder.Nodes, n => n.CompilerIdentity == "Sample.SampleType.Folded");
            var canonical = Single(builder.Nodes, n => n.CompilerIdentity == "Sample.SampleType.Canonical");
            Assert.IsTrue(builder.Edges.Any(e =>
                e.SourceNodeId == folded.Id &&
                e.TargetNodeId == canonical.Id &&
                e.ReasonKind == "deduplication"));
            Assert.AreEqual("2.2", builder.Metadata["mstat_version"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task MalformedDgmlHasExplicitDiagnosticCode()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "<DirectedGraph><Node>", TestContext.CancellationToken);
            var error = await Assert.ThrowsExactlyAsync<SizospyException>(
                () => DgmlParser.ParseAsync(path, new ImportBuilder(), TestContext.CancellationToken));
            Assert.AreEqual("malformed-dgml", error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task MalformedMapHasExplicitDiagnosticCode()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "<ObjectNodes><MethodCode>", TestContext.CancellationToken);
            var error = await Assert.ThrowsExactlyAsync<SizospyException>(
                () => MapXmlParser.ParseAsync(path, new ImportBuilder(), TestContext.CancellationToken));
            Assert.AreEqual("malformed-map", error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static T Single<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        var matches = values.Where(predicate).ToArray();
        Assert.AreEqual(1, matches.Length);
        return matches[0];
    }
}
