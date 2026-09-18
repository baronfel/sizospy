using System.Globalization;
using Microsoft.Data.Sqlite;
using Sizospy.Storage;

namespace Sizospy.Reporting;

internal sealed class ReportRepository(string databasePath)
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);

    public async Task<SummaryReport> GetSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var schemaVersion = await ScalarLongAsync(connection, "SELECT version FROM schema_info LIMIT 1;", cancellationToken);
        if (schemaVersion != DatabaseWriter.SchemaVersion)
        {
            throw new SizospyException(
                $"Database schema version {schemaVersion} is not supported; expected {DatabaseWriter.SchemaVersion}.",
                "schema-mismatch");
        }

        var metadata = await ReadMetadataAsync(connection, cancellationToken);
        var kinds = new List<KindSummary>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT kind, COUNT(*), COALESCE(SUM(self_size), 0)
                FROM nodes
                GROUP BY kind
                ORDER BY SUM(self_size) DESC, kind;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                kinds.Add(new KindSummary(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
            }
        }

        var diagnostics = new List<ImportDiagnostic>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT severity, code, message, source, line
                FROM import_diagnostics
                ORDER BY id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                diagnostics.Add(new ImportDiagnostic(
                    Enum.Parse<DiagnosticSeverity>(reader.GetString(0), true),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4)));
            }
        }

        return new SummaryReport(
            (int)schemaVersion,
            (int)await ScalarLongAsync(connection, "SELECT COUNT(*) FROM nodes;", cancellationToken),
            (int)await ScalarLongAsync(connection, "SELECT COUNT(*) FROM edges;", cancellationToken),
            (int)await ScalarLongAsync(connection, "SELECT COUNT(*) FROM logical_members;", cancellationToken),
            ParseLong(metadata, "accounted_size") ?? 0,
            ParseLong(metadata, "binary_size"),
            ParseLong(metadata, "unattributed_size"),
            bool.TryParse(metadata.GetValueOrDefault("graph_available"), out var graph) && graph,
            string.Equals(metadata.GetValueOrDefault("retained_size_semantics"), "graph-model-estimate", StringComparison.Ordinal),
            kinds,
            diagnostics);
    }

    public async Task<IReadOnlyList<MemberReportRow>> GetMembersAsync(
        ReportFilter filter,
        bool dominatorsOnly,
        CancellationToken cancellationToken)
    {
        if (filter.Limit is < 1 or > 100_000)
        {
            throw new SizospyException("Report limit must be between 1 and 100000.", "invalid-limit");
        }

        await using var connection = await OpenAsync(cancellationToken);
        var conditions = new List<string>();
        await using var command = connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            conditions.Add("(n.display_name LIKE $name ESCAPE '\\' OR n.compiler_identity LIKE $name ESCAPE '\\')");
            command.Parameters.AddWithValue("$name", $"%{EscapeLike(filter.Name)}%");
        }
        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            conditions.Add("n.kind = $kind COLLATE NOCASE");
            command.Parameters.AddWithValue("$kind", filter.Kind);
        }
        if (!string.IsNullOrWhiteSpace(filter.Assembly))
        {
            conditions.Add("lm.assembly_name = $assembly COLLATE NOCASE");
            command.Parameters.AddWithValue("$assembly", filter.Assembly);
        }
        if (!string.IsNullOrWhiteSpace(filter.Namespace))
        {
            conditions.Add("lm.namespace_name = $namespace COLLATE NOCASE");
            command.Parameters.AddWithValue("$namespace", filter.Namespace);
        }
        if (dominatorsOnly)
        {
            conditions.Add("d.node_id IS NOT NULL");
        }

        var order = filter.Sort switch
        {
            ReportSort.Self => "n.self_size DESC, n.display_name COLLATE NOCASE, n.id",
            ReportSort.Retained => "COALESCE(d.retained_size, -1) DESC, n.self_size DESC, n.display_name COLLATE NOCASE, n.id",
            ReportSort.Leverage => "COALESCE(d.leverage, -1) DESC, COALESCE(d.retained_size, -1) DESC, n.id",
            ReportSort.Name => "n.display_name COLLATE NOCASE, n.id",
            _ => throw new SizospyException($"Unsupported sort '{filter.Sort}'.", "invalid-sort"),
        };
        command.CommandText = $$"""
            SELECT
                n.id, n.stable_id, n.compiler_identity, n.display_name, n.kind,
                lm.assembly_name, lm.namespace_name, lm.type_name, lm.member_name,
                n.self_size, d.retained_size, d.marginal_retained_size, d.leverage,
                d.dominated_node_count, d.root_distance, n.is_root, n.provenance
            FROM nodes n
            LEFT JOIN logical_members lm ON lm.id = n.logical_member_id
            LEFT JOIN dominators d ON d.node_id = n.id
            {{(conditions.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", conditions)}")}}
            ORDER BY {{order}}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", filter.Limit);

        var rows = new List<MemberReportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    public async Task<WebReportModel> GetWebModelAsync(CancellationToken cancellationToken)
    {
        var summary = await GetSummaryAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var nodes = new List<WebNode>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    n.id, n.display_name, n.kind,
                    lm.assembly_name, lm.namespace_name, lm.type_name, lm.member_name,
                    n.self_size, d.retained_size, d.marginal_retained_size, d.leverage,
                    d.dominated_node_count, d.root_distance
                FROM nodes n
                LEFT JOIN logical_members lm ON lm.id = n.logical_member_id
                LEFT JOIN dominators d ON d.node_id = n.id
                WHERE n.self_size > 0 OR d.retained_size > 0 OR n.is_root = 1
                ORDER BY n.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                nodes.Add(new WebNode(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.IsDBNull(12) ? null : reader.GetInt32(12)));
            }
        }

        var edges = new List<WebEdge>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT source_node_id, target_node_id, reason, reason_kind, conditional_group
                FROM edges e
                JOIN nodes source ON source.id = e.source_node_id
                JOIN nodes target ON target.id = e.target_node_id
                LEFT JOIN dominators source_dominator ON source_dominator.node_id = source.id
                LEFT JOIN dominators target_dominator ON target_dominator.node_id = target.id
                WHERE
                    (source.self_size > 0 OR source_dominator.retained_size > 0 OR source.is_root = 1)
                    AND
                    (target.self_size > 0 OR target_dominator.retained_size > 0 OR target.is_root = 1)
                ORDER BY e.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add(new WebEdge(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var dominators = new List<WebDominator>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT node_id, immediate_dominator_id
                FROM dominators d
                JOIN nodes n ON n.id = d.node_id
                WHERE n.self_size > 0 OR d.retained_size > 0 OR n.is_root = 1
                ORDER BY d.node_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dominators.Add(new WebDominator(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1)));
            }
        }

        return new WebReportModel(summary, nodes, edges, dominators);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            throw new SizospyException($"Database '{_databasePath}' does not exist.", "database-not-found");
        }

        try
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                ForeignKeys = true,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch (SqliteException ex)
        {
            throw new SizospyException($"Unable to open database '{_databasePath}': {ex.Message}", "database-read", ex);
        }
    }

    private static MemberReportRow ReadRow(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetInt64(11),
        reader.IsDBNull(12) ? null : reader.GetDouble(12),
        reader.IsDBNull(13) ? null : reader.GetInt32(13),
        reader.IsDBNull(14) ? null : reader.GetInt32(14),
        reader.GetInt32(15) != 0,
        reader.GetString(16));

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<string, string>> ReadMetadataAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM import_metadata ORDER BY key;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0), reader.GetString(1));
        }
        return result;
    }

    private static long? ParseLong(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) &&
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
