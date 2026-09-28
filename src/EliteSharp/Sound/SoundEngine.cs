using EliteSharp.Data;
using Silk.NET.OpenAL;

namespace EliteSharp.Sound;

/// <summary>
/// Emulates the BBC Micro's SN76489 sound chip, and the sound routines from
/// the original (NOISE, which populates the sound buffer, and SOINT, which is
/// called by the interrupt handler 50 times a second to send the buffer to the
/// sound chip). The chip's output is streamed through OpenAL.
/// </summary>
public sealed unsafe class SoundEngine : IDisposable
{
    /// <summary>The output sample rate.</summary>
    private const int SampleRate = 44100;

    /// <summary>The number of samples between each run of the sound interrupt (which runs at 50 Hz).</summary>
    private const int SamplesPerTick = SampleRate / 50;

    /// <summary>The number of OpenAL buffers queued at once.</summary>
    private const int BufferCount = 4;

    /// <summary>The number of samples in each OpenAL buffer.</summary>
    private const int BufferSamples = SamplesPerTick * 2;

    // The sound buffer from the original, with one entry for each of the
    // three voices

    /// <summary>SOFLG: the flags for each voice (bit 7 set for a new sound, bits 0-5 the sound number + 1).</summary>
    private readonly int[] _flags = new int[3];

    /// <summary>SOCNT: the number of ticks left in each voice's sound.</summary>
    private readonly int[] _count = new int[3];

    /// <summary>SOVOL: the volume of each voice.</summary>
    private readonly int[] _volume = new int[3];

    /// <summary>SOVCH: the volume change rate for each voice (the volume drops every SOVCH ticks).</summary>
    private readonly int[] _volumeChange = new int[3];

    /// <summary>SOPR: the priority of the sound on each voice.</summary>
    private readonly int[] _priority = new int[3];

    /// <summary>SOFRCH: the frequency change for each voice, added to the frequency each tick.</summary>
    private readonly int[] _frequencyChange = new int[3];

    /// <summary>SOFRQ: the frequency of each voice.</summary>
    private readonly int[] _frequency = new int[3];

    /// <summary>SOFH: the sound chip latch bytes for each voice's frequency.</summary>
    private static readonly int[] FrequencyLatch = [0b11000000, 0b10100000, 0b10000000];

    /// <summary>SOOFF: the sound chip bytes that silence each voice (plus the noise control byte).</summary>
    private static readonly int[] VolumeLatch = [0b11111111, 0b10111111, 0b10011111, 0b11011111, 0b11101111];

    /// <summary>Guards the sound buffer, which is written by the game thread and read by the audio thread.</summary>
    private readonly object _lock = new();

    /// <summary>Where to get the game's volume setting (VOL, 0-7).</summary>
    private Func<int> _volumeSource = () => 7;

    // The emulated SN76489 sound chip's registers and state

    /// <summary>The 10-bit tone period register for each tone channel.</summary>
    private readonly int[] _tonePeriod = [1024, 1024, 1024];

    /// <summary>The 4-bit attenuation register for each channel (three tones and the noise), where 15 is silent.</summary>
    private readonly int[] _attenuation = [15, 15, 15, 15];

    /// <summary>The noise control register (bit 2 for white noise, bits 0-1 for the noise rate).</summary>
    private int _noiseControl;

    /// <summary>The register selected by the last latch byte, for any following data byte.</summary>
    private int _latchedRegister;

    /// <summary>The countdown to the next output flip for each tone channel.</summary>
    private readonly double[] _toneCounter = new double[3];

    /// <summary>The current output level (high or low) of each tone channel.</summary>
    private readonly bool[] _toneOutput = new bool[3];

    /// <summary>The countdown to the next shift of the noise generator.</summary>
    private double _noiseCounter;

    /// <summary>The noise generator's 15-bit linear feedback shift register.</summary>
    private int _lfsr = 0x4000;

    /// <summary>The noise generator's current output level.</summary>
    private bool _noiseOutput;

    /// <summary>The output level for each attenuation value (2 dB steps).</summary>
    private static readonly float[] VolumeTable = BuildVolumeTable();

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private uint _source;
    private uint[] _buffers = [];
    private Thread? _thread;
    private volatile bool _running;
    private int _samplesUntilTick;

    private static float[] BuildVolumeTable()
    {
        var table = new float[16];
        for (int i = 0; i < 15; i++)
        {
            table[i] = (float)Math.Pow(10, -2.0 * i / 20);
        }

        table[15] = 0;
        return table;
    }

    /// <summary>Start the sound engine, returning null if no audio device is available.</summary>
    public static SoundEngine? TryCreate()
    {
        try
        {
            var engine = new SoundEngine();
            engine.Start();
            return engine;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Sound is disabled: {exception.Message}");
            return null;
        }
    }

    /// <summary>Set the function that returns the volume setting (VOL, 0-7).</summary>
    public void SetVolumeSource(Func<int> source) => _volumeSource = source;

    private void Start()
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
        _source = _al.GenSource();
        _buffers = _al.GenBuffers(BufferCount);

        // SOFLUSH: silence all the channels
        foreach (int value in VolumeLatch)
        {
            WriteChip(value);
        }

        var samples = new short[BufferSamples];
        foreach (uint buffer in _buffers)
        {
            Generate(samples);
            fixed (short* p = samples)
            {
                _al.BufferData(buffer, BufferFormat.Mono16, p, samples.Length * sizeof(short), SampleRate);
            }
        }

        _al.SourceQueueBuffers(_source, _buffers);
        _al.SourcePlay(_source);

        _running = true;
        _thread = new Thread(StreamLoop) { IsBackground = true, Name = "Sound" };
        _thread.Start();
    }

    private void StreamLoop()
    {
        var samples = new short[BufferSamples];
        var al = _al!;
        while (_running)
        {
            al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            while (processed-- > 0)
            {
                uint buffer = 0;
                al.SourceUnqueueBuffers(_source, 1, &buffer);
                Generate(samples);
                fixed (short* p = samples)
                {
                    al.BufferData(buffer, BufferFormat.Mono16, p, samples.Length * sizeof(short), SampleRate);
                }

                al.SourceQueueBuffers(_source, 1, &buffer);
            }

            al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing)
            {
                al.SourcePlay(_source);
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>Generate samples from the sound chip, calling SOINT 50 times a second.</summary>
    private void Generate(short[] samples)
    {
        lock (_lock)
        {
            const double chipRate = 4_000_000.0 / 16.0;
            double step = chipRate / SampleRate;
            for (int i = 0; i < samples.Length; i++)
            {
                if (_samplesUntilTick <= 0)
                {
                    ProcessSoundBuffer();
                    _samplesUntilTick = SamplesPerTick;
                }

                _samplesUntilTick--;

                float mix = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int period = _tonePeriod[channel] == 0 ? 1024 : _tonePeriod[channel];
                    _toneCounter[channel] -= step;
                    while (_toneCounter[channel] <= 0)
                    {
                        _toneCounter[channel] += period;
                        _toneOutput[channel] = !_toneOutput[channel];
                        if (channel == 2 && (_noiseControl & 3) == 3 && _toneOutput[channel])
                        {
                            ClockNoise();
                        }
                    }

                    // Very high frequencies are output as a constant level
                    float level = period <= 1 ? 1 : (_toneOutput[channel] ? 1 : -1);
                    mix += level * VolumeTable[_attenuation[channel]];
                }

                if ((_noiseControl & 3) != 3)
                {
                    int noisePeriod = 16 << (_noiseControl & 3);
                    _noiseCounter -= step;
                    while (_noiseCounter <= 0)
                    {
                        _noiseCounter += noisePeriod;
                        ClockNoise();
                    }
                }

                mix += (_noiseOutput ? 1 : -1) * VolumeTable[_attenuation[3]];
                samples[i] = (short)(mix * 0.22f * short.MaxValue);
            }
        }
    }

    private void ClockNoise()
    {
        bool white = (_noiseControl & 4) != 0;
        int feedback = white ? ((_lfsr ^ (_lfsr >> 1)) & 1) : (_lfsr & 1);
        _lfsr = (_lfsr >> 1) | (feedback << 14);
        _noiseOutput = (_lfsr & 1) != 0;
    }

    /// <summary>SOUS1: write a byte to the sound chip.</summary>
    private void WriteChip(int value)
    {
        if ((value & 0x80) != 0)
        {
            _latchedRegister = (value >> 4) & 7;
            int data = value & 15;
            int channel = _latchedRegister >> 1;
            if ((_latchedRegister & 1) != 0)
            {
                _attenuation[channel] = data;
            }
            else if (channel == 3)
            {
                _noiseControl = data;
                _lfsr = 0x4000;
            }
            else
            {
                _tonePeriod[channel] = (_tonePeriod[channel] & 0x3F0) | data;
            }
        }
        else
        {
            int channel = _latchedRegister >> 1;
            if ((_latchedRegister & 1) == 0 && channel < 3)
            {
                _tonePeriod[channel] = (_tonePeriod[channel] & 15) | ((value & 0x3F) << 4);
            }
        }
    }

    /// <summary>
    /// NOISE: make the given sound effect by populating the sound buffer (the
    /// effect number is in Y in the original).
    /// </summary>
    public void Noise(int y)
    {
        lock (_lock)
        {
            int bits = GameData.SoundBits[y];
            int x;
            if ((bits & 1) != 0)
            {
                x = 0;
            }
            else if (_priority[1] < _priority[2])
            {
                x = 1;
            }
            else
            {
                x = 2;
            }

            // SOUS4
            int priority = GameData.SoundPriority[y];
            if (priority < _priority[x])
            {
                return;
            }

            _priority[x] = priority;
            _volume[x] = (priority >> 1) & 7;
            _volumeChange[x] = GameData.SoundVolumeChange[y];
            _count[x] = bits;
            _frequencyChange[x] = (bits & 15) >> 1;
            _frequency[x] = GameData.SoundFrequency[y];
            _flags[x] = 0x80;
        }
    }

    /// <summary>SOINT: process the sound buffer and send the results to the sound chip.</summary>
    private void ProcessSoundBuffer()
    {
        int vol = Math.Clamp(_volumeSource(), 0, 7);
        for (int y = 2; y >= 0; y--)
        {
            // SOUL8
            int flag = _flags[y];
            if (flag == 0)
            {
                continue;
            }

            int delta = -1;
            if ((flag & 0x80) != 0)
            {
                // SOUL4: a new sound, so just set the frequency
                delta = 0;
            }
            else if (_frequencyChange[y] != 0)
            {
                delta = _frequencyChange[y];
            }

            if (delta >= 0)
            {
                int a = (_frequency[y] + delta) & 0xFF;
                _frequency[y] = a;
                WriteChip(((a << 2) & 15) | FrequencyLatch[y]);
                WriteChip(a >> 2);
            }

            // SOUL5
            int volume;
            if ((_flags[y] & 0x80) != 0)
            {
                // SOUL6
                _flags[y] >>= 1;
                volume = _volume[y] + vol;
            }
            else
            {
                _count[y] = (_count[y] - 1) & 0xFF;
                if (_count[y] == 0)
                {
                    // SOKILL
                    _flags[y] = 0;
                    _priority[y] = 0;
                    WriteChip(VolumeLatch[y]);
                    continue;
                }

                if ((_count[y] & _volumeChange[y]) != 0)
                {
                    continue;
                }

                _volume[y] = (_volume[y] - 1) & 0xFF;
                if (_volume[y] == 0)
                {
                    _flags[y] = 0;
                    _priority[y] = 0;
                    WriteChip(VolumeLatch[y]);
                    continue;
                }

                volume = _volume[y] + vol;
            }

            // SOU3
            WriteChip((volume & 0xFF) ^ VolumeLatch[y]);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        if (_al != null)
        {
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
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
    }
}
