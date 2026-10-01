using Silk.NET.OpenAL;

namespace EliteSharp.Sound;

/// <summary>
/// Plays the sound effects (see <see cref="SoundEffects"/>) through OpenAL,
/// on the original's three voices: voice 0 for the effects that use the
/// sound chip's noise channel, and voices 1 and 2 for the others. As in the
/// original's NOISE routine, an effect only plays if its priority is at least
/// that of the sound already playing on its voice, and if it does, it cuts
/// that sound off.
/// </summary>
public sealed unsafe class SoundEngine : IDisposable
{
    /// <summary>The number of voices (one OpenAL source each).</summary>
    private const int VoiceCount = 3;

    /// <summary>The volume setting that the sound effects are recorded at (the loudest).</summary>
    private const int LoudestVolume = 7;

    /// <summary>SOPR: the priority of the sound on each voice (while it is playing).</summary>
    private readonly int[] _priority = new int[VoiceCount];

    /// <summary>Where to get the game's volume setting (VOL, 0-7).</summary>
    private Func<int> _volumeSource = () => LoudestVolume;

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private uint[] _sources = [];
    private uint[] _buffers = [];

    /// <summary>Start the sound engine with the sound effects, returning null if no audio device is available.</summary>
    public static SoundEngine? TryCreate(SoundSamples[] effects)
    {
        var engine = new SoundEngine();
        try
        {
            engine.Start(effects);
            return engine;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            engine.Dispose();
            Console.Error.WriteLine($"Sound is disabled: {exception.Message}");
            return null;
        }
    }

    /// <summary>Set the function that returns the volume setting (VOL, 0-7).</summary>
    public void SetVolumeSource(Func<int> source) => _volumeSource = source;

    private void Start(SoundSamples[] effects)
    {
        _alc = ALContext.GetApi(true);
        _al = AL.GetApi(true);
        _device = _alc.OpenDevice("");
        if (_device == null)
        {
            throw new InvalidOperationException("Could not open an audio device");
        }

        _context = _alc.CreateContext(_device, null);
        _alc.MakeContextCurrent(_context);
        _sources = _al.GenSources(VoiceCount);
        _buffers = _al.GenBuffers(effects.Length);
        for (int i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            var format = effect.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
            fixed (short* p = effect.Samples)
            {
                _al.BufferData(_buffers[i], format, p, effect.Samples.Length * sizeof(short), effect.SampleRate);
            }
        }
    }

    /// <summary>
    /// NOISE: make the given sound effect (the effect number is in Y in the
    /// original), on the noise voice if it uses it, or otherwise on whichever
    /// tone voice has the lower priority (voice 2 if they are the same).
    /// </summary>
    public void Noise(int y)
    {
        if (_al == null)
        {
            return;
        }

        var effect = SoundEffects.All[y];
        int voice = effect.NoiseVoice ? 0 : Priority(1) < Priority(2) ? 1 : 2;

        // SOUS4
        if (effect.Priority < Priority(voice))
        {
            return;
        }

        _priority[voice] = effect.Priority;
        uint source = _sources[voice];
        _al.SourceStop(source);
        _al.SetSourceProperty(source, SourceInteger.Buffer, _buffers[y]);
        _al.SetSourceProperty(source, SourceFloat.Gain, Gain());
        _al.SourcePlay(source);
    }

    /// <summary>The priority of the sound on a voice, which is 0 once it has finished (SOKILL).</summary>
    private int Priority(int voice)
    {
        _al!.GetSourceProperty(_sources[voice], GetSourceInteger.SourceState, out int state);
        return state == (int)SourceState.Playing ? _priority[voice] : 0;
    }

    /// <summary>
    /// The gain for the volume setting. The original adds the setting to each
    /// sound's volume on the sound chip, where each step is 2 dB, and the
    /// effects are recorded at the loudest setting.
    /// </summary>
    private float Gain()
    {
        int volume = Math.Clamp(_volumeSource(), 0, LoudestVolume);
        return MathF.Pow(10, -2f * (LoudestVolume - volume) / 20);
    }

    public void Dispose()
    {
        if (_al != null)
        {
            foreach (uint source in _sources)
            {
                _al.SourceStop(source);
            }

            _al.DeleteSources(_sources);
            _al.DeleteBuffers(_buffers);
        }

        if (_alc != null)
        {
            _alc.MakeContextCurrent(null);
            if (_context != null)
            {
                _alc.DestroyContext(_context);
            }

            if (_device != null)
            {
                _alc.CloseDevice(_device);
            }
        }

        _al = null;
    }
}
