using Silk.NET.OpenAL;
using Thread = System.Threading.Thread;

namespace EliteSharp.Sound;

/// <summary>
/// Plays the music (see <see cref="Music"/>) on its own OpenAL source, beside
/// the sound effects' voices. The music is streamed from memory through a
/// few short buffers that a thread of its own keeps filled, so it can loop
/// back to a track's loop start at exactly the right sample (OpenAL can only
/// loop the whole of a buffer by itself).
///
/// The game asks for a track with <see cref="Play"/>, and the thread starts it
/// from the beginning (or stops the music) on its next turn; asking for the
/// track that's already playing lets it carry on. A track that hasn't been
/// loaded yet starts as soon as it is.
/// </summary>
internal sealed unsafe class MusicPlayer : IDisposable
{
    /// <summary>The number of buffers in the source's queue.</summary>
    private const int BufferCount = 4;

    /// <summary>The number of sample frames in each buffer (about a fifth of a second at 44.1 kHz).</summary>
    private const int BufferFrames = 8192;

    /// <summary>How often the thread tops up the queue, in milliseconds.</summary>
    private const int UpdateInterval = 10;

    private readonly AL _al;
    private readonly uint _source;
    private readonly uint[] _buffers;
    private readonly Thread _thread;
    private readonly short[] _chunk = new short[BufferFrames * 2];

    private volatile MusicTrack?[] _tracks = [];
    private volatile int _requested = NoTrack;
    private volatile float _gain = 1;
    private volatile bool _stop;

    /// <summary>The track that's playing (on the music thread), or <see cref="NoTrack"/>.</summary>
    private int _playing = NoTrack;

    /// <summary>The next sample frame to queue from the playing track.</summary>
    private int _position;

    /// <summary>The track number that means no music.</summary>
    public const int NoTrack = -1;

    public MusicPlayer(AL al)
    {
        _al = al;
        _source = al.GenSource();
        _buffers = al.GenBuffers(BufferCount);
        _thread = new Thread(Run) { IsBackground = true, Name = "Music" };
        _thread.Start();
    }

    /// <summary>The volume of the music, as a gain from 0 to 1.</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = value;
    }

    /// <summary>Hand over the loaded tracks, by track number (null for any that are missing).</summary>
    public void SetTracks(MusicTrack?[] tracks) => _tracks = tracks;

    /// <summary>Ask for a track to play (or <see cref="NoTrack"/> for silence).</summary>
    public void Play(int track) => _requested = track;

    private void Run()
    {
        while (!_stop)
        {
            Update();
            Thread.Sleep(UpdateInterval);
        }
    }

    /// <summary>Start or stop the music as asked, and keep the playing track's buffers filled.</summary>
    private void Update()
    {
        int requested = _requested;
        if (requested != _playing)
        {
            if (_playing != NoTrack)
            {
                Stop();
            }

            if (Track(requested) is { } next)
            {
                _playing = requested;
                _position = 0;
                foreach (uint buffer in _buffers)
                {
                    Queue(buffer, next);
                }

                _al.SetSourceProperty(_source, SourceFloat.Gain, _gain);
                _al.SourcePlay(_source);
            }
        }

        if (_playing == NoTrack || Track(_playing) is not { } track)
        {
            return;
        }

        // Refill the buffers that have finished playing and put them back
        // at the end of the queue
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        for (; processed > 0; processed--)
        {
            uint buffer;
            _al.SourceUnqueueBuffers(_source, 1, &buffer);
            Queue(buffer, track);
        }

        _al.SetSourceProperty(_source, SourceFloat.Gain, _gain);

        // If the queue ran dry (the game stalled the thread), carry on
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if (state != (int)SourceState.Playing)
        {
            _al.SourcePlay(_source);
        }
    }

    /// <summary>A loaded track, or null if there's no such track or it isn't loaded.</summary>
    private MusicTrack? Track(int number)
    {
        var tracks = _tracks;
        return number >= 0 && number < tracks.Length ? tracks[number] : null;
    }

    /// <summary>Fill a buffer with the track's next frames, looping back to its loop start, and queue it.</summary>
    private void Queue(uint buffer, MusicTrack track)
    {
        var samples = track.Samples;
        int channels = samples.Channels;
        _position = CopyFrames(track, _position, _chunk, BufferFrames);

        var format = channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
        fixed (short* p = _chunk)
        {
            _al.BufferData(buffer, format, p, BufferFrames * channels * sizeof(short), samples.SampleRate);
        }

        _al.SourceQueueBuffers(_source, 1, &buffer);
    }

    /// <summary>
    /// Copy a number of sample frames of a track into a buffer, starting at
    /// a position and going back to the loop start whenever the loop end is
    /// reached, and return the position to carry on from.
    /// </summary>
    public static int CopyFrames(MusicTrack track, int position, short[] destination, int frameCount)
    {
        int channels = track.Samples.Channels;
        int filled = 0;
        while (filled < frameCount)
        {
            if (position >= track.LoopEnd)
            {
                position = track.LoopStart;
            }

            int frames = Math.Min(frameCount - filled, track.LoopEnd - position);
            Array.Copy(track.Samples.Samples, position * channels, destination, filled * channels, frames * channels);
            filled += frames;
            position += frames;
        }

        return position;
    }

    /// <summary>Stop the music and take its buffers off the source.</summary>
    private void Stop()
    {
        _al.SourceStop(_source);
        _al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
        _playing = NoTrack;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
        _al.SourceStop(_source);
        _al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
        _al.DeleteSource(_source);
        _al.DeleteBuffers(_buffers);
    }
}
