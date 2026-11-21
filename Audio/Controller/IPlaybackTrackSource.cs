using System;
using AuraAudio.Core.Models;

namespace AuraAudio.Audio.Controller;

/// <summary>
/// Read-only playback track source observed by UI components such as the Audio Inspector.
/// Provides a single source of truth for the currently active playback track and hardware path context.
/// </summary>
public interface IPlaybackTrackSource
{
    /// <summary>
    /// The track currently loaded or playing in the player, or null when no track is loaded.
    /// </summary>
    Track? CurrentTrack { get; }

    /// <summary>
    /// Raised whenever <see cref="CurrentTrack"/> changes (start, gapless transition, or session restore).
    /// May be raised on a background thread; UI subscribers must marshal updates as needed.
    /// </summary>
    event EventHandler<Track?>? CurrentTrackChanged;

    /// <summary>
    /// Returns the live hardware audit context for the specified audio file path.
    /// </summary>
    (AudioStreamSpec? HardwareSpec, bool HasActivePath, bool IsExclusive, AudioDriverType DriverType) GetHardwareAuditContext(string? targetFilePath);
}
