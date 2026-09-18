using Sizospy.Reporting;
using Sizospy.Storage;
using Microsoft.Data.Sqlite;

namespace Sizospy.Core.Tests;

public sealed class StorageAndReportingTests
{
    [Fact]
    public async Task DatabaseRoundTripProducesMetricsAndAllFormats()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sizospy-tests-{Guid.NewGuid():N}");
        var database = Path.Combine(directory, "sample.db");
        Directory.CreateDirectory(directory);
        try
        {
            var members = new[]
            {
                new LogicalMember(1, "m1", "Sample.Type.Root", NodeKind.Method, "Sample", "Sample", "Sample.Type", "Root"),
                new LogicalMember(2, "m2", "Sample.Type.Child", NodeKind.Method, "Sample", "Sample", "Sample.Type", "Child"),
            };
            var nodes = new[]
            {
                new SizeNode(1, "n1", "RootMethod", "Sample.Type.Root", NodeKind.Method, 100, 1, "test", true),
                new SizeNode(2, "n2", "ChildMethod", "Sample.Type.Child", NodeKind.Method, 50, 2, "test"),
            };
            var edges = new[] { new DependencyEdge(1, 1, 2, "direct call", "call", null, "test") };
            var model = new ImportModel(
                members,
                nodes,
                edges,
                [],
                [new ImportDiagnostic(DiagnosticSeverity.Info, "fixture", "fixture imported")],
                new Dictionary<string, string>
                {
                    ["dgml_edge_direction"] = "source-depends-on-target",
                    ["retained_size_semantics"] = "graph-model-estimate",
                },
                200);

            await DatabaseWriter.WriteAsync(database, model, false, TestContext.Current.CancellationToken);
            var conflict = await Assert.ThrowsAsync<SizospyException>(
                () => DatabaseWriter.WriteAsync(database, model, false, TestContext.Current.CancellationToken));
            Assert.Equal("output-conflict", conflict.Code);

            var service = new ReportService(database);
            var summary = await service.GetSummaryAsync(TestContext.Current.CancellationToken);
            var rows = await service.GetMembersAsync(
                new ReportFilter(Limit: 10, Sort: ReportSort.Retained),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, summary.NodeCount);
            Assert.Equal(150, summary.AccountedSize);
            Assert.Equal(50, summary.UnattributedSize);
            Assert.True(summary.GraphAvailable);
            Assert.Equal(150, rows[0].RetainedSize);
            Assert.Contains("\"nodeCount\": 2", ReportFormatter.FormatSummary(summary, OutputFormat.Json));
            Assert.StartsWith("metric,value\n", ReportFormatter.FormatSummary(summary, OutputFormat.Csv));
            Assert.Contains("Accounted size:", ReportFormatter.FormatSummary(summary, OutputFormat.Table));
            Assert.Contains("RootMethod", ReportFormatter.FormatMembers(rows, OutputFormat.Csv));

            var html = Path.Combine(directory, "report.html");
            await service.GenerateWebAsync(html, TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(html, TestContext.Current.CancellationToken);
            Assert.Contains("Ownership treemap", content);
            Assert.Contains("Dominator icicle", content);
            Assert.Contains("\"n\":\"Sample.Type.Root\"", content);
            Assert.Contains("artifact:${n.nodeId}", content);
            Assert.DoesNotContain("https://", content);

            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE schema_info SET version = 999;";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var mismatch = await Assert.ThrowsAsync<SizospyException>(
                () => service.GetSummaryAsync(TestContext.Current.CancellationToken));
            Assert.Equal("schema-mismatch", mismatch.Code);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NonDependencyEdgesDoNotEnableDominatorMetrics()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sizospy-tests-{Guid.NewGuid():N}");
        var database = Path.Combine(directory, "layout-only.db");
        Directory.CreateDirectory(directory);
        try
        {
            var nodes = new[]
            {
                new SizeNode(1, "n1", "Owner", "Owner", NodeKind.Method, 0, null, "mstat"),
                new SizeNode(2, "n2", "Artifact", "Artifact", NodeKind.CompilerArtifact, 50, null, "map"),
            };
            var model = new ImportModel(
                [],
                nodes,
                [new DependencyEdge(1, 1, 2, "emitted object", "layout", null, "map")],
                [],
                [],
                new Dictionary<string, string>(),
                null);

            await DatabaseWriter.WriteAsync(database, model, false, TestContext.Current.CancellationToken);
            var service = new ReportService(database);

            var summary = await service.GetSummaryAsync(TestContext.Current.CancellationToken);
            var dominators = await service.GetDominatorsAsync(
                new ReportFilter(Limit: 10),
                TestContext.Current.CancellationToken);

            Assert.False(summary.GraphAvailable);
            Assert.Empty(dominators);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CsvQuotingAndOrderingAreDeterministic()
    {
        var rows = new[]
        {
            new MemberReportRow(1, "n1", "compiler", "A,\"B\"", "Method", "Asm", "Ns", "T", "M", 2, 3, 1, 1.5, 1, 0, true, "test"),
        };

        var csv = ReportFormatter.FormatMembers(rows, OutputFormat.Csv);

        Assert.Contains("\"A,\"\"B\"\"\"", csv);
        Assert.EndsWith("\n", csv);
    }
}
