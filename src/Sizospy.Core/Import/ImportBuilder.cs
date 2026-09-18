using System.Security.Cryptography;
using System.Text;

namespace Sizospy.Import;

internal sealed class ImportBuilder
{
    private readonly Dictionary<string, long> _nodeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _nodeIndexes = [];
    private readonly Dictionary<string, long> _memberIds = new(StringComparer.Ordinal);
    private long _nextNodeId = 1;
    private long _nextMemberId = 1;
    private long _nextEdgeId = 1;
    private long _nextRangeId = 1;

    public List<LogicalMember> LogicalMembers { get; } = [];
    public List<SizeNode> Nodes { get; } = [];
    public List<DependencyEdge> Edges { get; } = [];
    public List<EmittedRange> Ranges { get; } = [];
    public List<ImportDiagnostic> Diagnostics { get; } = [];
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);

    public long GetOrAddLogicalMember(
        string stableKey,
        string displayName,
        NodeKind kind,
        string? assembly,
        string? namespaceName,
        string? typeName,
        string? memberName)
    {
        if (_memberIds.TryGetValue(stableKey, out var existing))
        {
            return existing;
        }

        var id = _nextMemberId++;
        _memberIds.Add(stableKey, id);
        LogicalMembers.Add(new LogicalMember(
            id,
            StableHash("member", stableKey),
            displayName,
            kind,
            assembly,
            namespaceName,
            typeName,
            memberName));
        return id;
    }

    public long GetOrAddNode(
        string compilerIdentity,
        string displayName,
        NodeKind kind,
        long selfSize,
        long? logicalMemberId,
        string provenance,
        bool isRoot = false)
    {
        if (_nodeIds.TryGetValue(compilerIdentity, out var existing))
        {
            var index = _nodeIndexes[existing];
            var current = Nodes[index];
            Nodes[index] = current with
            {
                DisplayName = Prefer(current.DisplayName, displayName),
                Kind = current.Kind == NodeKind.Unknown ? kind : current.Kind,
                SelfSize = Math.Max(current.SelfSize, selfSize),
                LogicalMemberId = current.LogicalMemberId ?? logicalMemberId,
                IsRoot = current.IsRoot || isRoot,
                Provenance = CombineProvenance(current.Provenance, provenance),
            };
            return existing;
        }

        var id = _nextNodeId++;
        _nodeIds.Add(compilerIdentity, id);
        _nodeIndexes.Add(id, Nodes.Count);
        Nodes.Add(new SizeNode(
            id,
            StableHash("node", compilerIdentity),
            compilerIdentity,
            displayName,
            kind,
            Math.Max(0, selfSize),
            logicalMemberId,
            provenance,
            isRoot));
        return id;
    }

    public bool TryGetNode(string compilerIdentity, out long nodeId) =>
        _nodeIds.TryGetValue(compilerIdentity, out nodeId);

    public long? GetLogicalMemberId(long nodeId) =>
        Nodes[_nodeIndexes[nodeId]].LogicalMemberId;

    public void MarkRoot(long nodeId)
    {
        var index = _nodeIndexes[nodeId];
        Nodes[index] = Nodes[index] with { IsRoot = true };
    }

    public void UseMapAsPhysicalSizeSource()
    {
        for (var i = 0; i < Nodes.Count; i++)
        {
            Nodes[i] = Nodes[i] with { SelfSize = 0 };
        }

        Metadata["physical_size_source"] = "map";
    }

    public void AddEdge(
        long source,
        long target,
        string reason,
        string? reasonKind,
        string? conditionalGroup,
        string provenance)
    {
        if (source == target && Edges.Any(e => e.SourceNodeId == source && e.TargetNodeId == target && e.Reason == reason))
        {
            return;
        }

        Edges.Add(new DependencyEdge(
            _nextEdgeId++,
            source,
            target,
            reason,
            reasonKind,
            conditionalGroup,
            provenance));
    }

    public void AddRange(
        long nodeId,
        string? section,
        ulong address,
        long size,
        string? objectName,
        string? contentHash,
        string provenance)
    {
        Ranges.Add(new EmittedRange(
            _nextRangeId++,
            nodeId,
            section,
            address,
            Math.Max(size, 0),
            objectName,
            contentHash,
            provenance));
    }

    public ImportModel Build(long? binarySize) => new(
        LogicalMembers.OrderBy(m => m.Id).ToArray(),
        Nodes.OrderBy(n => n.Id).ToArray(),
        Edges.OrderBy(e => e.Id).ToArray(),
        Ranges.OrderBy(r => r.Id).ToArray(),
        Diagnostics.ToArray(),
        new Dictionary<string, string>(Metadata, StringComparer.Ordinal),
        binarySize);

    private static string StableHash(string prefix, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}:{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static string Prefer(string current, string candidate) =>
        string.IsNullOrWhiteSpace(current) || current == candidate ? candidate : current;

    private static string CombineProvenance(string current, string candidate)
    {
        var values = current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(candidate)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return string.Join(',', values);
    }
}
