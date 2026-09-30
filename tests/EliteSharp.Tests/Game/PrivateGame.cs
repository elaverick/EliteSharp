using System.Reflection;
using EliteSharp.Game;
using EliteSharp.Game.Ships;
using EliteSharp.Input;
using EliteSharp.Rendering;

namespace EliteSharp.Tests.Game;

/// <summary>
/// A real game (without a window, sound or missions' side effects) whose
/// private routines and variables the tests can reach, so the game's maths can
/// be tested routine by routine, as they are ported from the original.
/// </summary>
internal sealed class PrivateGame
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public PrivateGame()
    {
        Game = new EliteGame(new Hud(new FrameExchange()), new BbcKeyboard(), null, new GameOptions { Sound = false, Gamepad = false, MainLoopRate = 50 });
    }

    public EliteGame Game { get; }

    /// <summary>INWK, the ship being worked on.</summary>
    public Ship CurrentShip => Get<Ship>("_currentShip");

    /// <summary>The ship slots (K%).</summary>
    public Ship?[] Slots => Get<Ship?[]>("Slots");

    public object? Call(string method, params object[] args)
    {
        var info = typeof(EliteGame).GetMethod(method, Private, [.. args.Select(a => a.GetType())])
            ?? throw new MissingMethodException(nameof(EliteGame), method);
        try
        {
            return info.Invoke(Game, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    public T Get<T>(string member) => (T)(Field(member)?.GetValue(Game) ?? Property(member)!.GetValue(Game))!;

    public void Set(string member, object? value)
    {
        if (Field(member) is { } field)
        {
            field.SetValue(Game, value);
        }
        else
        {
            Property(member)!.SetValue(Game, value);
        }
    }

    /// <summary>Set up INWK as a ship of the given type, as ZINF does, ready for the ship routines.</summary>
    public Ship SetUpShip(int type)
    {
        Call("ResetWorkspace");
        Set("_shipType", type);
        Set("_blueprint", Call("BlueprintFor", type));
        return CurrentShip;
    }

    /// <summary>
    /// Run a routine that may kill us, returning false if it does (DEATH jumps
    /// back to the title screen).
    /// </summary>
    public bool Survives(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (e.GetType().Name == "GameJumpException")
        {
            return false;
        }
    }

    private static FieldInfo? Field(string name) => typeof(EliteGame).GetField(name, Private);

    private static PropertyInfo? Property(string name) => typeof(EliteGame).GetProperty(name, Private);
}
