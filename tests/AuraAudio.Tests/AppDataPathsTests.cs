using System;
using System.IO;
using AuraAudio.Core;
using AuraAudio.Database;
using AuraAudio.Scanner;
using Xunit;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public sealed class AppDataPathsTests
{
    [Fact]
    public void DefaultRuntimePaths_ResolveUnderLocalAppData_NotInstallBaseDirectory()
    {
        string? prev = Environment.GetEnvironmentVariable(AppDataPaths.DataDirEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataDirEnvironmentVariable, null);

            string expectedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuraAudio");

            Assert.Equal(expectedRoot, AppDataPaths.RootDirectory, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(expectedRoot, "library.db"), AppDataPaths.LibraryDatabasePath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(expectedRoot, "Cache", "Thumbnails"), AppDataPaths.ThumbnailCacheDirectory, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(expectedRoot, "Logs", "aura_crash.log"), AppDataPaths.CrashLogPath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(expectedRoot, "Logs", "aura_startup_error.log"), AppDataPaths.StartupErrorLogPath, StringComparer.OrdinalIgnoreCase);

            string baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            Assert.False(AppDataPaths.ThumbnailCacheDirectory.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase));
            Assert.False(AppDataPaths.CrashLogPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase));
            Assert.False(AppDataPaths.LibraryDatabasePath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataDirEnvironmentVariable, prev);
        }
    }

    [Fact]
    public void EnvironmentOverride_RedirectsDatabaseAndThumbnailCache_WithoutTouchingBaseDirectory()
    {
        string? prev = Environment.GetEnvironmentVariable(AppDataPaths.DataDirEnvironmentVariable);
        string tempRoot = Path.Combine(Path.GetTempPath(), "AuraAudio_DataDirTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataDirEnvironmentVariable, tempRoot);

            var thumbMgr = new ThumbnailCacheManager();
            Assert.Equal(Path.Combine(tempRoot, "Cache", "Thumbnails"), thumbMgr.CacheDirectory, StringComparer.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(thumbMgr.CacheDirectory));

            using var db = new DatabaseManager();
            Assert.Equal(Path.Combine(tempRoot, "library.db"), db.DatabasePath, StringComparer.OrdinalIgnoreCase);
            db.Initialize();
            Assert.True(File.Exists(db.DatabasePath));

            Assert.Equal(Path.Combine(tempRoot, "Logs", "aura_crash.log"), AppDataPaths.CrashLogPath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(tempRoot, "Logs", "aura_startup_error.log"), AppDataPaths.StartupErrorLogPath, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataDirEnvironmentVariable, prev);
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
