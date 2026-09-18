using System.Globalization;
using Microsoft.Data.Sqlite;
using Sizospy.Graph;

namespace Sizospy.Storage;

internal static class DatabaseWriter
{
    public const int SchemaVersion = 1;

    public static async Task WriteAsync(
        string outputPath,
        ImportModel model,
        bool force,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath) ??
                        throw new SizospyException($"Output path '{outputPath}' has no parent directory.", "invalid-output");
        Directory.CreateDirectory(directory);
        if (File.Exists(fullPath) && !force)
        {
            throw new SizospyException(
                $"Output database '{fullPath}' already exists. Pass --force to replace it.",
                "output-conflict");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                Pooling = false,
            }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await ExecuteAsync(connection, null, SchemaSql, cancellationToken);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

                await InsertMetadataAsync(connection, transaction, model, cancellationToken);
                await InsertLogicalMembersAsync(connection, transaction, model.LogicalMembers, cancellationToken);
                await InsertNodesAsync(connection, transaction, model.Nodes, cancellationToken);
                await InsertEdgesAsync(connection, transaction, model.Edges, cancellationToken);
                await InsertRangesAsync(connection, transaction, model.Ranges, cancellationToken);
                await InsertDiagnosticsAsync(connection, transaction, model.Diagnostics, cancellationToken);

                var metrics = HasDependencyGraph(model)
                    ? DominatorAnalyzer.Analyze(model.Nodes, model.Edges, cancellationToken)
                    : new Dictionary<long, DominatorMetric>();
                await InsertDominatorsAsync(connection, transaction, metrics.Values, cancellationToken);
                await ExecuteAsync(connection, transaction, IndexSql, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await ExecuteAsync(connection, null, "PRAGMA optimize;", cancellationToken);
            }

            File.Move(temporaryPath, fullPath, force);
        }
        catch (SqliteException ex)
        {
            throw new SizospyException($"Unable to create database '{fullPath}': {ex.Message}", "database-write", ex);
        }
        catch (IOException ex)
        {
            throw new SizospyException($"Unable to replace database '{fullPath}': {ex.Message}", "database-io", ex);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task InsertMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImportModel model,
        CancellationToken cancellationToken)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema_version"] = SchemaVersion.ToString(CultureInfo.InvariantCulture),
            ["created_utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["graph_available"] = HasDependencyGraph(model).ToString(),
            ["accounted_size"] = model.Nodes.Sum(n => n.SelfSize).ToString(CultureInfo.InvariantCulture),
        };
        foreach (var pair in model.Metadata)
        {
            values[pair.Key] = pair.Value;
        }
        if (model.BinarySize is { } binarySize)
        {
            values["binary_size"] = binarySize.ToString(CultureInfo.InvariantCulture);
            values["unattributed_size"] = Math.Max(0, binarySize - model.Nodes.Sum(n => n.SelfSize))
                .ToString(CultureInfo.InvariantCulture);
        }

        foreach (var pair in values)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO import_metadata(key, value) VALUES ($key, $value);",
                cancellationToken,
                ("$key", pair.Key),
                ("$value", pair.Value));
        }
    }

    private static bool HasDependencyGraph(ImportModel model) =>
        model.Metadata.ContainsKey("dgml_edge_direction");

    private static async Task InsertLogicalMembersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LogicalMember> members,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO logical_members(
                id, stable_id, display_name, kind, assembly_name, namespace_name, type_name, member_name)
            VALUES ($id, $stable, $display, $kind, $assembly, $namespace, $type, $member);
            """;
        await InsertRowsAsync(
            connection,
            transaction,
            sql,
            members,
            ["$id", "$stable", "$display", "$kind", "$assembly", "$namespace", "$type", "$member"],
            static (parameters, member) =>
            {
                parameters[0].Value = member.Id;
                parameters[1].Value = member.StableId;
                parameters[2].Value = member.DisplayName;
                parameters[3].Value = member.Kind.ToString();
                parameters[4].Value = (object?)member.AssemblyName ?? DBNull.Value;
                parameters[5].Value = (object?)member.NamespaceName ?? DBNull.Value;
                parameters[6].Value = (object?)member.TypeName ?? DBNull.Value;
                parameters[7].Value = (object?)member.MemberName ?? DBNull.Value;
            },
            cancellationToken);
    }

    private static async Task InsertNodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SizeNode> nodes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO nodes(
                id, stable_id, compiler_identity, display_name, kind, self_size, logical_member_id, provenance, is_root)
            VALUES ($id, $stable, $identity, $display, $kind, $size, $member, $provenance, $root);
            """;
        await InsertRowsAsync(
            connection,
            transaction,
            sql,
            nodes,
            ["$id", "$stable", "$identity", "$display", "$kind", "$size", "$member", "$provenance", "$root"],
            static (parameters, node) =>
            {
                parameters[0].Value = node.Id;
                parameters[1].Value = node.StableId;
                parameters[2].Value = node.CompilerIdentity;
                parameters[3].Value = node.DisplayName;
                parameters[4].Value = node.Kind.ToString();
                parameters[5].Value = node.SelfSize;
                parameters[6].Value = (object?)node.LogicalMemberId ?? DBNull.Value;
                parameters[7].Value = node.Provenance;
                parameters[8].Value = node.IsRoot ? 1 : 0;
            },
            cancellationToken);
    }

    private static async Task InsertEdgesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<DependencyEdge> edges,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO edges(
                id, source_node_id, target_node_id, reason, reason_kind, conditional_group, provenance)
            VALUES ($id, $source, $target, $reason, $kind, $group, $provenance);
            """;
        await InsertRowsAsync(
            connection,
            transaction,
            sql,
            edges,
            ["$id", "$source", "$target", "$reason", "$kind", "$group", "$provenance"],
            static (parameters, edge) =>
            {
                parameters[0].Value = edge.Id;
                parameters[1].Value = edge.SourceNodeId;
                parameters[2].Value = edge.TargetNodeId;
                parameters[3].Value = edge.Reason;
                parameters[4].Value = (object?)edge.ReasonKind ?? DBNull.Value;
                parameters[5].Value = (object?)edge.ConditionalGroup ?? DBNull.Value;
                parameters[6].Value = edge.Provenance;
            },
            cancellationToken);
    }

    private static async Task InsertRangesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<EmittedRange> ranges,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO emitted_ranges(
                id, node_id, section_name, address, size, object_name, content_hash, provenance)
            VALUES ($id, $node, $section, $address, $size, $object, $hash, $provenance);
            """;
        await InsertRowsAsync(
            connection,
            transaction,
            sql,
            ranges,
            ["$id", "$node", "$section", "$address", "$size", "$object", "$hash", "$provenance"],
            static (parameters, range) =>
            {
                parameters[0].Value = range.Id;
                parameters[1].Value = range.NodeId;
                parameters[2].Value = (object?)range.Section ?? DBNull.Value;
                parameters[3].Value = range.Address.ToString(CultureInfo.InvariantCulture);
                parameters[4].Value = range.Size;
                parameters[5].Value = (object?)range.ObjectName ?? DBNull.Value;
                parameters[6].Value = (object?)range.ContentHash ?? DBNull.Value;
                parameters[7].Value = range.Provenance;
            },
            cancellationToken);
    }

    private static async Task InsertDiagnosticsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ImportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO import_diagnostics(severity, code, message, source, line)
            VALUES ($severity, $code, $message, $source, $line);
            """;
        await InsertRowsAsync(
            connection,
            transaction,
            sql,
            diagnostics,
            ["$severity", "$code", "$message", "$source", "$line"],
            static (parameters, diagnostic) =>
            {
                parameters[0].Value = diagnostic.Severity.ToString();
                parameters[1].Value = diagnostic.Code;
                parameters[2].Value = diagnostic.Message;
                parameters[3].Value = (object?)diagnostic.Source ?? DBNull.Value;
                parameters[4].Value = (object?)diagnostic.Line ?? DBNull.Value;
            },
            cancellationToken);
    }

    private static async Task InsertDominatorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<DominatorMetric> metrics,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dominators(
                node_id, immediate_dominator_id, retained_size, marginal_retained_size, leverage,
                dominated_node_count, root_distance, graph_reachable)
            VALUES ($node, $idom, $retained, $marginal, $leverage, $count, $distance, $reachable);
            """;
        await InsertRowsAsync(
            connection,
            transaction,
            sql,
            metrics,
            ["$node", "$idom", "$retained", "$marginal", "$leverage", "$count", "$distance", "$reachable"],
            static (parameters, metric) =>
            {
                parameters[0].Value = metric.NodeId;
                parameters[1].Value = (object?)metric.ImmediateDominatorId ?? DBNull.Value;
                parameters[2].Value = metric.RetainedSize;
                parameters[3].Value = metric.MarginalRetainedSize;
                parameters[4].Value = (object?)metric.Leverage ?? DBNull.Value;
                parameters[5].Value = metric.DominatedNodeCount;
                parameters[6].Value = (object?)metric.RootDistance ?? DBNull.Value;
                parameters[7].Value = metric.GraphReachable ? 1 : 0;
            },
            cancellationToken);
    }

    private static async Task InsertRowsAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IEnumerable<T> rows,
        IReadOnlyList<string> parameterNames,
        Action<SqliteParameterCollection, T> bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var name in parameterNames)
        {
            command.Parameters.AddWithValue(name, DBNull.Value);
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bind(command.Parameters, row);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SchemaSql = """
        PRAGMA foreign_keys = ON;
        PRAGMA journal_mode = DELETE;
        CREATE TABLE schema_info (
            version INTEGER NOT NULL,
            created_utc TEXT NOT NULL
        ) STRICT;
        INSERT INTO schema_info(version, created_utc)
        VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

        CREATE TABLE logical_members (
            id INTEGER PRIMARY KEY,
            stable_id TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            assembly_name TEXT,
            namespace_name TEXT,
            type_name TEXT,
            member_name TEXT
        ) STRICT;

        CREATE TABLE nodes (
            id INTEGER PRIMARY KEY,
            stable_id TEXT NOT NULL UNIQUE,
            compiler_identity TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            self_size INTEGER NOT NULL CHECK(self_size >= 0),
            logical_member_id INTEGER REFERENCES logical_members(id),
            provenance TEXT NOT NULL,
            is_root INTEGER NOT NULL CHECK(is_root IN (0, 1))
        ) STRICT;

        CREATE TABLE edges (
            id INTEGER PRIMARY KEY,
            source_node_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
            target_node_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
            reason TEXT NOT NULL,
            reason_kind TEXT,
            conditional_group TEXT,
            provenance TEXT NOT NULL
        ) STRICT;

        CREATE TABLE roots (
            node_id INTEGER PRIMARY KEY REFERENCES nodes(id) ON DELETE CASCADE,
            root_kind TEXT NOT NULL,
            provenance TEXT NOT NULL
        ) STRICT;
        CREATE TRIGGER nodes_insert_root AFTER INSERT ON nodes WHEN NEW.is_root = 1
        BEGIN
            INSERT INTO roots(node_id, root_kind, provenance) VALUES (NEW.id, 'graph-root', NEW.provenance);
        END;

        CREATE TABLE emitted_ranges (
            id INTEGER PRIMARY KEY,
            node_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
            section_name TEXT,
            address TEXT NOT NULL,
            size INTEGER NOT NULL CHECK(size >= 0),
            object_name TEXT,
            content_hash TEXT,
            provenance TEXT NOT NULL
        ) STRICT;

        CREATE TABLE import_metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) STRICT;

        CREATE TABLE import_diagnostics (
            id INTEGER PRIMARY KEY,
            severity TEXT NOT NULL,
            code TEXT NOT NULL,
            message TEXT NOT NULL,
            source TEXT,
            line INTEGER
        ) STRICT;

        CREATE TABLE dominators (
            node_id INTEGER PRIMARY KEY REFERENCES nodes(id) ON DELETE CASCADE,
            immediate_dominator_id INTEGER REFERENCES nodes(id),
            retained_size INTEGER NOT NULL CHECK(retained_size >= 0),
            marginal_retained_size INTEGER NOT NULL CHECK(marginal_retained_size >= 0),
            leverage REAL,
            dominated_node_count INTEGER NOT NULL CHECK(dominated_node_count >= 0),
            root_distance INTEGER,
            graph_reachable INTEGER NOT NULL CHECK(graph_reachable IN (0, 1))
        ) STRICT;

        """;

    private const string IndexSql = """
        CREATE INDEX ix_nodes_display_name ON nodes(display_name COLLATE NOCASE);
        CREATE INDEX ix_nodes_kind_size ON nodes(kind, self_size DESC);
        CREATE INDEX ix_nodes_logical_member ON nodes(logical_member_id);
        CREATE INDEX ix_logical_members_ownership ON logical_members(assembly_name, namespace_name, type_name, member_name);
        CREATE INDEX ix_edges_source ON edges(source_node_id);
        CREATE INDEX ix_edges_target ON edges(target_node_id);
        CREATE INDEX ix_edges_reason_kind ON edges(reason_kind);
        CREATE INDEX ix_ranges_node_address ON emitted_ranges(node_id, address);
        CREATE INDEX ix_dominators_idom ON dominators(immediate_dominator_id);
        CREATE INDEX ix_dominators_retained ON dominators(retained_size DESC);
        CREATE INDEX ix_dominators_leverage ON dominators(leverage DESC);
        """;
}
