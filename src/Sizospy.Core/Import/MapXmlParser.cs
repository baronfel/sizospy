using System.Globalization;

namespace Sizospy.Import;

internal static class MapXmlParser
{
    public static async Task ParseAsync(
        string path,
        ImportBuilder builder,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new LocalXmlReader(File.OpenRead(path));

            var parsedRanges = 0;
            var skippedRecords = 0;
            var usingMapSizes = false;
            var identityOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.LocalName == "ObjectNodes")
                {
                    continue;
                }

                var identity = First(reader, "Name", "name");
                var sizeText = First(reader, "Length", "length", "Size", "size");
                if (sizeText is null || !TryParseLong(sizeText, out var size))
                {
                    skippedRecords++;
                    continue;
                }

                var kind = reader.LocalName;
                var hash = First(reader, "Hash", "hash");
                if (!usingMapSizes)
                {
                    builder.UseMapAsPhysicalSizeSource();
                    usingMapSizes = true;
                }

                long? logicalMemberId = null;
                long? ownerNodeId = null;
                if (identity is not null)
                {
                    ownerNodeId = builder.GetOrAddNode(
                        identity,
                        identity,
                        NodeKind.Unknown,
                        0,
                        null,
                        "map");
                    logicalMemberId = builder.GetLogicalMemberId(ownerNodeId.Value);
                }

                var identityKey = $"{kind}\0{identity}\0{hash}";
                var occurrence = identityOccurrences.GetValueOrDefault(identityKey);
                identityOccurrences[identityKey] = occurrence + 1;
                var artifactIdentity = $"map:{kind}:{identity ?? "<unnamed>"}:{hash ?? "<no-hash>"}:{occurrence}";
                var displayName = identity is null ? kind : $"{identity} [{kind}]";
                var nodeId = builder.GetOrAddNode(
                    artifactIdentity,
                    displayName,
                    NodeKind.CompilerArtifact,
                    size,
                    logicalMemberId,
                    "map");
                builder.AddRange(nodeId, kind, 0, size, identity, hash, "map");
                if (ownerNodeId is { } owner)
                {
                    builder.AddEdge(owner, nodeId, $"emitted {kind}", "layout", null, "map");
                }
                parsedRanges++;
            }

            if (parsedRanges == 0)
            {
                builder.Diagnostics.Add(new ImportDiagnostic(
                    DiagnosticSeverity.Warning,
                    "map-no-ranges",
                    "The map XML was well-formed but no object nodes with a supported Length were found.",
                    path));
            }

            if (skippedRecords > 0)
            {
                builder.Diagnostics.Add(new ImportDiagnostic(
                    DiagnosticSeverity.Warning,
                    "map-records-skipped",
                    $"{skippedRecords} map records lacked a supported Length and were skipped.",
                    path));
            }

            builder.Metadata["map_ranges"] = parsedRanges.ToString(CultureInfo.InvariantCulture);
            builder.Metadata["map_accounted_size"] = builder.Ranges.Sum(r => r.Size).ToString(CultureInfo.InvariantCulture);
        }
        catch (LocalXmlException ex)
        {
            throw new SizospyException(
                $"Malformed map XML '{path}' at line {ex.LineNumber}, position {ex.LinePosition}: {ex.Message}",
                "malformed-map",
                ex);
        }
        catch (IOException ex)
        {
            throw new SizospyException($"Unable to read map XML '{path}': {ex.Message}", "map-io", ex);
        }
    }

    private static string? First(LocalXmlReader reader, params string[] names)
    {
        foreach (var name in names)
        {
            var value = reader.GetAttribute(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryParseLong(string text, out long value)
    {
        var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return long.TryParse(
            normalized,
            text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }
}
