using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Storage.Streams;
using AuraAudio.Audio.Controller;
using AuraAudio.Core.Models;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Manajer integrasi Windows 11 System Media Transport Controls (SMTC) dan Hardware Media Keys.
/// Menampilkan overlay Windows 11 saat tombol Fn/Media ditekan (Judul, Artis, Album Art, Play/Pause/Next/Prev).
/// </summary>
public class WindowsMediaControlsManager : IDisposable
{
    [ComImport]
    [Guid("ea131877-7ee5-4e45-a2b5-947ce49e0a4a")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISystemMediaTransportControlsInterop
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
    }

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    private static readonly Guid SmtcGuid = new("99848243-8644-4324-82ec-a30bebe5b1c6");
    private static readonly Guid InteropGuid = new("ea131877-7ee5-4e45-a2b5-947ce49e0a4a");

    private readonly AudiophilePlaybackController _controller;
    private SystemMediaTransportControls? _smtc;
    private bool _isDisposed;

    public WindowsMediaControlsManager(IntPtr windowHandle, AudiophilePlaybackController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        try
        {
            InitializeSmtc(windowHandle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SMTC Init Warning] Gagal inisialisasi SMTC Windows: {ex.Message}");
        }
    }

    private void InitializeSmtc(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;

        var interopGuid = InteropGuid;
        int hr = RoGetActivationFactory("Windows.Media.SystemMediaTransportControls", ref interopGuid, out IntPtr factoryPtr);
        if (hr != 0 || factoryPtr == IntPtr.Zero) return;

        try
        {
            var interop = (ISystemMediaTransportControlsInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            var smtcGuid = SmtcGuid;
            var obj = interop.GetForWindow(windowHandle, ref smtcGuid);
            _smtc = obj as SystemMediaTransportControls;

            if (_smtc != null)
            {
                _smtc.IsEnabled = true;
                _smtc.IsPlayEnabled = true;
                _smtc.IsPauseEnabled = true;
                _smtc.IsNextEnabled = true;
                _smtc.IsPreviousEnabled = true;
                _smtc.IsStopEnabled = true;

                _smtc.ButtonPressed += OnSmtcButtonPressed;
                _smtc.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                _controller.Play();
                break;
            case SystemMediaTransportControlsButton.Pause:
                _controller.Pause();
                break;
            case SystemMediaTransportControlsButton.Next:
                _ = _controller.NextAsync();
                break;
            case SystemMediaTransportControlsButton.Previous:
                _ = _controller.PreviousAsync();
                break;
            case SystemMediaTransportControlsButton.Stop:
                _controller.Stop();
                break;
        }
    }

    private void OnPlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        _controller.Seek(args.RequestedPlaybackPosition);
    }

    /// <summary>
    /// Memperbarui metadata lagu yang sedang berputar di overlay Windows 11 SMTC.
    /// </summary>
    public void UpdateTrackMetadata(Track? track, PlaybackStatus status)
    {
        if (_smtc == null) return;

        try
        {
            // Update Playback Status
            _smtc.PlaybackStatus = status switch
            {
                PlaybackStatus.Playing => MediaPlaybackStatus.Playing,
                PlaybackStatus.Paused => MediaPlaybackStatus.Paused,
                PlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
                _ => MediaPlaybackStatus.Closed
            };

            if (track == null) return;

            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = track.Artist;
            updater.MusicProperties.AlbumTitle = track.Album;

            if (!string.IsNullOrEmpty(track.CoverArtCacheKey) && File.Exists(track.CoverArtCacheKey))
            {
                try
                {
                    var fileUri = new Uri(track.CoverArtCacheKey);
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(fileUri);
                }
                catch
                {
                    // Abaikan jika thumbnail gagal dibuat
                }
            }

            updater.Update();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SMTC Update Warning] {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_smtc != null)
        {
            _smtc.ButtonPressed -= OnSmtcButtonPressed;
            _smtc.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
            _smtc.IsEnabled = false;
        }
    }
}
