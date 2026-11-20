using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AuraAudio.Metadata;
using AuraAudio.UI.ViewModels;
using Xunit;

namespace AuraAudio.Tests;

public class LyricsParserTests
{
    [Fact]
    public void ParseLrcContent_ParsesStandardAndVariantTimestampFormats()
    {
        const string lrc = """
            [ti:Sample Track]
            [ar:Sample Artist]
            [au:Hoshimachi Composer]
            [00:05]Exact second line
            [00:08.5]Tenths precision line
            [00:11.42]Hundredths precision line
            [00:14.125]Milliseconds precision line
            [00:17:60]Colon fraction line
            """;

        var result = LyricsParser.ParseLrcContent(lrc, LyricsSourceKind.SidecarLrc);

        Assert.True(result.IsSynced);
        Assert.Equal(LyricsSourceKind.SidecarLrc, result.Source);
        Assert.Equal("Hoshimachi Composer", result.Writers);
        Assert.Equal(5, result.Lines.Count);

        Assert.Equal(TimeSpan.FromSeconds(5), result.Lines[0].Timestamp);
        Assert.Equal("Exact second line", result.Lines[0].Text);

        Assert.Equal(TimeSpan.FromMilliseconds(8500), result.Lines[1].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(11420), result.Lines[2].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(14125), result.Lines[3].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(17600), result.Lines[4].Timestamp);
        Assert.All(result.Lines, l => Assert.False(l.IsInstrumentalGap));
    }

    [Fact]
    public void ParseLrcContent_ExpandsMultiTimestampLinesChronologically()
    {
        const string lrc = """
            [00:02.00]Verse one
            [00:06.00][00:14.00]Repeated chorus line
            [00:10.00]Verse two
            """;

        var result = LyricsParser.ParseLrcContent(lrc, LyricsSourceKind.SidecarLrc);

        Assert.True(result.IsSynced);
        Assert.Equal(4, result.Lines.Count);

        Assert.Equal("Verse one", result.Lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Lines[0].Timestamp);

        Assert.Equal("Repeated chorus line", result.Lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(6), result.Lines[1].Timestamp);

        Assert.Equal("Verse two", result.Lines[2].Text);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Lines[2].Timestamp);

        Assert.Equal("Repeated chorus line", result.Lines[3].Text);
        Assert.Equal(TimeSpan.FromSeconds(14), result.Lines[3].Timestamp);
    }

    [Fact]
    public void ParseLrcContent_PositiveOffsetShiftsEarlierAndClampsAtZero()
    {
        const string lrc = """
            [offset:+500]
            [00:00.20]Opening line clamped
            [00:03.50]Second line shifted
            """;

        var result = LyricsParser.ParseLrcContent(lrc);

        Assert.True(result.IsSynced);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(TimeSpan.Zero, result.Lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(3.0), result.Lines[1].Timestamp);
    }

    [Fact]
    public void ParseLrcContent_NegativeOffsetDelaysTimestamps()
    {
        const string lrc = """
            [offset:-750]
            [00:01.00]First line delayed
            [00:04.25]Second line delayed
            """;

        var result = LyricsParser.ParseLrcContent(lrc);

        Assert.True(result.IsSynced);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1750), result.Lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), result.Lines[1].Timestamp);
    }

    [Fact]
    public void ParseLrcContent_InsertsInstrumentalGapMarkerOnlyWhenGapAtLeastFiveSeconds()
    {
        const string lrc = """
            [00:02.00]Line A
            [00:06.90]Line B under threshold
            [00:15.00]Line C after long break
            """;

        var result = LyricsParser.ParseLrcContent(lrc);

        Assert.Equal(4, result.Lines.Count);
        Assert.Equal("Line A", result.Lines[0].Text);
        Assert.Equal("Line B under threshold", result.Lines[1].Text);

        Assert.True(result.Lines[2].IsInstrumentalGap);
        Assert.Equal(LyricsParser.InstrumentalBreakGlyph, result.Lines[2].Text);
        Assert.True(result.Lines[2].Timestamp > result.Lines[1].Timestamp);
        Assert.True(result.Lines[2].Timestamp < result.Lines[3].Timestamp);

        Assert.Equal("Line C after long break", result.Lines[3].Text);
    }

    [Fact]
    public void ParseLrcContent_RespectsExplicitBlankBreakTimestamp()
    {
        const string lrc = """
            [00:10.00]Verse ends
            [00:13.50]
            [00:22.00]Bridge begins
            """;

        var result = LyricsParser.ParseLrcContent(lrc);

        Assert.Equal(3, result.Lines.Count);
        Assert.True(result.Lines[1].IsInstrumentalGap);
        Assert.Equal(TimeSpan.FromSeconds(13.5), result.Lines[1].Timestamp);
    }

    [Fact]
    public void ParseText_UntimestampedLyricsFallsBackToPlainText()
    {
        const string raw = "First unsynced line\r\nSecond unsynced line\r\nThird line";

        var result = LyricsParser.ParseText(raw, LyricsSourceKind.EmbeddedLrc, "Test Writer");

        Assert.False(result.IsSynced);
        Assert.Equal(LyricsSourceKind.PlainText, result.Source);
        Assert.Empty(result.Lines);
        Assert.Equal("First unsynced line\nSecond unsynced line\nThird line", result.PlainText);
        Assert.Equal("Test Writer", result.Writers);
    }

    [Fact]
    public void LoadForFile_ResolvesUtf8BomSidecarLrcFirst()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_LrcTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string fakeAudioPath = Path.Combine(tempDir, "01 - Track.flac");
            string lrcPath = Path.Combine(tempDir, "01 - Track.lrc");
            File.WriteAllText(
                lrcPath,
                "[au:Yuki Kajiura]\n[00:01.00]Sidecar line one\n[00:04.00]Sidecar line two\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = LyricsParser.LoadForFile(fakeAudioPath);

            Assert.True(result.IsSynced);
            Assert.Equal(LyricsSourceKind.SidecarLrc, result.Source);
            Assert.Equal(2, result.Lines.Count);
            Assert.Equal("Sidecar line one", result.Lines[0].Text);
            Assert.Equal("Yuki Kajiura", result.Writers);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LyricsViewModel_SyncedTracksActiveLineTiersAndSeekCommand()
    {
        double? seekedSeconds = null;
        var vm = new LyricsViewModel(sec => seekedSeconds = sec);

        var parsed = LyricsParser.ParseLrcContent("""
            [au:Composer A]
            [00:02.00]Line zero
            [00:05.00]Line one
            [00:12.00]Line two after break
            """, LyricsSourceKind.SidecarLrc);

        vm.ApplyParsedResult(parsed);

        Assert.Equal(LyricsViewState.TimeBasedLyricsLoaded, vm.State);
        Assert.True(vm.IsSynced);
        Assert.Equal("Synced · LRC", vm.SourceLabel);
        Assert.Equal("Written by: Composer A", vm.WritersText);
        Assert.Equal(4, vm.Lines.Count); // Line 0, Line 1, • • •, Line 2

        // Advance to 5.5s -> index 1 ("Line one") is Current
        vm.UpdatePlaybackPosition(TimeSpan.FromSeconds(5.5));
        Assert.Equal(1, vm.ActiveLineIndex);
        Assert.Equal(LyricsLineTier.Past, vm.Lines[0].Tier);
        Assert.Equal(LyricsLineTier.Current, vm.Lines[1].Tier);
        Assert.Equal(LyricsLineTier.Future1, vm.Lines[2].Tier);
        Assert.Equal(LyricsLineTier.FutureN, vm.Lines[3].Tier);

        // Instrumental gap cannot be seeked
        Assert.False(vm.SeekToLineCommand.CanExecute(vm.Lines[2]));

        // Normal synced line CAN be seeked and updates active index immediately
        Assert.True(vm.SeekToLineCommand.CanExecute(vm.Lines[3]));
        vm.SeekToLineCommand.Execute(vm.Lines[3]);
        Assert.Equal(12.0, seekedSeconds);
        Assert.Equal(3, vm.ActiveLineIndex);
        Assert.Equal(LyricsLineTier.Current, vm.Lines[3].Tier);
    }

    [Fact]
    public void LyricsViewModel_PlainTextFallbackDisablesSeekingAndHighlighting()
    {
        double? seekedSeconds = null;
        var vm = new LyricsViewModel(sec => seekedSeconds = sec);

        var parsed = LyricsParser.ParseText("Plain prose line 1\nPlain prose line 2");
        vm.ApplyParsedResult(parsed);

        Assert.Equal(LyricsViewState.StaticLyricsLoaded, vm.State);
        Assert.False(vm.IsSynced);
        Assert.Equal("Unsynced Lyrics", vm.SourceLabel);
        Assert.Equal("Plain prose line 1\nPlain prose line 2", vm.PlainLyricsText);
        Assert.Empty(vm.Lines);
        Assert.Equal(-1, vm.ActiveLineIndex);
        Assert.False(vm.SeekToLineCommand.CanExecute(null));

        vm.UpdatePlaybackPosition(TimeSpan.FromSeconds(30));
        Assert.Equal(-1, vm.ActiveLineIndex);
        Assert.Null(seekedSeconds);
    }

    [Fact]
    public async Task LyricsViewModel_NullTrackResetsToNoSongState()
    {
        var vm = new LyricsViewModel();
        vm.ApplyParsedResult(LyricsParser.ParseText("Temporary lyrics"));
        Assert.Equal(LyricsViewState.StaticLyricsLoaded, vm.State);

        await vm.LoadForTrackAsync(null);
        Assert.Equal(LyricsViewState.NoSong, vm.State);
        Assert.False(vm.IsSynced);
        Assert.Equal(string.Empty, vm.PlainLyricsText);
    }
}
