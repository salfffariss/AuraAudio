using System;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Data peristiwa saat terjadi transisi lagu gapless.
/// </summary>
public record TrackTransitionEventArgs(IAudioStream PreviousTrack, IAudioStream CurrentTrack);

/// <summary>
/// Antrean Aliran Audio Gapless (Gapless Stream Queue) Kelas Audiophile.
/// Menggabungkan dua atau lebih berkas audio secara presisi pada level sampel (Sample-Exact Stitching)
/// tanpa jeda sunyi (silence gap), tanpa reset buffer hardware DAC, dan dilengkapi
/// perlindungan Anti-Pop Micro-Fade pada saat pergantian sample rate.
/// </summary>
public class GaplessStreamQueue : IAudioStream
{
    private readonly object _lock = new();
    private IAudioStream _currentStream;
    private IAudioStream? _nextStream;
    private bool _isFirstBuffer = true;
    private bool _isDisposed;

    public IAudioStream CurrentStream
    {
        get { lock (_lock) return _currentStream; }
    }

    public IAudioStream? NextStream
    {
        get { lock (_lock) return _nextStream; }
    }

    // Delegasi properti IAudioStream ke lagu yang sedang aktif diputar
    public string FilePath => CurrentStream.FilePath;
    public AudioStreamSpec Spec => CurrentStream.Spec;
    public WaveFormat WaveFormat => CurrentStream.WaveFormat;
    public TimeSpan TotalTime => CurrentStream.TotalTime;
    public TimeSpan CurrentTime
    {
        get => CurrentStream.CurrentTime;
        set => CurrentStream.CurrentTime = value;
    }
    public long Position
    {
        get => CurrentStream.Position;
        set => CurrentStream.Position = value;
    }
    public long Length => CurrentStream.Length;

    /// <summary>Ditembakkan saat lagu berpindah secara mulus (Gapless Seamless Transition)</summary>
    public event EventHandler<TrackTransitionEventArgs>? TrackTransitioned;

    /// <summary>Ditembakkan saat lagu berikutnya memerlukan negosiasi ulang sample rate hardware</summary>
    public event EventHandler<IAudioStream>? FormatChangeRequired;

    /// <summary>Ditembakkan saat seluruh antrean lagu telah selesai diputar</summary>
    public event EventHandler? QueueEnded;

    public GaplessStreamQueue(IAudioStream initialStream)
    {
        _currentStream = initialStream ?? throw new ArgumentNullException(nameof(initialStream));
    }

    /// <summary>
    /// Memasang lagu berikutnya ke dalam memori untuk siap disambung secara gapless.
    /// </summary>
    public void EnqueueNext(IAudioStream nextStream)
    {
        ArgumentNullException.ThrowIfNull(nextStream);
        lock (_lock)
        {
            _nextStream?.Dispose();
            _nextStream = nextStream;
        }
    }

    /// <summary>
    /// Menghapus antrean lagu berikutnya jika dibatalkan oleh pengguna.
    /// </summary>
    public void ClearNext()
    {
        lock (_lock)
        {
            _nextStream?.Dispose();
            _nextStream = null;
        }
    }

    /// <summary>
    /// Membaca sampel audio dari antrean dengan penyambungan gapless otomatis di batas lagu.
    /// </summary>
    /// <summary>
    /// Membaca sampel audio dari antrean dengan penyambungan gapless otomatis di batas lagu.
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return 0;

        return Read(new Span<byte>(buffer, offset, count));
    }

    /// <summary>
    /// Membaca sampel audio ke modern Span<byte> dengan penyambungan gapless.
    /// Mengumpulkan seluruh frame audio secara kontinu dan hanya mentrigger EOF saat stream benar-benar habis (read == 0).
    /// Notifikasi pergantian trek atau akhir antrean dikirim ke ThreadPool agar thread audio WASAPI tidak pernah deadlock.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;

        int totalRead = 0;

        lock (_lock)
        {
            while (totalRead < destination.Length)
            {
                int needed = destination.Length - totalRead;
                int read = _currentStream.Read(destination.Slice(totalRead, needed));

                if (read > 0)
                {
                    if (_isFirstBuffer)
                    {
                        byte[] temp = new byte[read];
                        destination.Slice(totalRead, read).CopyTo(temp);
                        AntiPopFader.ApplyFadeIn(temp, 0, read, _currentStream.WaveFormat);
                        temp.AsSpan().CopyTo(destination.Slice(totalRead, read));
                        _isFirstBuffer = false;
                    }

                    totalRead += read;
                }
                else
                {
                    // read <= 0 menunjukkan akhir sebenarnya (True EOF) dari trek aktif
                    if (_nextStream != null)
                    {
                        if (FormatMatches(_currentStream.WaveFormat, _nextStream.WaveFormat))
                        {
                            var prevTrack = _currentStream;
                            var next = _nextStream;
                            _currentStream = next;
                            _nextStream = null;

                            // Notifikasi pergantian trek secara asinkron agar tidak memblokir thread audio
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                            {
                                TrackTransitioned?.Invoke(this, new TrackTransitionEventArgs(prevTrack, next));
                                prevTrack.Dispose();
                            });

                            // Lanjutkan membaca dari trek baru untuk mengisi sisa buffer
                            continue;
                        }
                        else
                        {
                            // Format sample rate atau bit depth berbeda: lakukan fade-out halus
                            if (totalRead > 0)
                            {
                                byte[] temp = new byte[totalRead];
                                destination.Slice(0, totalRead).CopyTo(temp);
                                AntiPopFader.ApplyFadeOut(temp, 0, totalRead, _currentStream.WaveFormat);
                                temp.AsSpan().CopyTo(destination.Slice(0, totalRead));
                            }

                            var next = _nextStream;
                            _nextStream = null;
                            System.Threading.ThreadPool.QueueUserWorkItem(_ => FormatChangeRequired?.Invoke(this, next));
                            break;
                        }
                    }
                    else
                    {
                        // Akhir dari antrean pemutaran
                        if (totalRead > 0)
                        {
                            byte[] temp = new byte[totalRead];
                            destination.Slice(0, totalRead).CopyTo(temp);
                            AntiPopFader.ApplyFadeOut(temp, 0, totalRead, _currentStream.WaveFormat);
                            temp.AsSpan().CopyTo(destination.Slice(0, totalRead));
                        }

                        System.Threading.ThreadPool.QueueUserWorkItem(_ => QueueEnded?.Invoke(this, EventArgs.Empty));
                        break;
                    }
                }
            }
        }

        return totalRead;
    }

    private static bool FormatMatches(WaveFormat a, WaveFormat b)
    {
        return a.SampleRate == b.SampleRate
            && a.BitsPerSample == b.BitsPerSample
            && a.Channels == b.Channels
            && a.Encoding == b.Encoding;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (_lock)
        {
            _currentStream.Dispose();
            _nextStream?.Dispose();
            _nextStream = null;
        }

        GC.SuppressFinalize(this);
    }
}
