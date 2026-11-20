using System;
using System.Globalization;
using System.IO;

namespace AuraAudio.Core.Models;

/// <summary>
/// Metadata kredit lagu, produksi, dan spesifikasi file mendalam untuk ditampilkan
/// pada panggung Now Playing Music Detail Stage (gaya Apple Music InfoPanelControl).
/// Diekstrak secara on-demand dan disimpan di L1 cache memori.
/// </summary>
public sealed record TrackExtendedDetails
{
    public string Title { get; init; } = "—";
    public string Performers { get; init; } = "—";
    public string AlbumArtist { get; init; } = "—";
    public string Album { get; init; } = "—";
    public string Composer { get; init; } = "—";
    public string Conductor { get; init; } = "—";
    public string Genre { get; init; } = "—";
    public string YearText { get; init; } = "—";
    public string TrackDiscText { get; init; } = "—";
    public string Copyright { get; init; } = "—";
    public string CodecFullDescription { get; init; } = "—";
    public string SampleRateAndBitDepth { get; init; } = "—";
    public string BitrateText { get; init; } = "—";
    public string FileSizeText { get; init; } = "—";
    public string FileName { get; init; } = "—";
    public string DirectoryPath { get; init; } = "—";
    public string? Lyrics { get; init; }

    public bool HasLyrics => !string.IsNullOrWhiteSpace(Lyrics);
    public bool HasComposer => !string.IsNullOrWhiteSpace(Composer) && Composer != "—";
    public bool HasCopyright => !string.IsNullOrWhiteSpace(Copyright) && Copyright != "—";

    public static TrackExtendedDetails Empty { get; } = new();

    public static TrackExtendedDetails FromTrackFallback(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        double mb = track.FileSizeBytes / (1024.0 * 1024.0);
        string sizeStr = mb >= 0.01
            ? $"{mb.ToString("0.00", CultureInfo.InvariantCulture)} MB"
            : $"{track.FileSizeBytes:N0} B";

        uint kbps = track.StreamSpec.BitrateKbps;
        if (kbps == 0 && track.Duration.TotalSeconds > 0.5 && track.FileSizeBytes > 0)
        {
            kbps = (uint)Math.Round((track.FileSizeBytes * 8.0) / (track.Duration.TotalSeconds * 1000.0));
        }

        string bitrateStr = kbps > 0 ? $"{kbps:N0} kbps" : "Variable";
        double khz = Math.Round(track.StreamSpec.SampleRateHz / 1000.0, 1);
        string chText = track.StreamSpec.Channels switch
        {
            1 => "1 Ch (Mono)",
            2 => "2 Ch (Stereo)",
            6 => "5.1 Ch (Surround)",
            8 => "7.1 Ch (Surround)",
            _ => $"{track.StreamSpec.Channels} Ch"
        };

        string sampleSpec = track.StreamSpec.IsDsd
            ? $"{track.StreamSpec.FormattedBadge} • {chText}"
            : $"{(int)track.StreamSpec.BitDepth}-bit / {khz.ToString("0.#", CultureInfo.InvariantCulture)} kHz • {chText}";

        return new TrackExtendedDetails
        {
            Title = track.Title,
            Performers = track.Artist,
            AlbumArtist = string.IsNullOrWhiteSpace(track.AlbumArtist) ? track.Artist : track.AlbumArtist,
            Album = track.Album,
            Composer = "—",
            Conductor = "—",
            Genre = "—",
            YearText = track.Year.HasValue && track.Year.Value > 0 ? track.Year.Value.ToString(CultureInfo.InvariantCulture) : "—",
            TrackDiscText = $"Track {Math.Max(1, track.TrackNumber):D2} • Disc {Math.Max(1, track.DiscNumber)}",
            Copyright = "—",
            CodecFullDescription = FormatCodecFullDescription(track.StreamSpec.Codec),
            SampleRateAndBitDepth = sampleSpec,
            BitrateText = bitrateStr,
            FileSizeText = sizeStr,
            FileName = Path.GetFileName(track.FilePath),
            DirectoryPath = Path.GetDirectoryName(track.FilePath) ?? "—"
        };
    }

    public static string FormatCodecFullDescription(AudioCodec codec) => codec switch
    {
        AudioCodec.FLAC => "FLAC • Free Lossless Audio Codec",
        AudioCodec.ALAC => "ALAC • Apple Lossless Audio Codec",
        AudioCodec.WAV => "WAV • Uncompressed Linear PCM",
        AudioCodec.AIFF => "AIFF • Audio Interchange File Format",
        AudioCodec.DSD_DSF => "DSF • Direct Stream Digital (1-Bit)",
        AudioCodec.DSD_DFF => "DFF • Direct Stream Digital Interchange",
        AudioCodec.MP3 => "MP3 • MPEG-1 Audio Layer III",
        AudioCodec.AAC => "AAC • Advanced Audio Coding",
        AudioCodec.OGG_VORBIS => "Vorbis • Ogg Compressed Audio",
        AudioCodec.OPUS => "Opus • Low-Latency Interactive Audio",
        _ => codec.ToString()
    };
}
