using Sizospy.Import;

namespace Sizospy.Core.Tests;

public sealed class ParserTests
{
    [Fact]
    public async Task DgmlUsesLabelsAsCompilerIdentitiesAndPreservesDirection()
    {
        var builder = new ImportBuilder();

        await DgmlParser.ParseAsync(Fixture("scan.dgml.xml"), builder, TestContext.Current.CancellationToken);

        var root = Assert.Single(builder.Nodes, n => n.CompilerIdentity == "RootMethod");
        var dependency = Assert.Single(builder.Nodes, n => n.CompilerIdentity == "DependencyMethod");
        Assert.True(root.IsRoot);
        Assert.Contains(builder.Edges, e =>
            e.SourceNodeId == root.Id &&
            e.TargetNodeId == dependency.Id &&
            e.Reason == "direct call");
        Assert.Equal("source-depends-on-target", builder.Metadata["dgml_edge_direction"]);
    }

    [Fact]
    public async Task MapParsesOpenEndedObjectKindsAndOptionalNames()
    {
        var builder = new ImportBuilder();
        builder.GetOrAddNode("RootMethod", "RootMethod", NodeKind.Method, 100, null, "mstat");

        await MapXmlParser.ParseAsync(Fixture("map.xml"), builder, TestContext.Current.CancellationToken);

        Assert.Equal(3, builder.Ranges.Count);
        Assert.Equal(182, builder.Nodes.Sum(n => n.SelfSize));
        var root = Assert.Single(builder.Nodes, n => n.CompilerIdentity == "RootMethod");
        Assert.Equal(0, root.SelfSize);
        Assert.Equal(2, builder.Edges.Count(e => e.SourceNodeId == root.Id && e.ReasonKind == "layout"));
        Assert.Contains(builder.Nodes, n => n.DisplayName == "RootMethod [MethodCode]" && n.SelfSize == 128);
        Assert.Contains(builder.Ranges, r =>
            r.Section == "GCInfo" &&
            r.Size == 12 &&
            r.ObjectName == "RootMethod" &&
            r.ContentHash == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.Empty(builder.Diagnostics);
    }

    [Fact]
    public async Task UnsupportedMstatVersionHasExplicitDiagnosticCode()
    {
        var error = await Assert.ThrowsAsync<SizospyException>(
            () => MstatParser.ParseAsync(
                typeof(ParserTests).Assembly.Location,
                new ImportBuilder(),
                TestContext.Current.CancellationToken));

        Assert.Equal("unsupported-mstat-version", error.Code);
        Assert.Contains("supports versions 2.0 through 2.2", error.Message);
    }

    [Fact]
    public async Task GeneratedMstatFixtureParsesRealIlDataStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sizospy-mstat-{Guid.NewGuid():N}.dll");
        try
        {
            MstatFixtureFactory.Write(path);
            var builder = new ImportBuilder();

            await MstatParser.ParseAsync(path, builder, TestContext.Current.CancellationToken);

            var blob = Assert.Single(builder.Nodes, n => n.DisplayName == "fixture blob");
            Assert.Equal("fixture blob", blob.DisplayName);
            Assert.Equal(NodeKind.RuntimeArtifact, blob.Kind);
            Assert.Equal(42, blob.SelfSize);
            Assert.Equal(42, builder.Nodes.Sum(n => n.SelfSize));
            Assert.Contains(builder.Nodes, n =>
                n.DisplayName == "Sample: fixture.resources" &&
                n.SelfSize == 0);
            var folded = Assert.Single(builder.Nodes, n => n.CompilerIdentity == "Sample.SampleType.Folded");
            var canonical = Assert.Single(builder.Nodes, n => n.CompilerIdentity == "Sample.SampleType.Canonical");
            Assert.Contains(builder.Edges, e =>
                e.SourceNodeId == folded.Id &&
                e.TargetNodeId == canonical.Id &&
                e.ReasonKind == "deduplication");
            Assert.Equal("2.2", builder.Metadata["mstat_version"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MalformedDgmlHasExplicitDiagnosticCode()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "<DirectedGraph><Node>", TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<SizospyException>(
                () => DgmlParser.ParseAsync(path, new ImportBuilder(), TestContext.Current.CancellationToken));
            Assert.Equal("malformed-dgml", error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MalformedMapHasExplicitDiagnosticCode()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "<ObjectNodes><MethodCode>", TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<SizospyException>(
                () => MapXmlParser.ParseAsync(path, new ImportBuilder(), TestContext.Current.CancellationToken));
            Assert.Equal("malformed-map", error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
