using System;
using System.IO;
using System.Text;

namespace AuraAudio.Audio.Dsd;

/// <summary>
/// Pembaca file DSD berekstensi .DSF (Sony DSD Stream File format).
/// Mengurai chunk DSD, fmt, dan data untuk mengekstrak aliran 1-bit audio murni
/// pada frekuensi mega-hertz (DSD64 2.8224 MHz, DSD128, DSD256).
/// </summary>
public class DsfReader : IDisposable
{
    private readonly FileStream _fileStream;
    private readonly BinaryReader _reader;
    private readonly string _filePath;

    // Header & Format Metadata
    public uint SampleRate { get; private set; }
    public ushort Channels { get; private set; }
    public ushort BitsPerSample { get; private set; }
    public ulong SampleCount { get; private set; }
    public uint BlockSizePerChannel { get; private set; }
    public long DataChunkDataOffset { get; private set; }
    public long DataChunkDataLength { get; private set; }
    public TimeSpan Duration { get; private set; }

    public string FilePath => _filePath;

    public DsfReader(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File DSF tidak ditemukan.", filePath);
        }

        _fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_fileStream, Encoding.ASCII, leaveOpen: true);

        ParseDsfHeaders();
    }

    /// <summary>
    /// Mengurai struktur chunk resmi Sony DSF: 'DSD ', 'fmt ', dan 'data'.
    /// </summary>
    private void ParseDsfHeaders()
    {
        // 1. Chunk 'DSD ' (28 byte)
        string dsdHeader = ReadFourCc();
        if (dsdHeader != "DSD ")
        {
            throw new InvalidDataException($"Header tidak valid: diharapkan 'DSD ', ditemukan '{dsdHeader}'");
        }

        ulong dsdChunkSize = _reader.ReadUInt64();
        ulong totalFileSize = _reader.ReadUInt64();
        ulong metadataOffset = _reader.ReadUInt64();

        // 2. Chunk 'fmt ' (52 byte)
        string fmtHeader = ReadFourCc();
        if (fmtHeader != "fmt ")
        {
            throw new InvalidDataException($"Chunk 'fmt ' tidak ditemukan, ditemukan '{fmtHeader}'");
        }

        ulong fmtChunkSize = _reader.ReadUInt64();
        uint formatVersion = _reader.ReadUInt32();
        uint formatId = _reader.ReadUInt32(); // 0 = DSD raw
        uint channelType = _reader.ReadUInt32(); // 1 = mono, 2 = stereo
        Channels = (ushort)_reader.ReadUInt32();
        SampleRate = _reader.ReadUInt32();
        BitsPerSample = (ushort)_reader.ReadUInt32();
        SampleCount = _reader.ReadUInt64();
        BlockSizePerChannel = _reader.ReadUInt32();
        uint reserved = _reader.ReadUInt32();

        // Lewati sisa fmt chunk jika ada padding
        long fmtRemaining = (long)fmtChunkSize - 52;
        if (fmtRemaining > 0) _fileStream.Seek(fmtRemaining, SeekOrigin.Current);

        // 3. Chunk 'data'
        string dataHeader = ReadFourCc();
        if (dataHeader != "data")
        {
            throw new InvalidDataException($"Chunk 'data' tidak ditemukan, ditemukan '{dataHeader}'");
        }

        ulong dataChunkSize = _reader.ReadUInt64();
        DataChunkDataOffset = _fileStream.Position;
        DataChunkDataLength = (long)dataChunkSize - 12; // 12 byte untuk header data chunk

        // Hitung total durasi
        if (SampleRate > 0)
        {
            double totalSeconds = (double)SampleCount / SampleRate;
            Duration = TimeSpan.FromSeconds(totalSeconds);
        }
        else
        {
            Duration = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Membaca blok data biner 1-bit DSD mentah dari posisi file saat ini.
    /// </summary>
    public int ReadRawDsd(byte[] buffer, int offset, int count)
    {
        long remaining = (DataChunkDataOffset + DataChunkDataLength) - _fileStream.Position;
        if (remaining <= 0) return 0;

        int bytesToRead = (int)Math.Min(count, remaining);
        return _fileStream.Read(buffer, offset, bytesToRead);
    }

    /// <summary>
    /// Mengatur posisi pembacaan DSD berdasarkan TimeSpan (Seeking).
    /// </summary>
    public void SeekTo(TimeSpan time)
    {
        if (Duration == TimeSpan.Zero) return;

        double ratio = Math.Clamp(time.TotalMilliseconds / Duration.TotalMilliseconds, 0.0, 1.0);
        long targetByte = (long)(ratio * DataChunkDataLength);

        // Sejajarkan (align) dengan ukuran blok kanal agar tidak merusak sinkronisasi stereo
        long totalBlock = BlockSizePerChannel * Channels;
        if (totalBlock > 0)
        {
            targetByte = (targetByte / totalBlock) * totalBlock;
        }
        _fileStream.Seek(DataChunkDataOffset + targetByte, SeekOrigin.Begin);
    }

    /// <summary>
    /// Mengembalikan posisi pembacaan audio DSD ke awal chunk data.
    /// </summary>
    public void ResetToBeginning()
    {
        _fileStream.Seek(DataChunkDataOffset, SeekOrigin.Begin);
    }

    private string ReadFourCc()
    {
        byte[] bytes = _reader.ReadBytes(4);
        return Encoding.ASCII.GetString(bytes);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _fileStream.Dispose();
        GC.SuppressFinalize(this);
    }
}
