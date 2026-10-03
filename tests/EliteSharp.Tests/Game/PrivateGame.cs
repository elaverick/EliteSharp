using System.Reflection;
using EliteSharp.Data;
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

    /// <summary>
    /// Create the game, optionally paced by the given clock (such as a
    /// <see cref="VirtualClock"/>), reading the given keyboard and handing its
    /// frames to the given exchange.
    /// </summary>
    public PrivateGame(
        GameStrings? strings = null,
        IGameClock? clock = null,
        BbcKeyboard? keyboard = null,
        FrameExchange? exchange = null,
        int mainLoopRate = 50)
    {
        var options = new GameOptions { Sound = false, Gamepad = false, MainLoopRate = mainLoopRate };
        Game = new EliteGame(new Hud(exchange ?? new FrameExchange()), keyboard ?? new BbcKeyboard(), null, options, strings: strings, clock: clock);
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

    /// <summary>The text on the screen, one string for each text row (1-24), with the trailing spaces removed.</summary>
    public string[] ScreenText()
    {
        var hud = Get<Hud>("_hud");
        var text = (System.Collections.IDictionary)typeof(Hud).GetField("_text", Private)!.GetValue(hud)!;
        var rows = Enumerable.Range(0, 25).Select(_ => new char[33]).ToArray();
        foreach (var row in rows)
        {
            Array.Fill(row, ' ');
        }

        foreach (System.Collections.DictionaryEntry entry in text)
        {
            var (column, row) = ((int, int))entry.Key;
            if (row is >= 1 and <= 24 && column is >= 0 and <= 32)
            {
                rows[row][column] = (((char, Ink))entry.Value!).Item1;
            }
        }

        return rows.Skip(1).Select(row => new string(row, 1, 32).TrimEnd()).ToArray();
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
