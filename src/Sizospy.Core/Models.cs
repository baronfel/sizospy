namespace Sizospy;

/// <summary>Classifies a normalized logical or physical size node.</summary>
public enum NodeKind
{
    /// <summary>The artifact kind could not be inferred.</summary>
    Unknown,
    /// <summary>A graph-only node introduced by Sizospy.</summary>
    Synthetic,
    /// <summary>An assembly ownership node.</summary>
    Assembly,
    /// <summary>A namespace ownership node.</summary>
    Namespace,
    /// <summary>A managed type or its runtime representation.</summary>
    Type,
    /// <summary>A managed method or generated method body.</summary>
    Method,
    /// <summary>A managed field or its emitted data.</summary>
    Field,
    /// <summary>Emitted data associated with managed code.</summary>
    Data,
    /// <summary>A runtime data structure without a managed owner.</summary>
    RuntimeArtifact,
    /// <summary>A physical object emitted by the NativeAOT compiler.</summary>
    CompilerArtifact,
}

/// <summary>Specifies the severity of an import diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational context that does not affect analysis.</summary>
    Info,
    /// <summary>A recoverable limitation that can reduce analysis fidelity.</summary>
    Warning,
    /// <summary>An error that prevents a complete import.</summary>
    Error,
}

/// <summary>Describes a managed ownership target shared by one or more physical artifacts.</summary>
/// <param name="Id">Database-local numeric identifier.</param>
/// <param name="StableId">Deterministic identifier derived from logical ownership.</param>
/// <param name="DisplayName">Human-readable logical member name.</param>
/// <param name="Kind">Logical member classification.</param>
/// <param name="AssemblyName">Owning assembly name, when known.</param>
/// <param name="NamespaceName">Owning namespace name, when known.</param>
/// <param name="TypeName">Owning type name, when known.</param>
/// <param name="MemberName">Owning member name, when known.</param>
public sealed record LogicalMember(
    long Id,
    string StableId,
    string DisplayName,
    NodeKind Kind,
    string? AssemblyName,
    string? NamespaceName,
    string? TypeName,
    string? MemberName);

/// <summary>Describes a normalized graph node and its non-overlapping physical byte weight.</summary>
/// <param name="Id">Database-local numeric identifier.</param>
/// <param name="StableId">Deterministic identifier derived from compiler identity.</param>
/// <param name="CompilerIdentity">Exact compiler artifact name used to join diagnostic inputs.</param>
/// <param name="DisplayName">Human-readable artifact name.</param>
/// <param name="Kind">Artifact classification.</param>
/// <param name="SelfSize">Physical bytes attributed directly to this node.</param>
/// <param name="LogicalMemberId">Associated logical member identifier, when known.</param>
/// <param name="Provenance">Comma-separated diagnostic inputs that contributed this node.</param>
/// <param name="IsRoot">Whether the diagnostic graph identifies this node as a root.</param>
public sealed record SizeNode(
    long Id,
    string StableId,
    string CompilerIdentity,
    string DisplayName,
    NodeKind Kind,
    long SelfSize,
    long? LogicalMemberId,
    string Provenance,
    bool IsRoot = false);

/// <summary>Represents a directed dependency where the source depends on the target.</summary>
/// <param name="Id">Database-local numeric identifier.</param>
/// <param name="SourceNodeId">Identifier of the depending node.</param>
/// <param name="TargetNodeId">Identifier of the depended-on node.</param>
/// <param name="Reason">Compiler-provided dependency explanation.</param>
/// <param name="ReasonKind">Optional normalized reason category.</param>
/// <param name="ConditionalGroup">Optional identifier for conditional dependency grouping.</param>
/// <param name="Provenance">Diagnostic input that contributed this edge.</param>
public sealed record DependencyEdge(
    long Id,
    long SourceNodeId,
    long TargetNodeId,
    string Reason,
    string? ReasonKind,
    string? ConditionalGroup,
    string Provenance);

/// <summary>Describes one physical object or address range emitted for a normalized node.</summary>
/// <param name="Id">Database-local numeric identifier.</param>
/// <param name="NodeId">Owning physical node identifier.</param>
/// <param name="Section">Compiler object kind or binary section, when known.</param>
/// <param name="Address">Starting virtual address, or zero when the input does not provide one.</param>
/// <param name="Size">Length of the emitted range in bytes.</param>
/// <param name="ObjectName">Compiler-provided object name, when present.</param>
/// <param name="ContentHash">Compiler-provided content hash, when present.</param>
/// <param name="Provenance">Diagnostic input that contributed this range.</param>
public sealed record EmittedRange(
    long Id,
    long NodeId,
    string? Section,
    ulong Address,
    long Size,
    string? ObjectName,
    string? ContentHash,
    string Provenance);

/// <summary>Records a durable diagnostic produced while importing compiler artifacts.</summary>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Code">Stable machine-readable diagnostic code.</param>
/// <param name="Message">Human-readable diagnostic text.</param>
/// <param name="Source">Related input path, when applicable.</param>
/// <param name="Line">Related source line, when applicable.</param>
public sealed record ImportDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Source = null,
    int? Line = null);

/// <summary>Contains immediate-dominator and retained-size metrics for one graph node.</summary>
/// <param name="NodeId">Analyzed node identifier.</param>
/// <param name="ImmediateDominatorId">Immediate dominator, excluding the synthetic super-root.</param>
/// <param name="RetainedSize">Sum of physical self sizes in the node's dominator subtree.</param>
/// <param name="MarginalRetainedSize">Retained size excluding the node's own bytes.</param>
/// <param name="Leverage">Retained size divided by self size, when self size is nonzero.</param>
/// <param name="DominatedNodeCount">Number of descendants in the dominator tree.</param>
/// <param name="RootDistance">Distance from the synthetic super-root, when meaningful.</param>
/// <param name="GraphReachable">Whether the node was reachable from a compiler-provided or inferred root.</param>
public sealed record DominatorMetric(
    long NodeId,
    long? ImmediateDominatorId,
    long RetainedSize,
    long MarginalRetainedSize,
    double? Leverage,
    int DominatedNodeCount,
    int? RootDistance,
    bool GraphReachable);

/// <summary>Contains the normalized, database-ready result of an artifact import.</summary>
/// <param name="LogicalMembers">Logical ownership records.</param>
/// <param name="Nodes">Normalized graph and physical artifact nodes.</param>
/// <param name="Edges">Directed dependency edges.</param>
/// <param name="Ranges">Emitted physical ranges.</param>
/// <param name="Diagnostics">Import diagnostics.</param>
/// <param name="Metadata">Import and format metadata.</param>
/// <param name="BinarySize">Final native binary size, when supplied.</param>
public sealed record ImportModel(
    IReadOnlyList<LogicalMember> LogicalMembers,
    IReadOnlyList<SizeNode> Nodes,
    IReadOnlyList<DependencyEdge> Edges,
    IReadOnlyList<EmittedRange> Ranges,
    IReadOnlyList<ImportDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, string> Metadata,
    long? BinarySize = null);

/// <summary>Specifies NativeAOT inputs and the SQLite output for an import.</summary>
/// <param name="MstatPath">Required NativeAOT MSTAT assembly path.</param>
/// <param name="OutputPath">SQLite database path to create.</param>
/// <param name="DgmlPath">Optional NativeAOT scan DGML path.</param>
/// <param name="MapPath">Optional NativeAOT object map XML path.</param>
/// <param name="BinaryPath">Optional final native binary path.</param>
/// <param name="Force">Whether to replace an existing output database.</param>
public sealed record ImportRequest(
    string MstatPath,
    string OutputPath,
    string? DgmlPath = null,
    string? MapPath = null,
    string? BinaryPath = null,
    bool Force = false);

/// <summary>Summarizes a completed import.</summary>
/// <param name="DatabasePath">Absolute output database path.</param>
/// <param name="NodeCount">Number of normalized nodes.</param>
/// <param name="EdgeCount">Number of dependency edges.</param>
/// <param name="AccountedSize">Non-overlapping physical bytes represented by nodes.</param>
/// <param name="BinarySize">Final native binary size, when supplied.</param>
/// <param name="UnattributedSize">Binary bytes not represented by imported artifacts, when available.</param>
/// <param name="GraphAvailable">Whether a scan DGML dependency graph was imported.</param>
/// <param name="WarningCount">Number of recoverable import warnings.</param>
public sealed record ImportResult(
    string DatabasePath,
    int NodeCount,
    int EdgeCount,
    long AccountedSize,
    long? BinarySize,
    long? UnattributedSize,
    bool GraphAvailable,
    int WarningCount);

/// <summary>Specifies deterministic ordering for member and dominator reports.</summary>
public enum ReportSort
{
    /// <summary>Order by descending physical self size.</summary>
    Self,
    /// <summary>Order by descending retained size.</summary>
    Retained,
    /// <summary>Order by descending retained-to-self-size ratio.</summary>
    Leverage,
    /// <summary>Order by display name.</summary>
    Name,
}

/// <summary>Specifies filters, row limits, and ordering for node reports.</summary>
/// <param name="Name">Case-insensitive display or compiler-name substring.</param>
/// <param name="Kind">Exact node kind, matched case-insensitively.</param>
/// <param name="Assembly">Exact owning assembly, matched case-insensitively.</param>
/// <param name="Namespace">Exact owning namespace, matched case-insensitively.</param>
/// <param name="Limit">Maximum rows to return.</param>
/// <param name="Sort">Result ordering.</param>
public sealed record ReportFilter(
    string? Name = null,
    string? Kind = null,
    string? Assembly = null,
    string? Namespace = null,
    int Limit = 50,
    ReportSort Sort = ReportSort.Self);

/// <summary>Represents one row in a member or dominator report.</summary>
/// <param name="NodeId">Database-local node identifier.</param>
/// <param name="StableId">Deterministic node identifier.</param>
/// <param name="CompilerIdentity">Exact compiler identity.</param>
/// <param name="DisplayName">Human-readable node name.</param>
/// <param name="Kind">Node kind.</param>
/// <param name="Assembly">Owning assembly, when known.</param>
/// <param name="Namespace">Owning namespace, when known.</param>
/// <param name="Type">Owning type, when known.</param>
/// <param name="Member">Owning member, when known.</param>
/// <param name="SelfSize">Physical bytes attributed directly to the node.</param>
/// <param name="RetainedSize">Dominator-subtree bytes, when graph metrics are available.</param>
/// <param name="MarginalRetainedSize">Retained bytes excluding self size.</param>
/// <param name="Leverage">Retained-to-self-size ratio, when defined.</param>
/// <param name="DominatedNodeCount">Dominator descendants, when graph metrics are available.</param>
/// <param name="RootDistance">Distance from the synthetic super-root, when available.</param>
/// <param name="IsRoot">Whether the node is a graph root.</param>
/// <param name="Provenance">Comma-separated contributing inputs.</param>
public sealed record MemberReportRow(
    long NodeId,
    string StableId,
    string CompilerIdentity,
    string DisplayName,
    string Kind,
    string? Assembly,
    string? Namespace,
    string? Type,
    string? Member,
    long SelfSize,
    long? RetainedSize,
    long? MarginalRetainedSize,
    double? Leverage,
    int? DominatedNodeCount,
    int? RootDistance,
    bool IsRoot,
    string Provenance);

/// <summary>Summarizes database coverage, physical bytes, graph availability, and diagnostics.</summary>
/// <param name="SchemaVersion">Sizospy SQLite schema version.</param>
/// <param name="NodeCount">Number of normalized nodes.</param>
/// <param name="EdgeCount">Number of dependency edges.</param>
/// <param name="LogicalMemberCount">Number of logical ownership records.</param>
/// <param name="AccountedSize">Non-overlapping physical bytes represented by nodes.</param>
/// <param name="BinarySize">Final native binary size, when supplied.</param>
/// <param name="UnattributedSize">Binary bytes not represented by imported artifacts, when available.</param>
/// <param name="GraphAvailable">Whether graph metrics are available.</param>
/// <param name="RetainedSizesAreEstimates">Whether retained sizes use the graph-model estimate.</param>
/// <param name="Kinds">Per-kind node and physical-size summaries.</param>
/// <param name="Diagnostics">Durable import diagnostics.</param>
public sealed record SummaryReport(
    int SchemaVersion,
    int NodeCount,
    int EdgeCount,
    int LogicalMemberCount,
    long AccountedSize,
    long? BinarySize,
    long? UnattributedSize,
    bool GraphAvailable,
    bool RetainedSizesAreEstimates,
    IReadOnlyList<KindSummary> Kinds,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

/// <summary>Summarizes node count and physical self size for one artifact kind.</summary>
/// <param name="Kind">Artifact kind.</param>
/// <param name="Count">Number of nodes of this kind.</param>
/// <param name="SelfSize">Total physical bytes attributed to the kind.</param>
public sealed record KindSummary(string Kind, int Count, long SelfSize);

/// <summary>Represents an expected Sizospy failure with a stable machine-readable code.</summary>
/// <param name="message">Human-readable failure description.</param>
/// <param name="code">Stable machine-readable error code.</param>
/// <param name="innerException">Underlying exception, when applicable.</param>
public sealed class SizospyException(string message, string code, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Gets the stable machine-readable error code.</summary>
    public string Code { get; } = code;
}
