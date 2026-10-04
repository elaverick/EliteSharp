using System.Globalization;
using NVorbis;

namespace EliteSharp.Sound;

/// <summary>
/// A piece of music: its samples, and the part of it that loops, in sample
/// frames (one sample per channel). It plays from the start to
/// <see cref="LoopEnd"/>, and then from <see cref="LoopStart"/> to
/// <see cref="LoopEnd"/> over and over, so an introduction can play once.
/// </summary>
public sealed record MusicTrack(SoundSamples Samples, int LoopStart, int LoopEnd)
{
    /// <summary>The number of sample frames in the track.</summary>
    public int Frames => Samples.Samples.Length / Samples.Channels;
}

/// <summary>
/// The music, which isn't in the BBC version: the Commodore 64 version's
/// title theme, and The Blue Danube while the docking computer flies us in.
/// Each is an OGG file in Assets/Music, which a mod can replace or leave out
/// (the game is silent where a piece is missing).
///
/// The loop points come from the files' LOOPSTART and LOOPLENGTH (or LOOPEND)
/// tags, in sample frames, as many games and players use them; without them,
/// the whole piece loops.
/// </summary>
public static class Music
{
    /// <summary>The title theme, played on the title screens.</summary>
    public const int Title = 0;

    /// <summary>The Blue Danube, played while the docking computer is on.</summary>
    public const int Docking = 1;

    /// <summary>No music.</summary>
    public const int None = MusicPlayer.NoTrack;

    /// <summary>The names of the music files (without ".ogg"), by track number.</summary>
    public static readonly string[] Names = ["title", "docking"];

    /// <summary>
    /// Load every piece of music, by track number, with null for any that's
    /// missing or can't be read (which is reported, as the game can carry on
    /// without it).
    /// </summary>
    public static MusicTrack?[] Load() => [.. Names.Select(name => TryLoad(GameAssets.File("Music", name + ".ogg")))];

    /// <summary>Load a piece of music, or return null (and report why) if it is missing or can't be read.</summary>
    public static MusicTrack? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return LoadTrack(path);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine(e.Message);
            return null;
        }
    }

    /// <summary>Load a piece of music and its loop points from an OGG file.</summary>
    /// <exception cref="InvalidDataException">The file isn't valid music (the message says why).</exception>
    public static MusicTrack LoadTrack(string path)
    {
        string file = Path.GetFileName(path);
        try
        {
            using var reader = new VorbisReader(path);
            var samples = SoundEffects.ReadSamples(reader, file);
            int frames = samples.Samples.Length / samples.Channels;

            // Without loop tags (or with ones that don't make sense), loop the lot
            int loopStart = Tag(reader, "LOOPSTART") ?? 0;
            int loopEnd = Tag(reader, "LOOPLENGTH") is { } length ? loopStart + length
                : Tag(reader, "LOOPEND") ?? frames;

            // The loop can run a little past the samples the file decodes to
            // (the encoder rounds the length), so it stops at the end
            loopEnd = Math.Min(loopEnd, frames);
            if (loopStart < 0 || loopStart >= loopEnd)
            {
                Console.Error.WriteLine($"{file}: ignoring the loop tags, which don't fit the music (it loops from the start instead)");
                (loopStart, loopEnd) = (0, frames);
            }

            return new MusicTrack(samples, loopStart, loopEnd);
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException($"{file}: the music can't be read ({e.Message})", e);
        }
    }

    /// <summary>A whole-number tag (the name in any case), or null if there isn't one.</summary>
    private static int? Tag(VorbisReader reader, string name)
    {
        foreach (var (key, values) in reader.Tags.All)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase) && values.Count > 0
                && int.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return null;
    }
}
