using System.Diagnostics;
using EliteSharp.Input;
using Silk.NET.Input;

namespace EliteSharp;

/// <summary>
/// Runs a test script of timed key presses and screenshots, for testing the
/// game without a player. Each line of the script is one of the following,
/// with times in milliseconds from the start:
///
///   &lt;time&gt; key &lt;Key&gt; [hold]   press a key (a Silk.NET key name), optionally holding it
///   &lt;time&gt; shot &lt;file.png&gt;    save a screenshot
///   &lt;time&gt; cmd &lt;command&gt;    run a test command (see EliteGame.DebugCommand)
///   &lt;time&gt; pad &lt;Button&gt; [hold]  press a controller button (a PadButton name)
///   &lt;time&gt; axis &lt;name&gt; &lt;value&gt;  set a controller axis (lx, ly, rx, ry, lt, rt)
///   &lt;time&gt; quit               close the game
/// </summary>
public sealed class ScriptRunner(string path, BbcKeyboard keyboard, Gamepad? gamepad, Action<string> capture, Action quit, Action<string> command)
{
    public void Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Script" };
        thread.Start();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        var releases = new List<(long Time, Key Key)>();
        var padReleases = new List<(long Time, PadButton Button)>();
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long time = long.Parse(parts[0]);
            WaitUntil(clock, time, releases, padReleases);

            switch (parts[1])
            {
                case "key":
                {
                    var key = Enum.Parse<Key>(parts[2], ignoreCase: true);
                    long hold = parts.Length > 3 ? long.Parse(parts[3]) : 80;
                    keyboard.OnKeyDown(key);
                    releases.Add((clock.ElapsedMilliseconds + hold, key));
                    break;
                }

                case "pad":
                {
                    var button = Enum.Parse<PadButton>(parts[2], ignoreCase: true);
                    long hold = parts.Length > 3 ? long.Parse(parts[3]) : 80;
                    gamepad?.InjectButton(button, true);
                    padReleases.Add((clock.ElapsedMilliseconds + hold, button));
                    break;
                }

                case "axis":
                    gamepad?.InjectAxis(parts[2], float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
                    break;

                case "shot":
                    capture(parts[2]);
                    break;

                case "cmd":
                    command(string.Join(' ', parts.Skip(2)));
                    break;

                case "quit":
                    quit();
                    return;
            }
        }
    }

    private void WaitUntil(Stopwatch clock, long time, List<(long Time, Key Key)> releases, List<(long Time, PadButton Button)> padReleases)
    {
        while (clock.ElapsedMilliseconds < time)
        {
            ReleaseKeys(clock, releases, padReleases);
            Thread.Sleep(5);
        }

        ReleaseKeys(clock, releases, padReleases);
    }

    private void ReleaseKeys(Stopwatch clock, List<(long Time, Key Key)> releases, List<(long Time, PadButton Button)> padReleases)
    {
        for (int i = padReleases.Count - 1; i >= 0; i--)
        {
            if (padReleases[i].Time <= clock.ElapsedMilliseconds)
            {
                gamepad?.InjectButton(padReleases[i].Button, false);
                padReleases.RemoveAt(i);
            }
        }

        for (int i = releases.Count - 1; i >= 0; i--)
        {
            if (releases[i].Time <= clock.ElapsedMilliseconds)
            {
                keyboard.OnKeyUp(releases[i].Key);
                releases.RemoveAt(i);
            }
        }
    }
}
