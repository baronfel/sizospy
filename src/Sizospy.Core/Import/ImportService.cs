using Sizospy.Storage;

namespace Sizospy.Import;

/// <summary>Imports NativeAOT diagnostic artifacts into a normalized Sizospy SQLite database.</summary>
public sealed class ImportService
{
    /// <summary>Imports the requested artifacts and atomically creates the output database.</summary>
    /// <param name="request">Input artifact paths and output behavior.</param>
    /// <param name="progress">Optional sink for phase-level progress messages.</param>
    /// <param name="cancellationToken">Token used to cancel parsing, analysis, and database writes.</param>
    /// <returns>A summary of the completed import.</returns>
    /// <exception cref="SizospyException">The inputs are invalid or the database cannot be created.</exception>
    public async Task<ImportResult> ImportAsync(
        ImportRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(request.MstatPath, "MSTAT");
        var discoveredInputs = new List<string>();
        var discoveryDiagnostics = new List<ImportDiagnostic>();
        var dgmlPath = DiscoverSibling(
            request.MstatPath,
            request.DgmlPath,
            ".scan.dgml.xml",
            "scan DGML",
            "dgml",
            discoveredInputs,
            discoveryDiagnostics);
        var mapPath = DiscoverSibling(
            request.MstatPath,
            request.MapPath,
            ".map.xml",
            "map XML",
            "map",
            discoveredInputs,
            discoveryDiagnostics);
        var binaryPath = DiscoverBinary(
            request.MstatPath,
            request.BinaryPath,
            discoveredInputs,
            discoveryDiagnostics);

        if (dgmlPath is not null) ValidateInput(dgmlPath, "DGML");
        if (mapPath is not null) ValidateInput(mapPath, "map XML");
        if (binaryPath is not null) ValidateInput(binaryPath, "binary");

        var builder = new ImportBuilder();
        builder.Diagnostics.AddRange(discoveryDiagnostics);
        if (discoveredInputs.Count > 0)
        {
            builder.Metadata["auto_discovered_inputs"] = string.Join(',', discoveredInputs);
        }

        progress?.Report("Reading MSTAT");
        await MstatParser.ParseAsync(request.MstatPath, builder, cancellationToken);
        if (dgmlPath is not null)
        {
            progress?.Report("Reading scan DGML");
            await DgmlParser.ParseAsync(dgmlPath, builder, cancellationToken);
        }
        else
        {
            builder.Diagnostics.Add(new ImportDiagnostic(
                DiagnosticSeverity.Warning,
                "graph-unavailable",
                "No DGML was supplied; dominator and retained-size metrics are unavailable."));
        }

        if (mapPath is not null)
        {
            progress?.Report("Reading map XML");
            await MapXmlParser.ParseAsync(mapPath, builder, cancellationToken);
            builder.Metadata["map_path"] = Path.GetFullPath(mapPath);
        }

        long? binarySize = binaryPath is null ? null : new FileInfo(binaryPath).Length;
        if (binaryPath is not null)
        {
            builder.Metadata["binary_path"] = Path.GetFullPath(binaryPath);
        }

        var model = builder.Build(binarySize);
        progress?.Report("Writing SQLite database");
        await DatabaseWriter.WriteAsync(request.OutputPath, model, request.Force, cancellationToken);

        var accounted = model.Nodes.Sum(n => n.SelfSize);
        var graphAvailable = model.Metadata.ContainsKey("dgml_edge_direction");
        return new ImportResult(
            Path.GetFullPath(request.OutputPath),
            model.Nodes.Count,
            model.Edges.Count,
            accounted,
            binarySize,
            binarySize is null ? null : Math.Max(0, binarySize.Value - accounted),
            graphAvailable,
            model.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning));
    }

    private static string? DiscoverSibling(
        string mstatPath,
        string? suppliedPath,
        string suffix,
        string description,
        string inputKind,
        List<string> discoveredInputs,
        List<ImportDiagnostic> diagnostics)
    {
        if (suppliedPath is not null)
        {
            return suppliedPath;
        }

        var candidate = Path.Combine(
            GetMstatDirectory(mstatPath),
            GetMstatStem(mstatPath) + suffix);
        if (!File.Exists(candidate))
        {
            return null;
        }

        discoveredInputs.Add(inputKind);
        diagnostics.Add(new ImportDiagnostic(
            DiagnosticSeverity.Info,
            "input-auto-discovered",
            $"Discovered adjacent {description} input '{Path.GetFullPath(candidate)}'."));
        return candidate;
    }

    private static string? DiscoverBinary(
        string mstatPath,
        string? suppliedPath,
        List<string> discoveredInputs,
        List<ImportDiagnostic> diagnostics)
    {
        if (suppliedPath is not null)
        {
            return suppliedPath;
        }

        var directory = GetMstatDirectory(mstatPath);
        var stem = GetMstatStem(mstatPath);
        var candidates = new[]
        {
            Path.Combine(directory, stem + ".exe"),
            Path.Combine(directory, stem),
            Path.Combine(directory, stem + ".so"),
            Path.Combine(directory, "lib" + stem + ".so"),
            Path.Combine(directory, stem + ".dylib"),
            Path.Combine(directory, "lib" + stem + ".dylib"),
        }
        .Where(File.Exists)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

        if (candidates.Length == 0)
        {
            var dllCandidate = Path.Combine(directory, stem + ".dll");
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (File.Exists(dllCandidate) &&
                !Path.GetFullPath(dllCandidate).Equals(Path.GetFullPath(mstatPath), pathComparison))
            {
                candidates = [dllCandidate];
            }
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        if (candidates.Length > 1)
        {
            diagnostics.Add(new ImportDiagnostic(
                DiagnosticSeverity.Warning,
                "binary-discovery-ambiguous",
                $"Multiple adjacent native binaries match the MSTAT name: {string.Join(", ", candidates.Select(Path.GetFileName))}. Use --binary."));
            return null;
        }

        discoveredInputs.Add("binary");
        diagnostics.Add(new ImportDiagnostic(
            DiagnosticSeverity.Info,
            "input-auto-discovered",
            $"Discovered adjacent native binary input '{Path.GetFullPath(candidates[0])}'."));
        return candidates[0];
    }

    private static string GetMstatDirectory(string mstatPath) =>
        Path.GetDirectoryName(Path.GetFullPath(mstatPath))!;

    private static string GetMstatStem(string mstatPath)
    {
        var fileName = Path.GetFileName(mstatPath);
        const string suffix = ".mstat";
        return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }

    private static void ValidateInput(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new SizospyException($"{description} input '{path}' does not exist.", "input-not-found");
        }
    }
}
