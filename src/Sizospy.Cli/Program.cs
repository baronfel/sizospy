using System.CommandLine;
using Sizospy;
using Sizospy.Import;
using Sizospy.Reporting;

return await SizospyCli.RunAsync(args);

internal static class SizospyCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = BuildRoot();
        return await root.Parse(args).InvokeAsync();
    }

    private static RootCommand BuildRoot()
    {
        var root = new RootCommand(
            "Analyze .NET NativeAOT binary size and reachability. " +
            "Use 'sizospy import' to create a database and 'sizospy report' to query it.");
        root.Subcommands.Add(BuildImport());
        root.Subcommands.Add(BuildReport());
        return root;
    }

    private static Command BuildImport()
    {
        var command = new Command("import", "Import NativeAOT MSTAT, scan DGML, map XML, and binary data into SQLite.");
        var mstat = new Option<FileInfo>("--mstat") { Description = "Required NativeAOT .mstat managed assembly.", Required = true };
        var dgml = new Option<FileInfo?>("--dgml") { Description = "NativeAOT scan DGML. Defaults to an adjacent <name>.scan.dgml.xml file." };
        var map = new Option<FileInfo?>("--map") { Description = "NativeAOT XML object map. Defaults to an adjacent <name>.map.xml file." };
        var binary = new Option<FileInfo?>("--binary") { Description = "Final native binary. Defaults to one unambiguous adjacent binary with the MSTAT name." };
        var output = new Option<FileInfo>("--output") { Description = "SQLite database to create.", Required = true };
        var force = new Option<bool>("--force") { Description = "Replace an existing output database." };
        command.Options.Add(mstat);
        command.Options.Add(dgml);
        command.Options.Add(map);
        command.Options.Add(binary);
        command.Options.Add(output);
        command.Options.Add(force);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(async () =>
        {
            var progress = new Progress<string>(message => Console.Error.WriteLine($"sizospy: {message}..."));
            var result = await new ImportService().ImportAsync(
                new ImportRequest(
                    parseResult.GetValue(mstat)!.FullName,
                    parseResult.GetValue(output)!.FullName,
                    parseResult.GetValue(dgml)?.FullName,
                    parseResult.GetValue(map)?.FullName,
                    parseResult.GetValue(binary)?.FullName,
                    parseResult.GetValue(force)),
                progress,
                cancellationToken);
            Console.Error.WriteLine(
                $"sizospy: imported {result.NodeCount:N0} nodes and {result.EdgeCount:N0} edges; " +
                $"{ReportFormatter.FormatBytes(result.AccountedSize)} accounted.");
            if (result.WarningCount > 0)
            {
                Console.Error.WriteLine($"sizospy: {result.WarningCount} import warning(s); see report summary.");
            }
            return 0;
        }));
        return command;
    }

    private static Command BuildReport()
    {
        var report = new Command("report", "Query a Sizospy SQLite database or generate an offline web report.");
        var database = new Option<FileInfo>("--database", "-d") { Description = "Sizospy SQLite database.", Required = true };
        report.Options.Add(database);
        report.Subcommands.Add(BuildSummary(database));
        report.Subcommands.Add(BuildMembers(database, false));
        report.Subcommands.Add(BuildMembers(database, true));
        report.Subcommands.Add(BuildWeb(database));
        return report;
    }

    private static Command BuildSummary(Option<FileInfo> database)
    {
        var command = new Command("summary", "Show import coverage, graph availability, and size totals.");
        var format = FormatOption();
        var output = OutputOption();
        command.Options.Add(format);
        command.Options.Add(output);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(async () =>
        {
            var summary = await new ReportService(parseResult.GetValue(database)!.FullName)
                .GetSummaryAsync(cancellationToken);
            await WriteOutputAsync(
                ReportFormatter.FormatSummary(summary, parseResult.GetValue(format)),
                parseResult.GetValue(output),
                cancellationToken);
            return 0;
        }));
        return command;
    }

    private static Command BuildMembers(
        Option<FileInfo> database,
        bool dominators)
    {
        var command = new Command(
            dominators ? "dominators" : "members",
            dominators ? "Show the highest-retained dominators." : "Find and rank imported artifacts and logical members.");
        var name = new Option<string?>("--name") { Description = "Case-insensitive name substring." };
        var kind = new Option<string?>("--kind") { Description = "Exact artifact kind." };
        var assembly = new Option<string?>("--assembly") { Description = "Exact owning assembly." };
        var namespaceName = new Option<string?>("--namespace") { Description = "Exact owning namespace." };
        var limit = new Option<int>("--limit") { Description = "Maximum rows (1-100000).", DefaultValueFactory = _ => 50 };
        var sort = new Option<ReportSort>("--sort")
        {
            Description = "Sort by self, retained, leverage, or name.",
            DefaultValueFactory = _ => dominators ? ReportSort.Retained : ReportSort.Self,
        };
        var format = FormatOption();
        var output = OutputOption();
        foreach (var option in new Option[] { name, kind, assembly, namespaceName, limit, sort, format, output })
        {
            command.Options.Add(option);
        }
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(async () =>
        {
            var filter = new ReportFilter(
                parseResult.GetValue(name),
                parseResult.GetValue(kind),
                parseResult.GetValue(assembly),
                parseResult.GetValue(namespaceName),
                parseResult.GetValue(limit),
                parseResult.GetValue(sort));
            var service = new ReportService(parseResult.GetValue(database)!.FullName);
            var rows = dominators
                ? await service.GetDominatorsAsync(filter, cancellationToken)
                : await service.GetMembersAsync(filter, cancellationToken);
            await WriteOutputAsync(
                ReportFormatter.FormatMembers(rows, parseResult.GetValue(format)),
                parseResult.GetValue(output),
                cancellationToken);
            return 0;
        }));
        return command;
    }

    private static Command BuildWeb(Option<FileInfo> database)
    {
        var command = new Command("web", "Generate a self-contained offline HTML report.");
        var output = new Option<FileSystemInfo>("--output") { Description = "HTML file or output directory.", Required = true };
        command.Options.Add(output);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(async () =>
        {
            var target = parseResult.GetValue(output)!.FullName;
            await new ReportService(parseResult.GetValue(database)!.FullName)
                .GenerateWebAsync(target, cancellationToken);
            Console.Error.WriteLine($"sizospy: wrote offline report to {Path.GetFullPath(target)}");
            return 0;
        }));
        return command;
    }

    private static Option<OutputFormat> FormatOption() => new("--format")
    {
        Description = "Output format: table, json, or csv.",
        DefaultValueFactory = _ => OutputFormat.Table,
    };

    private static Option<FileInfo?> OutputOption() => new("--output")
    {
        Description = "Write output to a file instead of stdout.",
    };

    private static async Task WriteOutputAsync(string content, FileInfo? output, CancellationToken cancellationToken)
    {
        if (output is null)
        {
            Console.Out.Write(content);
            return;
        }
        Directory.CreateDirectory(output.DirectoryName!);
        await File.WriteAllTextAsync(output.FullName, content, cancellationToken);
    }

    private static async Task<int> ExecuteAsync(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("sizospy: canceled.");
            return 130;
        }
        catch (SizospyException ex)
        {
            Console.Error.WriteLine($"sizospy: {ex.Message} [{ex.Code}]");
            return ex.Code switch
            {
                "input-not-found" or "invalid-output" or "output-conflict" => 3,
                "invalid-mstat" or "unsupported-mstat-version" or "malformed-dgml" or "malformed-map" => 4,
                "schema-mismatch" or "database-read" or "database-write" or "database-io" => 5,
                _ => 1,
            };
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"sizospy: I/O error: {ex.Message}");
            return 6;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"sizospy: access error: {ex.Message}");
            return 6;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"sizospy: invalid path or option value: {ex.Message}");
            return 3;
        }
        catch (NotSupportedException ex)
        {
            Console.Error.WriteLine($"sizospy: unsupported path or operation: {ex.Message}");
            return 3;
        }
    }
}
