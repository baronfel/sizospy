using System.Xml;

namespace Sizospy.Import;

internal static class DgmlParser
{
    public static async Task ParseAsync(
        string path,
        ImportBuilder builder,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });

            var dgmlNodes = new Dictionary<string, string>(StringComparer.Ordinal);
            var links = new List<(string Source, string Target, string Reason, string? Kind, string? Group)>();
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName == "Node")
                {
                    var dgmlId = RequiredAttribute(reader, "Id", path);
                    var label = reader.GetAttribute("Label") ?? dgmlId;
                    dgmlNodes[dgmlId] = label;
                    var category = reader.GetAttribute("Category") ?? reader.GetAttribute("Group");
                    var isRoot = IsTrue(reader.GetAttribute("IsRoot")) ||
                                 category?.Contains("Root", StringComparison.OrdinalIgnoreCase) == true;
                    builder.GetOrAddNode(
                        label,
                        label,
                        ParseKind(category, label),
                        0,
                        null,
                        "dgml",
                        isRoot);
                }
                else if (reader.LocalName == "Link")
                {
                    var source = RequiredAttribute(reader, "Source", path);
                    var target = RequiredAttribute(reader, "Target", path);
                    var reason = reader.GetAttribute("Reason") ??
                                 reader.GetAttribute("Label") ??
                                 reader.GetAttribute("Category") ??
                                 "dependency";
                    links.Add((
                        source,
                        target,
                        reason,
                        reader.GetAttribute("Category") ?? reader.GetAttribute("ReasonKind"),
                        reader.GetAttribute("ConditionalGroup") ?? reader.GetAttribute("Group")));
                }
            }

            foreach (var link in links)
            {
                var sourceIdentity = dgmlNodes.GetValueOrDefault(link.Source, link.Source);
                var targetIdentity = dgmlNodes.GetValueOrDefault(link.Target, link.Target);
                var source = builder.GetOrAddNode(sourceIdentity, sourceIdentity, NodeKind.Unknown, 0, null, "dgml");
                var target = builder.GetOrAddNode(targetIdentity, targetIdentity, NodeKind.Unknown, 0, null, "dgml");
                builder.AddEdge(source, target, link.Reason, link.Kind, link.Group, "dgml");
            }

            if (links.Count > 0 && !builder.Nodes.Any(n => n.IsRoot))
            {
                var incoming = links.Select(l => l.Target).ToHashSet(StringComparer.Ordinal);
                foreach (var rootDgmlId in links.Select(l => l.Source).Where(s => !incoming.Contains(s)).Distinct(StringComparer.Ordinal))
                {
                    var rootIdentity = dgmlNodes.GetValueOrDefault(rootDgmlId, rootDgmlId);
                    if (builder.TryGetNode(rootIdentity, out var id))
                    {
                        builder.MarkRoot(id);
                    }
                }
            }

            builder.Metadata["dgml_edge_direction"] = "source-depends-on-target";
            builder.Metadata["dgml_path"] = Path.GetFullPath(path);
            builder.Metadata["retained_size_semantics"] = "graph-model-estimate";
        }
        catch (XmlException ex)
        {
            throw new SizospyException(
                $"Malformed DGML '{path}' at line {ex.LineNumber}, position {ex.LinePosition}: {ex.Message}",
                "malformed-dgml",
                ex);
        }
        catch (IOException ex)
        {
            throw new SizospyException($"Unable to read DGML '{path}': {ex.Message}", "dgml-io", ex);
        }
    }

    private static string RequiredAttribute(XmlReader reader, string name, string path) =>
        reader.GetAttribute(name) ??
        throw new SizospyException($"DGML '{path}' contains a {reader.LocalName} without required '{name}'.", "malformed-dgml");

    private static bool IsTrue(string? value) =>
        bool.TryParse(value, out var result) && result;

    private static NodeKind ParseKind(string? category, string label)
    {
        var value = $"{category} {label}";
        if (value.Contains("Method", StringComparison.OrdinalIgnoreCase)) return NodeKind.Method;
        if (value.Contains("Type", StringComparison.OrdinalIgnoreCase)) return NodeKind.Type;
        if (value.Contains("Field", StringComparison.OrdinalIgnoreCase)) return NodeKind.Field;
        if (value.Contains("Runtime", StringComparison.OrdinalIgnoreCase)) return NodeKind.RuntimeArtifact;
        if (value.Contains("Data", StringComparison.OrdinalIgnoreCase)) return NodeKind.Data;
        return NodeKind.Unknown;
    }
}
