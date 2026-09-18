using Microsoft.Data.Sqlite;

namespace Sizospy.Reporting;

/// <summary>Queries and renders reports from a version-compatible Sizospy database.</summary>
/// <param name="databasePath">Path to the Sizospy SQLite database.</param>
public sealed class ReportService(string databasePath)
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly ReportRepository _repository = new(databasePath);

    /// <summary>Reads database coverage, size reconciliation, and diagnostics.</summary>
    /// <param name="cancellationToken">Token used to cancel database operations.</param>
    /// <returns>The database summary.</returns>
    public Task<SummaryReport> GetSummaryAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(() => _repository.GetSummaryAsync(cancellationToken));

    /// <summary>Finds and ranks normalized artifacts.</summary>
    /// <param name="filter">Filters, row limit, and ordering.</param>
    /// <param name="cancellationToken">Token used to cancel database operations.</param>
    /// <returns>Matching artifact rows.</returns>
    public Task<IReadOnlyList<MemberReportRow>> GetMembersAsync(
        ReportFilter filter,
        CancellationToken cancellationToken = default) =>
        QueryAsync(() => _repository.GetMembersAsync(filter, false, cancellationToken));

    /// <summary>Finds and ranks nodes with computed dominator metrics.</summary>
    /// <param name="filter">Filters, row limit, and ordering.</param>
    /// <param name="cancellationToken">Token used to cancel database operations.</param>
    /// <returns>Matching dominator rows.</returns>
    public Task<IReadOnlyList<MemberReportRow>> GetDominatorsAsync(
        ReportFilter filter,
        CancellationToken cancellationToken = default) =>
        QueryAsync(() => _repository.GetMembersAsync(filter, true, cancellationToken));

    /// <summary>Writes a self-contained offline HTML report.</summary>
    /// <param name="outputPath">HTML file path or directory in which to create <c>index.html</c>.</param>
    /// <param name="cancellationToken">Token used to cancel database and file operations.</param>
    public async Task GenerateWebAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var model = await QueryAsync(() => _repository.GetWebModelAsync(cancellationToken));
        await WebReportGenerator.WriteAsync(outputPath, model, cancellationToken);
    }

    private async Task<T> QueryAsync<T>(Func<Task<T>> query)
    {
        try
        {
            return await query();
        }
        catch (SqliteException ex)
        {
            throw new SizospyException(
                $"Unable to query database '{_databasePath}': {ex.Message}",
                "database-read",
                ex);
        }
    }
}

internal sealed record WebReportModel(
    SummaryReport Summary,
    IReadOnlyList<WebNode> Nodes,
    IReadOnlyList<WebEdge> Edges,
    IReadOnlyList<WebDominator> Dominators);

internal sealed record WebNode(
    [property: System.Text.Json.Serialization.JsonPropertyName("i")] long NodeId,
    [property: System.Text.Json.Serialization.JsonPropertyName("n")] string DisplayName,
    [property: System.Text.Json.Serialization.JsonPropertyName("k")] string Kind,
    [property: System.Text.Json.Serialization.JsonPropertyName("a")] string? Assembly,
    [property: System.Text.Json.Serialization.JsonPropertyName("ns")] string? Namespace,
    [property: System.Text.Json.Serialization.JsonPropertyName("t")] string? Type,
    [property: System.Text.Json.Serialization.JsonPropertyName("m")] string? Member,
    [property: System.Text.Json.Serialization.JsonPropertyName("s")] long SelfSize,
    [property: System.Text.Json.Serialization.JsonPropertyName("r")] long? RetainedSize,
    [property: System.Text.Json.Serialization.JsonPropertyName("g")] long? MarginalRetainedSize,
    [property: System.Text.Json.Serialization.JsonPropertyName("l")] double? Leverage,
    [property: System.Text.Json.Serialization.JsonPropertyName("c")] int? DominatedNodeCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("d")] int? RootDistance);

internal sealed record WebEdge(
    [property: System.Text.Json.Serialization.JsonPropertyName("s")] long Source,
    [property: System.Text.Json.Serialization.JsonPropertyName("t")] long Target,
    [property: System.Text.Json.Serialization.JsonPropertyName("r")] string Reason,
    [property: System.Text.Json.Serialization.JsonPropertyName("k")] string? ReasonKind,
    [property: System.Text.Json.Serialization.JsonPropertyName("c")] string? ConditionalGroup);

internal sealed record WebDominator(
    [property: System.Text.Json.Serialization.JsonPropertyName("n")] long Node,
    [property: System.Text.Json.Serialization.JsonPropertyName("p")] long? ImmediateDominator);
