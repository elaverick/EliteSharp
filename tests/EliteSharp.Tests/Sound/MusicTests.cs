using System.Reflection;
using EliteSharp.Sound;

namespace EliteSharp.Tests.Sound;

/// <summary>
/// The music loads with the loop points from its files' tags, and plays its
/// introduction once and then its loop over and over, without a gap.
/// </summary>
public sealed class MusicTests
{
    /// <summary>MusicPlayer.CopyFrames (the player is internal, as it needs OpenAL).</summary>
    private static int CopyFrames(MusicTrack track, int position, short[] destination, int frameCount) =>
        (int)typeof(SoundEngine).Assembly.GetType("EliteSharp.Sound.MusicPlayer", true)!
            .GetMethod("CopyFrames", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [track, position, destination, frameCount])!;

    /// <summary>A track whose samples are their own frame numbers (in each channel).</summary>
    private static MusicTrack Numbered(int frames, int channels, int loopStart, int loopEnd) =>
        new(new SoundSamples([.. Enumerable.Range(0, frames).SelectMany(f => Enumerable.Repeat((short)f, channels))], channels, 44100), loopStart, loopEnd);

    [Fact]
    public void TheTitleThemeHasAnIntroductionThenLoopsToTheEnd()
    {
        var track = Music.LoadTrack(GameAssets.File("Music", "title.ogg"));

        // LOOPSTART=70385, LOOPLENGTH=3853561
        Assert.Equal(70385, track.LoopStart);
        Assert.Equal(70385 + 3853561, track.LoopEnd);
        Assert.True(track.LoopEnd <= track.Frames);
    }

    [Fact]
    public void TheDockingMusicLoopsAsAWhole()
    {
        var track = Music.LoadTrack(GameAssets.File("Music", "docking.ogg"));

        // LOOPSTART=0, LOOPLENGTH=5569188
        Assert.Equal(0, track.LoopStart);
        Assert.Equal(5569188, track.LoopEnd);
        Assert.True(track.LoopEnd <= track.Frames);
    }

    [Fact]
    public void EveryPieceOfMusicLoads()
    {
        Assert.All(Music.Load(), Assert.NotNull);
    }

    [Fact]
    public void MissingMusicIsSilenceRatherThanAnError()
    {
        Assert.Null(Music.TryLoad(Path.Combine(Path.GetTempPath(), "no-such-music.ogg")));
    }

    [Fact]
    public void TheIntroductionPlaysOnceAndTheLoopRepeats()
    {
        // Frames 0-1 are the introduction and 2-4 the loop
        var track = Numbered(frames: 6, channels: 1, loopStart: 2, loopEnd: 5);
        var buffer = new short[10];

        int position = CopyFrames(track, 0, buffer, 10);

        Assert.Equal([0, 1, 2, 3, 4, 2, 3, 4, 2, 3], buffer.Select(s => (int)s));
        Assert.Equal(4, position);
    }

    [Fact]
    public void TheLoopCarriesOnFromOneBufferToTheNext()
    {
        var track = Numbered(frames: 5, channels: 1, loopStart: 2, loopEnd: 5);
        var first = new short[4];
        var second = new short[4];

        int position = CopyFrames(track, 0, first, 4);
        CopyFrames(track, position, second, 4);

        Assert.Equal([0, 1, 2, 3, 4, 2, 3, 4], first.Concat(second).Select(s => (int)s));
    }

    [Fact]
    public void StereoFramesStayTogether()
    {
        var track = Numbered(frames: 3, channels: 2, loopStart: 1, loopEnd: 3);
        var buffer = new short[8];

        CopyFrames(track, 0, buffer, 4);

        Assert.Equal([0, 0, 1, 1, 2, 2, 1, 1], buffer.Select(s => (int)s));
    }
}
