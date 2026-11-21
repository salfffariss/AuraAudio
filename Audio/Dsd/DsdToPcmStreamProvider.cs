using System;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio.Dsd;

/// <summary>
/// Penyedia aliran audio DSD-ke-PCM menggunakan Filter Desimasi FIR 128-tap.
/// Mengonversi berkas 1-bit DSD64 (2.8224 MHz) menjadi aliran 24-bit / 88.2 kHz PCM
/// yang kompatibel secara instan dengan semua soundcard, headphone, dan speaker laptop.
/// </summary>
public class DsdToPcmStreamProvider : IAudioStream
{
    private readonly DsfReader _dsfReader;
    private readonly AudioStreamSpec _spec;
    private readonly WaveFormat _waveFormat;

    private readonly byte[] _rawBlockBuffer;
    private readonly byte[] _ch0WorkBuffer;
    private readonly byte[] _ch1WorkBuffer;
    private readonly byte[] _ch0History = new byte[16];
    private readonly byte[] _ch1History = new byte[16];
    private readonly byte[] _pcmBuffer;

    private int _pcmOffset;
    private int _pcmAvailable;
    private long _position;
    private bool _isDisposed;

    public string FilePath => _dsfReader.FilePath;
    public AudioStreamSpec Spec => _spec;
    public WaveFormat WaveFormat => _waveFormat;
    public TimeSpan TotalTime => _dsfReader.Duration;

    /// <summary>
    /// Total ukuran aliran PCM hasil desimasi (24-bit stereo = 6 byte per frame pada 88.2 kHz).
    /// </summary>
    public long Length
    {
        get
        {
            ulong totalPcmSamples = _dsfReader.SampleCount / (ulong)DsdFirFilter.DecimationFactor;
            return (long)(totalPcmSamples * 6);
        }
    }

    /// <summary>
    /// Posisi waktu pemutaran saat ini (Mendukung seeking).
    /// </summary>
    public TimeSpan CurrentTime
    {
        get
        {
            if (Length == 0) return TimeSpan.Zero;
            double ratio = (double)_position / Length;
            return TimeSpan.FromMilliseconds(ratio * TotalTime.TotalMilliseconds);
        }
        set
        {
            _dsfReader.SeekTo(value);
            ResetDecimationState();

            if (TotalTime.TotalMilliseconds > 0)
            {
                double ratio = Math.Clamp(value.TotalMilliseconds / TotalTime.TotalMilliseconds, 0.0, 1.0);
                long targetPos = (long)(ratio * Length);
                _position = (targetPos / 6) * 6;
            }
            else
            {
                _position = 0;
            }
        }
    }

    /// <summary>
    /// Posisi byte dalam aliran PCM hasil konversi.
    /// </summary>
    public long Position
    {
        get => _position;
        set
        {
            long clamped = Math.Clamp(value, 0, Length);
            clamped = (clamped / 6) * 6;
            _position = clamped;

            if (Length > 0)
            {
                double ratio = (double)_position / Length;
                TimeSpan targetTime = TimeSpan.FromMilliseconds(ratio * TotalTime.TotalMilliseconds);
                _dsfReader.SeekTo(targetTime);
                ResetDecimationState();
            }
        }
    }

    public DsdToPcmStreamProvider(DsfReader dsfReader)
    {
        _dsfReader = dsfReader ?? throw new ArgumentNullException(nameof(dsfReader));

        // Format PCM keluaran: 24-bit / 88.2 kHz Stereo
        uint outputSampleRate = _dsfReader.SampleRate / (uint)DsdFirFilter.DecimationFactor;
        _waveFormat = new WaveFormat((int)outputSampleRate, 24, 2);

        // Spesifikasi audio untuk Signal Path & Authenticity Engine (file average bitrate konsisten)
        long fileSizeBytes = System.IO.File.Exists(_dsfReader.FilePath)
            ? new System.IO.FileInfo(_dsfReader.FilePath).Length
            : 0;
        int dsdStreamKbps = (int)Math.Round(_dsfReader.SampleRate * (double)_dsfReader.Channels / 1000.0);
        uint bitrateKbps = AudioSourceFormatResolver.ComputeFileAverageBitrateKbps(
            fileSizeBytes,
            _dsfReader.Duration,
            dsdStreamKbps);
        _spec = new AudioStreamSpec(
            AudioCodec.DSD_DSF,
            _dsfReader.SampleRate,
            BitDepth.DSD1Bit,
            _dsfReader.Channels,
            bitrateKbps
        );

        // Ukuran blok DSD
        uint blockSize = _dsfReader.BlockSizePerChannel > 0 ? _dsfReader.BlockSizePerChannel : 4096;
        int rawBufferSize = (int)(blockSize * Math.Max((ushort)1, _dsfReader.Channels));
        _rawBlockBuffer = new byte[rawBufferSize];

        // Buffer kerja per kanal (16 byte riwayat sebelumnya + blok baru)
        _ch0WorkBuffer = new byte[16 + blockSize];
        _ch1WorkBuffer = new byte[16 + blockSize];

        // Inisialisasi riwayat dengan nilai baseline diam DSD (0xAA = 10101010)
        Array.Fill(_ch0History, (byte)0xAA);
        Array.Fill(_ch1History, (byte)0xAA);

        // Setiap 4 byte DSD per kanal (32 bit) menghasilkan 1 sampel stereo (6 byte PCM)
        int maxPcmSamples = (int)(blockSize / (DsdFirFilter.DecimationFactor / 8));
        _pcmBuffer = new byte[maxPcmSamples * 6];
    }

    /// <summary>
    /// Membaca sampel PCM hasil desimasi FIR ke buffer byte konvensional.
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) return 0;

        int totalBytesRead = 0;

        while (totalBytesRead < count)
        {
            int availableInPcm = _pcmAvailable - _pcmOffset;
            if (availableInPcm > 0)
            {
                int toCopy = Math.Min(availableInPcm, count - totalBytesRead);
                Array.Copy(_pcmBuffer, _pcmOffset, buffer, offset + totalBytesRead, toCopy);
                _pcmOffset += toCopy;
                totalBytesRead += toCopy;
                _position += toCopy;
                continue;
            }

            bool hasMore = DecimateNextBlock();
            if (!hasMore) break;
        }

        return totalBytesRead;
    }

    /// <summary>
    /// Membaca sampel PCM ke span memori modern tanpa alokasi heap.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;

        int totalBytesRead = 0;

        while (totalBytesRead < destination.Length)
        {
            int availableInPcm = _pcmAvailable - _pcmOffset;
            if (availableInPcm > 0)
            {
                int toCopy = Math.Min(availableInPcm, destination.Length - totalBytesRead);
                _pcmBuffer.AsSpan(_pcmOffset, toCopy).CopyTo(destination.Slice(totalBytesRead, toCopy));
                _pcmOffset += toCopy;
                totalBytesRead += toCopy;
                _position += toCopy;
                continue;
            }

            bool hasMore = DecimateNextBlock();
            if (!hasMore) break;
        }

        return totalBytesRead;
    }

    /// <summary>
    /// Membaca blok saluran DSD dan menerapkan konvolusi FIR 128-tap menjadi sampel PCM 24-bit.
    /// </summary>
    private bool DecimateNextBlock()
    {
        int bytesRead = _dsfReader.ReadRawDsd(_rawBlockBuffer, 0, _rawBlockBuffer.Length);
        if (bytesRead <= 0)
        {
            _pcmAvailable = 0;
            _pcmOffset = 0;
            return false;
        }

        int channels = _dsfReader.Channels;
        int blockSize = (int)_dsfReader.BlockSizePerChannel;
        int validBytesPerChannel = (bytesRead >= _rawBlockBuffer.Length)
            ? blockSize
            : (bytesRead / Math.Max(1, channels));

        // Salin 16 byte riwayat sebelumnya ke awal buffer kerja
        _ch0History.CopyTo(_ch0WorkBuffer, 0);
        _ch1History.CopyTo(_ch1WorkBuffer, 0);

        // Salin blok DSD baru ke buffer kerja
        Array.Copy(_rawBlockBuffer, 0, _ch0WorkBuffer, 16, validBytesPerChannel);
        if (channels > 1)
        {
            Array.Copy(_rawBlockBuffer, blockSize, _ch1WorkBuffer, 16, validBytesPerChannel);
        }
        else
        {
            Array.Copy(_rawBlockBuffer, 0, _ch1WorkBuffer, 16, validBytesPerChannel);
        }

        // Simpan 16 byte terakhir untuk kesinambungan blok berikutnya (mencegah klik antarbblok)
        int lastHistoryOffset = 16 + validBytesPerChannel - 16;
        if (lastHistoryOffset >= 0)
        {
            Array.Copy(_ch0WorkBuffer, lastHistoryOffset, _ch0History, 0, 16);
            Array.Copy(_ch1WorkBuffer, lastHistoryOffset, _ch1History, 0, 16);
        }

        // Jalankan desimasi FIR: Setiap 4 byte DSD (32 bit) menghasilkan 1 sampel PCM
        int outputSamples = validBytesPerChannel / 4;
        int pcmIdx = 0;

        for (int i = 0; i < outputSamples; i++)
        {
            int dsdOffset = i * 4;

            // Konvolusi 128-tap Sinc dengan Jendela Blackman-Harris via Tabel L1
            float leftSample = DsdFirFilter.Convolve128(_ch0WorkBuffer.AsSpan(dsdOffset, 16));
            float rightSample = (channels > 1)
                ? DsdFirFilter.Convolve128(_ch1WorkBuffer.AsSpan(dsdOffset, 16))
                : leftSample;

            // Kuantisasi ke format Little-Endian 24-bit PCM (Rentang -8.388.608 s.d. 8.388.607)
            int leftInt24 = (int)(leftSample * 8_388_607.0f);
            leftInt24 = Math.Clamp(leftInt24, -8_388_608, 8_388_607);
            _pcmBuffer[pcmIdx++] = (byte)(leftInt24 & 0xFF);
            _pcmBuffer[pcmIdx++] = (byte)((leftInt24 >> 8) & 0xFF);
            _pcmBuffer[pcmIdx++] = (byte)((leftInt24 >> 16) & 0xFF);

            int rightInt24 = (int)(rightSample * 8_388_607.0f);
            rightInt24 = Math.Clamp(rightInt24, -8_388_608, 8_388_607);
            _pcmBuffer[pcmIdx++] = (byte)(rightInt24 & 0xFF);
            _pcmBuffer[pcmIdx++] = (byte)((rightInt24 >> 8) & 0xFF);
            _pcmBuffer[pcmIdx++] = (byte)((rightInt24 >> 16) & 0xFF);
        }

        _pcmAvailable = pcmIdx;
        _pcmOffset = 0;
        return _pcmAvailable > 0;
    }

    private void ResetDecimationState()
    {
        _pcmOffset = 0;
        _pcmAvailable = 0;
        Array.Fill(_ch0History, (byte)0xAA);
        Array.Fill(_ch1History, (byte)0xAA);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _dsfReader.Dispose();
        GC.SuppressFinalize(this);
    }
}
