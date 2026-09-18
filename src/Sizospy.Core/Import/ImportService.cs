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
        if (request.DgmlPath is not null) ValidateInput(request.DgmlPath, "DGML");
        if (request.MapPath is not null) ValidateInput(request.MapPath, "map XML");
        if (request.BinaryPath is not null) ValidateInput(request.BinaryPath, "binary");

        var builder = new ImportBuilder();
        progress?.Report("Reading MSTAT");
        await MstatParser.ParseAsync(request.MstatPath, builder, cancellationToken);
        if (request.DgmlPath is not null)
        {
            progress?.Report("Reading scan DGML");
            await DgmlParser.ParseAsync(request.DgmlPath, builder, cancellationToken);
        }
        else
        {
            builder.Diagnostics.Add(new ImportDiagnostic(
                DiagnosticSeverity.Warning,
                "graph-unavailable",
                "No DGML was supplied; dominator and retained-size metrics are unavailable."));
        }

        if (request.MapPath is not null)
        {
            progress?.Report("Reading map XML");
            await MapXmlParser.ParseAsync(request.MapPath, builder, cancellationToken);
        }

        long? binarySize = request.BinaryPath is null ? null : new FileInfo(request.BinaryPath).Length;
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

    private static void ValidateInput(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new SizospyException($"{description} input '{path}' does not exist.", "input-not-found");
        }
    }
}
