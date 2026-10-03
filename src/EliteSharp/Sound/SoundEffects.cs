using NVorbis;

namespace EliteSharp.Sound;

/// <summary>
/// A sound effect: its file in Assets/Sounds (its name plus ".ogg"), its
/// priority, and whether it uses the noise voice (voice 0) rather than one of
/// the two tone voices.
/// </summary>
public sealed record SoundEffect(string Name, int Priority, bool NoiseVoice);

/// <summary>A sound effect's samples, as 16-bit samples (interleaved, if there is more than one channel).</summary>
public sealed record SoundSamples(short[] Samples, int Channels, int SampleRate);

/// <summary>
/// The game's sound effects, by the effect number the game passes to
/// <see cref="SoundEngine.Noise"/>. Each is an OGG file in Assets/Sounds,
/// rendered from the original's sound data by tools/render_sounds.py, so the
/// effects sound as they did on the BBC Master's sound chip (at the loudest
/// volume setting), and can be replaced by any OGG file, mono or stereo.
///
/// The priorities and voices are the original's (SFXPR, and bit 0 of
/// SFXBT): a sound plays on its voice only if its priority is at least that
/// of the sound already playing there.
/// </summary>
public static class SoundEffects
{
    public static readonly SoundEffect[] All =
    [
        new("boop", 75, NoiseVoice: false),          // soboop: a long, low beep
        new("beep", 91, NoiseVoice: false),          // sobeep: a short, high beep
        new("click", 63, NoiseVoice: true),          // soclick: a click
        new("laser", 235, NoiseVoice: false),        // solaser: our laser firing (first part)
        new("explosion", 255, NoiseVoice: true),     // soexpl: an explosion
        new("laser-2", 9, NoiseVoice: true),         // solas2: a laser firing, ours or an enemy's (second part)
        new("hit", 255, NoiseVoice: true),           // sohit: a laser strike on another ship, or the energy bomb
        new("ecm", 139, NoiseVoice: false),          // soecm: the E.C.M.
        new("launch", 207, NoiseVoice: true),        // solaun: launching, docking or a missile launch
        new("hit-us", 231, NoiseVoice: false),       // us being hit by lasers (first part)
        new("hyperspace", 255, NoiseVoice: false),   // sohyp: hyperspace (first part)
        new("hyperspace-2", 239, NoiseVoice: false), // sohyp2: hyperspace (second part)
    ];

    /// <summary>The game's own folder containing the sound effects (a mod can replace any of them).</summary>
    public static string DefaultFolder => Path.Combine(GameAssets.BaseFolder, "Sounds");

    /// <summary>Load all the sound effects' samples, in the order of <see cref="All"/>, from a folder (by default, each from the mod's or the game's Assets/Sounds).</summary>
    /// <exception cref="InvalidDataException">A sound effect isn't valid (the message says which and why).</exception>
    public static SoundSamples[] Load(string? folder = null) =>
        All.Select(effect => LoadSamples(folder != null
            ? Path.Combine(folder, effect.Name + ".ogg")
            : GameAssets.File("Sounds", effect.Name + ".ogg"))).ToArray();

    /// <summary>Load a sound effect's samples from an OGG file.</summary>
    /// <exception cref="InvalidDataException">The file isn't a valid sound effect (the message says why).</exception>
    public static SoundSamples LoadSamples(string path)
    {
        string file = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The sound effect '{path}' is missing", path);
        }

        try
        {
            using var reader = new VorbisReader(path);
            if (reader.Channels is not (1 or 2))
            {
                throw new InvalidDataException($"{file}: the sound must be mono or stereo, not {reader.Channels} channels");
            }

            var samples = new List<short>();
            var buffer = new float[4096 * reader.Channels];
            int read;
            while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    samples.Add((short)Math.Clamp(MathF.Round(buffer[i] * short.MaxValue), short.MinValue, short.MaxValue));
                }
            }

            return new SoundSamples(samples.ToArray(), reader.Channels, reader.SampleRate);
        }
        catch (Exception e) when (e is not (IOException or InvalidDataException))
        {
            throw new InvalidDataException($"{file}: the sound can't be read ({e.Message})", e);
        }
    }
}
