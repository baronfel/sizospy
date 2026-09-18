using Sizospy.Reporting;
using Sizospy.Storage;
using Microsoft.Data.Sqlite;

namespace Sizospy.Core.Tests;

[TestClass]
public sealed class StorageAndReportingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SchemaUsesStrictTablesOnlyWhenSupported()
    {
        Assert.IsFalse(DatabaseWriter.GetSchemaSql(SqliteRuntime.StrictTablesVersion - 1)
            .Contains(" STRICT;", StringComparison.Ordinal));
        StringAssert.Contains(
            DatabaseWriter.GetSchemaSql(SqliteRuntime.StrictTablesVersion),
            " STRICT;");
    }

    [TestMethod]
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

            await DatabaseWriter.WriteAsync(database, model, false, TestContext.CancellationToken);
            var conflict = await Assert.ThrowsExactlyAsync<SizospyException>(
                () => DatabaseWriter.WriteAsync(database, model, false, TestContext.CancellationToken));
            Assert.AreEqual("output-conflict", conflict.Code);

            var service = new ReportService(database);
            var summary = await service.GetSummaryAsync(TestContext.CancellationToken);
            var rows = await service.GetMembersAsync(
                new ReportFilter(Limit: 10, Sort: ReportSort.Retained),
                TestContext.CancellationToken);

            Assert.AreEqual(2L, summary.NodeCount);
            Assert.AreEqual(150L, summary.AccountedSize);
            Assert.AreEqual(50L, summary.UnattributedSize);
            Assert.IsTrue(summary.GraphAvailable);
            Assert.AreEqual(150L, rows[0].RetainedSize);
            StringAssert.Contains(ReportFormatter.FormatSummary(summary, OutputFormat.Json), "\"nodeCount\": 2");
            Assert.IsTrue(ReportFormatter.FormatSummary(summary, OutputFormat.Csv).StartsWith("metric,value\n", StringComparison.Ordinal));
            StringAssert.Contains(ReportFormatter.FormatSummary(summary, OutputFormat.Table), "Accounted size:");
            StringAssert.Contains(ReportFormatter.FormatMembers(rows, OutputFormat.Csv), "RootMethod");

            var html = Path.Combine(directory, "report.html");
            await service.GenerateWebAsync(html, TestContext.CancellationToken);
            var content = await File.ReadAllTextAsync(html, TestContext.CancellationToken);
            StringAssert.Contains(content, "Ownership treemap");
            StringAssert.Contains(content, "Dominator icicle");
            StringAssert.Contains(content, "\"n\":\"Sample.Type.Root\"");
            StringAssert.Contains(content, "artifact:${n.nodeId}");
            Assert.IsFalse(content.Contains("https://", StringComparison.Ordinal));

            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND sql LIKE '% STRICT';";
                var strictTableCount = (long)(await command.ExecuteScalarAsync(TestContext.CancellationToken) ?? 0L);
                Assert.AreEqual(
                    SqliteRuntime.SupportsStrictTables(SqliteRuntime.VersionNumber),
                    strictTableCount > 0);

                command.CommandText = "UPDATE schema_info SET version = 999;";
                await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            }

            var mismatch = await Assert.ThrowsExactlyAsync<SizospyException>(
                () => service.GetSummaryAsync(TestContext.CancellationToken));
            Assert.AreEqual("schema-mismatch", mismatch.Code);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
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

            await DatabaseWriter.WriteAsync(database, model, false, TestContext.CancellationToken);
            var service = new ReportService(database);

            var summary = await service.GetSummaryAsync(TestContext.CancellationToken);
            var dominators = await service.GetDominatorsAsync(
                new ReportFilter(Limit: 10),
                TestContext.CancellationToken);

            Assert.IsFalse(summary.GraphAvailable);
            Assert.AreEqual(0, dominators.Count);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void CsvQuotingAndOrderingAreDeterministic()
    {
        var rows = new[]
        {
            new MemberReportRow(1, "n1", "compiler", "A,\"B\"", "Method", "Asm", "Ns", "T", "M", 2, 3, 1, 1.5, 1, 0, true, "test"),
        };

        var csv = ReportFormatter.FormatMembers(rows, OutputFormat.Csv);

        StringAssert.Contains(csv, "\"A,\"\"B\"\"\"");
        Assert.IsTrue(csv.EndsWith('\n'));
    }
}
