# Sizospy

Sizospy analyzes size and reachability in .NET NativeAOT applications.

It imports NativeAOT diagnostic artifacts into SQLite. It then produces deterministic command-line reports and a linked offline web report.

## Requirements

- .NET SDK 11 or later for the current package
- A RID-specific `sizospy` package for the operating system
- The operating system's SQLite runtime library (`libsqlite3.so.0` on Linux)
- An MSTAT file from a NativeAOT publish
- A scan DGML file for retained-size analysis

## Install and run

Run the tool without a permanent installation:

```powershell
dnx sizospy -- --help
```

Pin a package version when you need reproducible analysis:

```powershell
dnx sizospy@0.1.0 -- --help
```

The NuGet package ID and command name are both `sizospy`.

## Produce NativeAOT inputs

Add these properties to the application project:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <IlcGenerateMstatFile>true</IlcGenerateMstatFile>
  <IlcGenerateDgmlFile>true</IlcGenerateDgmlFile>
  <IlcGenerateMapFile>true</IlcGenerateMapFile>
</PropertyGroup>
```

Publish for the target runtime identifier:

```powershell
dotnet publish MyApp.csproj -c Release -r win-x64
```

The NativeAOT intermediate directory contains these files:

- `MyApp.mstat` contains structured size records.
- `MyApp.scan.dgml.xml` contains dependency and root information.
- `MyApp.codegen.dgml.xml` contains the later code-generation graph.
- `MyApp.map.xml` contains emitted object records.

Use the scan DGML file with `--dgml`. Sizospy does not use the code-generation DGML file in this release.

Sizospy parses DGML and map XML as local, namespace-agnostic streams. It does not resolve DTDs,
external entities, namespace URLs, or other network resources.

Sizospy uses the SQLite library supplied by the operating system. Windows uses `winsqlite3.dll`,
Linux uses `libsqlite3.so.0` (or `libsqlite3.so`), and macOS uses `/usr/lib/libsqlite3.dylib`.
SQLite 3.37 and later use strict tables, while older versions receive the same schema without the
optional `STRICT` table qualifier.

## Import

Create a database from MSTAT only:

```powershell
dnx sizospy -- import `
  --mstat .\MyApp.mstat `
  --output .\MyApp.sizospy.db
```

When an optional input is omitted, Sizospy searches the MSTAT directory for matching companion files.

It recognizes `<name>.scan.dgml.xml`, `<name>.map.xml`, and common native binary names for Windows, Linux, and macOS.

If multiple native binaries match, Sizospy records a warning and requires `--binary` for binary reconciliation.

Create a complete database with reachability, physical layout, and binary reconciliation:

```powershell
dnx sizospy -- import `
  --mstat .\MyApp.mstat `
  --dgml .\MyApp.scan.dgml.xml `
  --map .\MyApp.map.xml `
  --binary .\MyApp.exe `
  --output .\MyApp.sizospy.db
```

Use `--force` to replace an existing database. Sizospy writes a temporary sibling database before it replaces the output.

MSTAT is required. Sizospy currently supports MSTAT assembly format versions 2.0 through 2.2.

### Physical size sources

MSTAT contains aggregate size records. The NativeAOT map contains emitted object records that can overlap those aggregates.

When a map is present, Sizospy uses map records as the physical size source. MSTAT nodes keep identity and ownership with zero weight.

Each map record becomes a separate compiler artifact. This model counts each emitted object once.

Without a map, Sizospy uses the non-overlapping MSTAT aggregate sizes. MSTAT 2.1+ detailed RVA field,
frozen object, and manifest resource records are retained for identity and ownership but receive zero
physical weight because the same bytes are also present in the 2.x `Blobs` stream. The database still
supports summary and member reports.

## Report

The `report` command has four subcommands. Pass the database option before the subcommand.

Show import coverage and size reconciliation:

```powershell
dnx sizospy -- report -d .\MyApp.sizospy.db summary
```

Find the largest matching artifacts:

```powershell
dnx sizospy -- report -d .\MyApp.sizospy.db members `
  --name Http `
  --assembly System.Net.Http `
  --limit 25 `
  --sort self
```

Show the largest retained dominators:

```powershell
dnx sizospy -- report -d .\MyApp.sizospy.db dominators `
  --limit 50 `
  --sort retained
```

Create a self-contained offline report:

```powershell
dnx sizospy -- report -d .\MyApp.sizospy.db web `
  --output .\MyApp-size.html
```

`summary`, `members`, and `dominators` support `--format table`, `--format json`, and `--format csv`.

Use `--output <file>` to write structured output to a file. Progress and diagnostics go to standard error.

Member and dominator filters include `--name`, `--kind`, `--assembly`, and `--namespace`.

## Metrics

Sizospy adds a synthetic super-root and connects it to each graph root. It computes immediate dominators with the Lengauer-Tarjan algorithm.

The scan DGML edge direction is `Source -> Target`. The source node depends on the target node.

| Metric | Meaning |
|---|---|
| Self size | Physical bytes assigned directly to one node |
| Retained size | Self size in the node's dominator subtree |
| Marginal retained size | Retained size minus the retained sizes of immediate dominated children |
| Leverage | Retained size divided by self size |
| Dominated count | Number of descendants in the dominator tree |
| Root distance | Edge distance from the synthetic root |

Disconnected graph components receive a synthetic-root connection. Zero-size graph nodes can still dominate physical compiler artifacts.

**Retained size is a graph-model estimate, not guaranteed savings after recompilation.**

Conditional dependencies, inlining, generic sharing, folding, and changed code generation can change the final binary.

Current DGML files do not always identify complete conditional or hyperedge groups. Sizospy preserves available reason and grouping metadata.

## Offline web report

The web report does not use a server, CDN, or network dependency. Private build data stays in the generated HTML file.

The report includes these coordinated views:

- An ownership treemap from assembly to namespace, type, member, and compiler artifact
- A dominator icicle weighted by retained size
- A sortable and searchable metrics table
- A focused incoming and outgoing dependency explanation

Select a node in one view to select it in the other views. Search and kind filters apply to all views.

The HTML embeds positive-size nodes, positive-retained nodes, roots, and their connecting edges. Zero-size nodes without size influence remain queryable through the CLI.

The table displays at most 2,000 matching rows. The treemap displays the 5,000 largest matching physical artifacts.

## SQLite schema

The database enables foreign keys and records its format in `schema_info`.

| Table | Content |
|---|---|
| `schema_info` | Schema version |
| `logical_members` | Assembly, namespace, type, and member ownership |
| `nodes` | Managed and compiler artifacts with physical weights |
| `edges` | Directed dependencies, reasons, and conditional groups |
| `roots` | Imported graph roots |
| `emitted_ranges` | Map sections, object names, content hashes, addresses, and sizes |
| `import_metadata` | Input paths, format versions, and size reconciliation |
| `import_diagnostics` | Import warnings and information |
| `dominators` | Immediate dominators and retained metrics |

Stable IDs derive from compiler identities and logical ownership. Numeric IDs are deterministic within one import.

## Build and test

NuGet package versions are centralized in `Directory.Packages.props`. `global.json` pins the .NET and MSTest SDKs.

`CHANGELOG.md` is the source for the tool version and NuGet release notes. The
`Ionide.KeepAChangelog.Tasks` package applies the latest released changelog entry during the build.

`NuGet.Config` maps all packages to NuGet.org. Tests use MSTest with Microsoft Testing Platform.

Restore and test the solution:

```powershell
dotnet restore Sizospy.slnx /bl:artifacts\log\restore.binlog
dotnet test Sizospy.slnx -c Release --no-restore /bl:artifacts\log\test.binlog
```

Sizospy targets `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, and `osx-arm64`. Publish each
NativeAOT executable on its target operating system. For example, on Windows x64:

```powershell
dotnet publish src\Sizospy.Cli\Sizospy.Cli.csproj `
  -c Release `
  -r win-x64 `
  /bl:artifacts\log\publish.binlog
```

Release builds do not generate PDB files. RID packages contain runtime files and generated XML documentation.

## Pack RID-specific SDK tools

Create each NativeAOT implementation package on its target operating system with `-r`. For example,
create the Windows x64 package with:

```powershell
dotnet pack src\Sizospy.Cli\Sizospy.Cli.csproj `
  -c Release `
  -r win-x64 `
  /bl:artifacts\log\pack.binlog
```

The project declares `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, and `osx-arm64`.
Create the pointer package after you create the implementation packages:

```powershell
dotnet pack src\Sizospy.Cli\Sizospy.Cli.csproj `
  -c Release `
  -p:CreateRidSpecificToolPackages=false `
  /bl:artifacts\log\pack-pointer.binlog
```

The property prevents the pointer pack from rebuilding compatible RID packages on the current host.

Test version 0.1.0 from the local package output:

```powershell
dnx sizospy@0.1.0 `
  --source .\artifacts\package\release `
  --yes `
  -- --help
```

## Release

Move the release changes from `Unreleased` to a dated version in `CHANGELOG.md`.
The version must use Semantic Versioning.

Push a matching `v<version>` tag. The `release.yml` workflow builds these packages on matching hosted runners:

- `sizospy.win-x64`
- `sizospy.win-arm64`
- `sizospy.linux-x64`
- `sizospy.linux-arm64`
- `sizospy.osx-arm64`
- `sizospy`, which is the pointer package

The workflow publishes the five RID packages before it publishes the pointer package.
Pull requests and `main` pushes build the same package set without publishing it.

Configure trusted publishing before the first release:

1. Create a GitHub environment named `nuget.org`.
2. Add the `NUGET_USER` variable to that environment.
3. Create a NuGet.org trusted publishing policy for repository `baronfel/sizospy`.
4. Set the policy workflow file to `release.yml`.
5. Set the policy environment to `nuget.org`.
6. Give the policy access to `sizospy` and its RID package IDs.

The publish job uses GitHub OIDC and a short-lived NuGet API key. It does not use a stored NuGet API key.

## Limitations

- This release supports MSTAT versions 2.0 through 2.2.
- Map XML does not always contain addresses. Sizospy stores zero when an address is absent.
- Logical ownership is best effort for compiler-only artifacts.
- Retained size requires scan DGML.
- Retained size does not predict exact bytes removed by a source change.

## License

Sizospy is available under the MIT License.
