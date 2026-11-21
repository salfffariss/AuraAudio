using System;
using System.IO;

namespace AuraAudio.Core;

/// <summary>
/// Single source of truth for all user-writable runtime storage (SQLite database,
/// thumbnail disk cache, and diagnostic crash/startup logs). Always resolves outside
/// the application install directory so both per-user (%LocalAppData%\Programs\AuraAudio)
/// and system-wide (C:\Program Files\AuraAudio) installations remain read-only safe.
/// </summary>
public static class AppDataPaths
{
    public const string DataDirEnvironmentVariable = "AURA_DATA_DIR";

    public static string RootDirectory
    {
        get
        {
            string? envOverride = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envOverride))
            {
                return Path.GetFullPath(envOverride.Trim());
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuraAudio");
        }
    }

    public static string LibraryDatabasePath => Path.Combine(RootDirectory, "library.db");

    public static string ThumbnailCacheDirectory => Path.Combine(RootDirectory, "Cache", "Thumbnails");

    public static string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    public static string CrashLogPath => Path.Combine(LogsDirectory, "aura_crash.log");

    public static string StartupErrorLogPath => Path.Combine(LogsDirectory, "aura_startup_error.log");

    public static string EnsureRootDirectory()
    {
        string dir = RootDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string EnsureThumbnailCacheDirectory()
    {
        string dir = ThumbnailCacheDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string EnsureLogsDirectory()
    {
        string dir = LogsDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
