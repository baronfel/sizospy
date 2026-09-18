using System.Reflection;
using System.Runtime.InteropServices;
using SQLitePCL;
using NativeLibrary = System.Runtime.InteropServices.NativeLibrary;

namespace Sizospy.Storage;

internal static class SqliteRuntime
{
    public const int StrictTablesVersion = 3_037_000;

    private static readonly object Sync = new();
    private static bool s_initialized;
    private static int s_versionNumber;
    private static nint s_libraryHandle;

    public static int VersionNumber
    {
        get
        {
            EnsureInitialized();
            return s_versionNumber;
        }
    }

    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref s_initialized))
        {
            return;
        }

        lock (Sync)
        {
            if (s_initialized)
            {
                return;
            }

            try
            {
                ISQLite3Provider provider;
                if (OperatingSystem.IsWindows())
                {
                    provider = new SQLite3Provider_winsqlite3();
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    s_libraryHandle = LoadSystemLibrary();
                    NativeLibrary.SetDllImportResolver(
                        typeof(SQLite3Provider_sqlite3).Assembly,
                        ResolveSqliteLibrary);
                    provider = new SQLite3Provider_sqlite3();
                }
                else
                {
                    throw new PlatformNotSupportedException(
                        $"System SQLite is not configured for '{RuntimeInformation.OSDescription}'.");
                }

                raw.SetProvider(provider);
                raw.FreezeProvider();
                s_versionNumber = raw.sqlite3_libversion_number();
                Volatile.Write(ref s_initialized, true);
            }
            catch (DllNotFoundException ex)
            {
                throw ProviderError(ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw ProviderError(ex);
            }
            catch (BadImageFormatException ex)
            {
                throw ProviderError(ex);
            }
        }
    }

    public static bool SupportsStrictTables(int versionNumber) =>
        versionNumber >= StrictTablesVersion;

    internal static string[] GetLibraryNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return ["winsqlite3.dll"];
        }

        if (OperatingSystem.IsLinux())
        {
            return ["libsqlite3.so.0", "libsqlite3.so"];
        }

        if (OperatingSystem.IsMacOS())
        {
            return ["/usr/lib/libsqlite3.dylib", "libsqlite3.dylib"];
        }

        throw new PlatformNotSupportedException(
            $"System SQLite is not configured for '{RuntimeInformation.OSDescription}'.");
    }

    private static nint LoadSystemLibrary()
    {
        string[] libraryNames = GetLibraryNames();
        foreach (string libraryName in libraryNames)
        {
            if (NativeLibrary.TryLoad(libraryName, out nint libraryHandle))
            {
                return libraryHandle;
            }
        }

        throw new DllNotFoundException(
            $"None of the system SQLite libraries could be loaded: {string.Join(", ", libraryNames)}.");
    }

    private static nint ResolveSqliteLibrary(
        string libraryName,
        Assembly _,
        DllImportSearchPath? __) =>
        libraryName == "sqlite3" ? s_libraryHandle : 0;

    private static SizospyException ProviderError(Exception innerException) =>
        new(
            $"Unable to load a compatible system SQLite library on '{RuntimeInformation.OSDescription}'. " +
            "Install SQLite through the operating system and try again.",
            "sqlite-provider",
            innerException);
}
