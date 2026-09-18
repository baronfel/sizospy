using Microsoft.Data.Sqlite;
using Sizospy.Import;

namespace Sizospy.Core.Tests;

[TestClass]
public sealed class ImportServiceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OmittedOptionalInputsAreDiscoveredBesideMstat()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sizospy-discovery-{Guid.NewGuid():N}");
        var mstat = Path.Combine(directory, "Sample.mstat");
        var database = Path.Combine(directory, "Sample.sizospy.db");
        Directory.CreateDirectory(directory);

        try
        {
            MstatFixtureFactory.Write(mstat);
            File.Copy(Fixture("scan.dgml.xml"), Path.Combine(directory, "Sample.scan.dgml.xml"));
            File.Copy(Fixture("map.xml"), Path.Combine(directory, "Sample.map.xml"));
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "Sample.exe"),
                new byte[2048],
                TestContext.CancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "Sample.dll"),
                new byte[1024],
                TestContext.CancellationToken);

            var result = await new ImportService().ImportAsync(
                new ImportRequest(mstat, database),
                cancellationToken: TestContext.CancellationToken);

            Assert.IsTrue(result.GraphAvailable);
            Assert.AreEqual(2048L, result.BinarySize);
            Assert.AreEqual("dgml,map,binary", await ReadMetadataAsync(database, "auto_discovered_inputs"));
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(directory, "Sample.map.xml")),
                await ReadMetadataAsync(database, "map_path"));
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(directory, "Sample.exe")),
                await ReadMetadataAsync(database, "binary_path"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private async Task<string?> ReadMetadataAsync(string database, string key)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM import_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(TestContext.CancellationToken);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
