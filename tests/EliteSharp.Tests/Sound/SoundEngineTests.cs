using System.Reflection;
using EliteSharp.Sound;

namespace EliteSharp.Tests.Sound;

/// <summary>
/// The game runs without sound if there is no audio device, but a mistake in
/// the game isn't hidden by turning the sound off, and the sound engine can be
/// disposed of more than once.
/// </summary>
public sealed class SoundEngineTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static bool IsSoundUnavailable(Exception exception) =>
        (bool)typeof(SoundEngine).GetMethod("IsSoundUnavailable", Private)!.Invoke(null, [exception])!;

    private static Exception SoundUnavailable(string message) =>
        (Exception)Activator.CreateInstance(typeof(SoundEngine).Assembly.GetType("EliteSharp.Sound.SoundUnavailableException", true)!, message)!;

    /// <summary>An engine that hasn't started (as TryCreate makes before it starts it).</summary>
    private static SoundEngine Unstarted() => (SoundEngine)Activator.CreateInstance(typeof(SoundEngine), nonPublic: true)!;

    public static TheoryData<Exception> NoSound =>
    [
        SoundUnavailable("Could not open an audio device"),
        new FileNotFoundException("Could not find or load the native library: openal"),
        new DllNotFoundException(),
        new BadImageFormatException(),
        new EntryPointNotFoundException(),
        new PlatformNotSupportedException(),
    ];

    public static TheoryData<Exception> Mistakes =>
    [
        new NullReferenceException(),
        new IndexOutOfRangeException(),
        new ArgumentException(),
        new InvalidOperationException(),
        new InvalidCastException(),
    ];

    [Theory]
    [MemberData(nameof(NoSound))]
    public void AFailureToStartOpenAlTurnsTheSoundOff(Exception exception)
    {
        Assert.True(IsSoundUnavailable(exception));
    }

    [Theory]
    [MemberData(nameof(Mistakes))]
    public void AMistakeInTheGameDoesntTurnTheSoundOff(Exception exception)
    {
        Assert.False(IsSoundUnavailable(exception));
    }

    [Fact]
    public void AnEngineThatDidntStartCanBeDisposedOfTwice()
    {
        var engine = Unstarted();

        engine.Dispose();
        engine.Dispose();
        engine.Noise(0);
    }

    /// <summary>With an audio device (and without one, TryCreate turns the sound off).</summary>
    [Fact]
    public void AnEngineCanBeDisposedOfTwice()
    {
        var engine = SoundEngine.TryCreate(SoundEffects.Load());
        if (engine == null)
        {
            return;
        }

        engine.Dispose();
        engine.Dispose();

        // Nothing plays once it's disposed of
        engine.Noise(0);
    }
}
