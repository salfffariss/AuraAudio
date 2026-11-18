using System;

namespace AuraAudio.Core.Models;

/// <summary>
/// Snapshot status pemutaran audio yang sepenuhnya immutable (Thread-Safe State Snapshot).
/// Menyediakan salinan kondisi mutlak mesin pemutar audio (Playback Engine) pada titik waktu tertentu
/// yang dapat dibaca oleh UI Thread (120 FPS) tanpa resiko race condition atau deadlock dengan Audio Thread.
/// </summary>
public record PlaybackStateSnapshot
{
    public Track? CurrentTrack { get; init; }
    public PlaybackStatus Status { get; init; }
    public TimeSpan CurrentTime { get; init; }
    public TimeSpan TotalTime { get; init; }
    public SignalPathSummary? SignalPath { get; init; }
    public AudioIntegrityReport? IntegrityReport { get; init; }
    public float Volume { get; init; }
    public bool IsMuted { get; init; }
    public bool IsExclusiveLocked { get; init; }
    public AudioDevice? ActiveDevice { get; init; }
    public int QueueIndex { get; init; }
    public int QueueCount { get; init; }
    public PlaybackRepeatMode RepeatMode { get; init; }
    public bool IsShuffleEnabled { get; init; }

    /// <summary>
    /// Rasio posisi pemutaran saat ini (0.0 s.d. 1.0) untuk progress slider UI.
    /// </summary>
    public double ProgressRatio => TotalTime.TotalSeconds > 0
        ? Math.Clamp(CurrentTime.TotalSeconds / TotalTime.TotalSeconds, 0.0, 1.0)
        : 0.0;

    /// <summary>
    /// Format waktu berjalan ramah UI, misal "01:24 / 04:15".
    /// </summary>
    public string FormattedTimeProgress => $"{CurrentTime:mm\\:ss} / {TotalTime:mm\\:ss}";

    /// <summary>
    /// Membuat snapshot keadaan kosong awal (Idle).
    /// </summary>
    public static PlaybackStateSnapshot Empty => new()
    {
        CurrentTrack = null,
        Status = PlaybackStatus.Stopped,
        CurrentTime = TimeSpan.Zero,
        TotalTime = TimeSpan.Zero,
        SignalPath = null,
        IntegrityReport = null,
        Volume = 1.0f,
        IsMuted = false,
        IsExclusiveLocked = false,
        ActiveDevice = null,
        QueueIndex = -1,
        QueueCount = 0,
        RepeatMode = PlaybackRepeatMode.Off,
        IsShuffleEnabled = false
    };
}
