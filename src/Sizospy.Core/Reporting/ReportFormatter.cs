using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sizospy.Reporting;

/// <summary>Specifies a deterministic structured or human-readable report format.</summary>
public enum OutputFormat
{
    /// <summary>Human-readable fixed-column text.</summary>
    Table,
    /// <summary>Source-generated JSON.</summary>
    Json,
    /// <summary>RFC 4180-style comma-separated values.</summary>
    Csv,
}

/// <summary>Formats report models without performing console or file I/O.</summary>
public static class ReportFormatter
{
    /// <summary>Formats an import summary.</summary>
    /// <param name="summary">Summary to format.</param>
    /// <param name="format">Requested output format.</param>
    /// <returns>Deterministically formatted text.</returns>
    public static string FormatSummary(SummaryReport summary, OutputFormat format) => format switch
    {
        OutputFormat.Json => JsonSerializer.Serialize(summary, SizospyJsonContext.Default.SummaryReport),
        OutputFormat.Csv => FormatSummaryCsv(summary),
        OutputFormat.Table => FormatSummaryTable(summary),
        _ => throw new SizospyException($"Unsupported output format '{format}'.", "invalid-format"),
    };

    /// <summary>Formats member or dominator rows.</summary>
    /// <param name="rows">Rows to format in their existing order.</param>
    /// <param name="format">Requested output format.</param>
    /// <returns>Deterministically formatted text.</returns>
    public static string FormatMembers(IReadOnlyList<MemberReportRow> rows, OutputFormat format) => format switch
    {
        OutputFormat.Json => JsonSerializer.Serialize(rows, SizospyJsonContext.Default.IReadOnlyListMemberReportRow),
        OutputFormat.Csv => FormatMembersCsv(rows),
        OutputFormat.Table => FormatMembersTable(rows),
        _ => throw new SizospyException($"Unsupported output format '{format}'.", "invalid-format"),
    };

    private static string FormatSummaryTable(SummaryReport summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Schema:            {summary.SchemaVersion}");
        builder.AppendLine($"Nodes:             {summary.NodeCount.ToString("N0", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Edges:             {summary.EdgeCount.ToString("N0", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Logical members:   {summary.LogicalMemberCount.ToString("N0", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Accounted size:    {FormatBytes(summary.AccountedSize)}");
        builder.AppendLine($"Binary size:       {(summary.BinarySize is { } binary ? FormatBytes(binary) : "not supplied")}");
        builder.AppendLine($"Unattributed size: {(summary.UnattributedSize is { } overhead ? FormatBytes(overhead) : "not available")}");
        builder.AppendLine($"Graph metrics:     {(summary.GraphAvailable ? "available (estimate)" : "unavailable")}");
        if (summary.Kinds.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Kind                     Nodes       Self size");
            builder.AppendLine("------------------------  ----------  ------------");
            foreach (var kind in summary.Kinds)
            {
                builder.AppendLine($"{Truncate(kind.Kind, 24),-24}  {kind.Count,10:N0}  {FormatBytes(kind.SelfSize),12}");
            }
        }
        return builder.ToString();
    }

    private static string FormatSummaryCsv(SummaryReport summary)
    {
        var builder = new StringBuilder("metric,value\n");
        Csv(builder, "schema_version", summary.SchemaVersion);
        Csv(builder, "node_count", summary.NodeCount);
        Csv(builder, "edge_count", summary.EdgeCount);
        Csv(builder, "logical_member_count", summary.LogicalMemberCount);
        Csv(builder, "accounted_size", summary.AccountedSize);
        Csv(builder, "binary_size", summary.BinarySize);
        Csv(builder, "unattributed_size", summary.UnattributedSize);
        Csv(builder, "graph_available", summary.GraphAvailable);
        return builder.ToString();
    }

    private static string FormatMembersTable(IReadOnlyList<MemberReportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Self         Retained     Leverage  Kind              Ownership / artifact");
        builder.AppendLine("-----------  -----------  --------  ----------------  ----------------------------------------");
        foreach (var row in rows)
        {
            var retained = row.RetainedSize is { } retainedSize ? FormatBytes(retainedSize) : "-";
            var leverage = row.Leverage is { } ratio ? ratio.ToString("0.00", CultureInfo.InvariantCulture) + "x" : "-";
            var ownership = string.Join(" / ", new[] { row.Assembly, row.Namespace, row.Type, row.Member }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var label = string.IsNullOrWhiteSpace(ownership) ? row.DisplayName : $"{ownership} [{row.DisplayName}]";
            builder.AppendLine($"{FormatBytes(row.SelfSize),11}  {retained,11}  {leverage,8}  {Truncate(row.Kind, 16),-16}  {Truncate(label, 72)}");
        }
        return builder.ToString();
    }

    private static string FormatMembersCsv(IReadOnlyList<MemberReportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("node_id,stable_id,compiler_identity,display_name,kind,assembly,namespace,type,member,self_size,retained_size,marginal_retained_size,leverage,dominated_node_count,root_distance,is_root,provenance");
        foreach (var row in rows)
        {
            var values = new object?[]
            {
                row.NodeId, row.StableId, row.CompilerIdentity, row.DisplayName, row.Kind, row.Assembly, row.Namespace, row.Type,
                row.Member, row.SelfSize, row.RetainedSize, row.MarginalRetainedSize, row.Leverage,
                row.DominatedNodeCount, row.RootDistance, row.IsRoot, row.Provenance,
            };
            builder.AppendLine(string.Join(',', values.Select(QuoteCsv)));
        }
        return builder.ToString();
    }

    /// <summary>Formats a byte count using invariant binary units.</summary>
    /// <param name="bytes">Byte count.</param>
    /// <returns>A compact value in bytes, KiB, MiB, or GiB.</returns>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static void Csv(StringBuilder builder, string name, object? value) =>
        builder.Append(QuoteCsv(name)).Append(',').Append(QuoteCsv(value)).AppendLine();

    private static string QuoteCsv(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";
}
