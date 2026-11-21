using System;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio.Dsd;

/// <summary>
/// Penyedia aliran DoP (DSD over PCM) v1.1 Open Standard.
/// Mengemas aliran bit DSD 1-bit murni ke dalam wadah PCM 24-bit pada 176.4 kHz (untuk DSD64)
/// dengan penanda (marker) bergantian 0x05 dan 0xFA pada byte MSB.
/// Memungkinkan pemutaran DSD secara bit-perfect melewati WASAPI Exclusive atau ASIO
/// langsung ke DAC audiophile eksternal tanpa konversi PCM lossy.
/// </summary>
public class DopStreamProvider : IAudioStream
{
    // Tabel pembalik bit 8-bit berkecepatan tinggi (Precomputed Bit-Reverse Lookup Table)
    // Standar DSF menyimpan LSB lebih dulu (Bit 0 adalah sampel t0), sedangkan standar DoP v1.1
    // mentransmisikan MSB lebih dulu (Bit 15 adalah sampel t0).
    private static readonly byte[] BitReverseTable = new byte[256];

    static DopStreamProvider()
    {
        for (int i = 0; i < 256; i++)
        {
            int r = 0;
            for (int b = 0; b < 8; b++)
            {
                if ((i & (1 << b)) != 0)
                {
                    r |= (1 << (7 - b));
                }
            }
            BitReverseTable[i] = (byte)r;
        }
    }

    private readonly DsfReader _dsfReader;
    private readonly AudioStreamSpec _spec;
    private readonly WaveFormat _waveFormat;
    private readonly byte[] _rawBlockBuffer;
    private readonly byte[] _dopPcmBuffer;

    private int _dopPcmAvailable;
    private int _dopPcmOffset;
    private byte _currentMarker = 0x05; // DoP v1.1 bergantian: 0x05 <-> 0xFA
    private long _position;
    private bool _isDisposed;

    public string FilePath => _dsfReader.FilePath;
    public AudioStreamSpec Spec => _spec;
    public WaveFormat WaveFormat => _waveFormat;
    public TimeSpan TotalTime => _dsfReader.Duration;

    /// <summary>
    /// Total ukuran aliran data DoP dalam representasi byte PCM (24-bit stereo = 6 byte per frame).
    /// </summary>
    public long Length
    {
        get
        {
            // Setiap 16 sampel DSD per kanal dikemas ke dalam 1 frame DoP stereo (6 byte)
            ulong totalFrames = _dsfReader.SampleCount / 16;
            return (long)(totalFrames * 6);
        }
    }

    /// <summary>
    /// Posisi pemutaran saat ini (Mendukung lompat waktu / Seeking).
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
            _dopPcmOffset = 0;
            _dopPcmAvailable = 0;
            _currentMarker = 0x05;

            if (TotalTime.TotalMilliseconds > 0)
            {
                double ratio = Math.Clamp(value.TotalMilliseconds / TotalTime.TotalMilliseconds, 0.0, 1.0);
                long targetPos = (long)(ratio * Length);
                // Sejajarkan ke batas 6-byte frame
                _position = (targetPos / 6) * 6;
            }
            else
            {
                _position = 0;
            }
        }
    }

    /// <summary>
    /// Posisi byte DoP PCM saat ini.
    /// </summary>
    public long Position
    {
        get => _position;
        set
        {
            long clamped = Math.Clamp(value, 0, Length);
            clamped = (clamped / 6) * 6; // Sejajarkan ke frame
            _position = clamped;

            if (Length > 0)
            {
                double ratio = (double)_position / Length;
                TimeSpan targetTime = TimeSpan.FromMilliseconds(ratio * TotalTime.TotalMilliseconds);
                _dsfReader.SeekTo(targetTime);
                _dopPcmOffset = 0;
                _dopPcmAvailable = 0;
                _currentMarker = 0x05;
            }
        }
    }

    public DopStreamProvider(DsfReader dsfReader)
    {
        _dsfReader = dsfReader ?? throw new ArgumentNullException(nameof(dsfReader));

        // Format keluaran DoP untuk transmisi PCM ke DAC:
        // Frekuensi PCM = Frekuensi DSD / 16 (DSD64 2.8224 MHz -> 176.4 kHz)
        uint pcmSampleRate = _dsfReader.SampleRate / 16;
        _waveFormat = new WaveFormat((int)pcmSampleRate, 24, 2);

        // Spesifikasi fisik asli DSD untuk Authenticity Signal Path UI (file average bitrate konsisten)
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

        // Alokasi buffer blok DSD mentah (ukuran blok saluran × jumlah kanal)
        uint blockSize = _dsfReader.BlockSizePerChannel > 0 ? _dsfReader.BlockSizePerChannel : 4096;
        int rawBufferSize = (int)(blockSize * Math.Max((ushort)1, _dsfReader.Channels));
        _rawBlockBuffer = new byte[rawBufferSize];

        // Alokasi buffer DoP PCM (1 frame stereo butuh 2 byte DSD per channel -> 6 byte PCM)
        int maxFramesPerBlock = (int)(blockSize / 2);
        _dopPcmBuffer = new byte[maxFramesPerBlock * 6];
    }

    /// <summary>
    /// Membaca sampel DoP terenkapsulasi ke dalam buffer PCM NAudio.
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) return 0;

        int totalBytesRead = 0;

        while (totalBytesRead < count)
        {
            // 1. Salin dari buffer DoP PCM yang sudah tersedia
            int availableInDop = _dopPcmAvailable - _dopPcmOffset;
            if (availableInDop > 0)
            {
                int toCopy = Math.Min(availableInDop, count - totalBytesRead);
                Array.Copy(_dopPcmBuffer, _dopPcmOffset, buffer, offset + totalBytesRead, toCopy);
                _dopPcmOffset += toCopy;
                totalBytesRead += toCopy;
                _position += toCopy;
                continue;
            }

            // 2. Jika buffer DoP kosong, baca dan konversi blok DSF berikutnya
            bool hasMoreData = ConvertNextDsfBlock();
            if (!hasMoreData)
            {
                // File DSD telah selesai dibaca (EOF)
                break;
            }
        }

        return totalBytesRead;
    }

    /// <summary>
    /// Dukungan antarmuka modern Read Span<byte> pada NAudio 3.1.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;

        int totalBytesRead = 0;

        while (totalBytesRead < destination.Length)
        {
            int availableInDop = _dopPcmAvailable - _dopPcmOffset;
            if (availableInDop > 0)
            {
                int toCopy = Math.Min(availableInDop, destination.Length - totalBytesRead);
                _dopPcmBuffer.AsSpan(_dopPcmOffset, toCopy).CopyTo(destination.Slice(totalBytesRead, toCopy));
                _dopPcmOffset += toCopy;
                totalBytesRead += toCopy;
                _position += toCopy;
                continue;
            }

            bool hasMoreData = ConvertNextDsfBlock();
            if (!hasMoreData) break;
        }

        return totalBytesRead;
    }

    /// <summary>
    /// Membaca satu blok data kanal dari DSF dan mengemasnya ke format frame DoP 24-bit Little-Endian.
    /// </summary>
    private bool ConvertNextDsfBlock()
    {
        int bytesRead = _dsfReader.ReadRawDsd(_rawBlockBuffer, 0, _rawBlockBuffer.Length);
        if (bytesRead <= 0)
        {
            _dopPcmAvailable = 0;
            _dopPcmOffset = 0;
            return false;
        }

        int channels = _dsfReader.Channels;
        int blockSize = (int)_dsfReader.BlockSizePerChannel;

        int bytesPerChannel = (bytesRead >= _rawBlockBuffer.Length)
            ? blockSize
            : (bytesRead / Math.Max(1, channels));

        // Setiap frame DoP mengonsumsi 2 byte per kanal (16 bit DSD)
        int frames = bytesPerChannel / 2;
        int dopIndex = 0;

        for (int f = 0; f < frames; f++)
        {
            int dsdByteIdx = f * 2;

            // Saluran Kiri (Ch 0)
            byte ch0_b0 = _rawBlockBuffer[dsdByteIdx];
            byte ch0_b1 = _rawBlockBuffer[dsdByteIdx + 1];

            // Saluran Kanan (Ch 1)
            byte ch1_b0 = (channels > 1) ? _rawBlockBuffer[blockSize + dsdByteIdx] : ch0_b0;
            byte ch1_b1 = (channels > 1) ? _rawBlockBuffer[blockSize + dsdByteIdx + 1] : ch0_b1;

            // Paketkan ke 24-bit PCM Little-Endian:
            // Byte 0: LSB (Bit DSD kedua yang dibalik)
            // Byte 1: MID (Bit DSD pertama yang dibalik)
            // Byte 2: MSB (DoP Marker: 0x05 / 0xFA)

            // Sampel Kiri
            _dopPcmBuffer[dopIndex++] = BitReverseTable[ch0_b1];
            _dopPcmBuffer[dopIndex++] = BitReverseTable[ch0_b0];
            _dopPcmBuffer[dopIndex++] = _currentMarker;

            // Sampel Kanan
            _dopPcmBuffer[dopIndex++] = BitReverseTable[ch1_b1];
            _dopPcmBuffer[dopIndex++] = BitReverseTable[ch1_b0];
            _dopPcmBuffer[dopIndex++] = _currentMarker;

            // Ganti marker untuk frame berikutnya (alternating marker)
            _currentMarker = (_currentMarker == 0x05) ? (byte)0xFA : (byte)0x05;
        }

        _dopPcmAvailable = dopIndex;
        _dopPcmOffset = 0;
        return _dopPcmAvailable > 0;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _dsfReader.Dispose();
        GC.SuppressFinalize(this);
    }
}
