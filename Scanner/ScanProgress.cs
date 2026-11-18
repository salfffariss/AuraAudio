using System;

namespace AuraAudio.Scanner;

/// <summary>
/// Laporan progres pemindaian folder musik yang dikirim secara asinkron ke UI / Konsol.
/// </summary>
public record ScanProgress
{
    public int TotalFound { get; init; }
    public int Processed { get; init; }
    public int Added { get; init; }
    public int Skipped { get; init; }
    public string CurrentFile { get; init; }
    public TimeSpan Elapsed { get; init; }

    public double Percentage => TotalFound > 0 ? (Processed * 100.0) / TotalFound : 0.0;
    public double TracksPerSecond => Elapsed.TotalSeconds > 0 ? Processed / Elapsed.TotalSeconds : 0.0;

    public ScanProgress(
        int totalFound,
        int processed,
        int added,
        int skipped,
        string currentFile,
        TimeSpan elapsed)
    {
        TotalFound = totalFound;
        Processed = processed;
        Added = added;
        Skipped = skipped;
        CurrentFile = currentFile;
        Elapsed = elapsed;
    }
}
