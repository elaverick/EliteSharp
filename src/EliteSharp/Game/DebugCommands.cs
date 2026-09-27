using System.Collections.Concurrent;
using EliteSharp.Game.Ships;

namespace EliteSharp.Game;

/// <summary>
/// Commands for testing the game from a script (see ScriptRunner). These are
/// not part of the original game and are only available from test scripts.
/// </summary>
public sealed partial class EliteGame
{
    private readonly ConcurrentQueue<string> _debugCommands = new();

    private StreamWriter? _trace;

    private void Trace(string text) => _trace?.WriteLine(text);

    /// <summary>Queue a test command, which is run at the start of the next main loop iteration.</summary>
    public void DebugCommand(string command) => _debugCommands.Enqueue(command);

    private void RunDebugCommands()
    {
        while (_debugCommands.TryDequeue(out string? command))
        {
            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "spawn":
                {
                    // spawn <type> [z_hi] [x] [y] [ai] [newb]: add a ship in front of us
                    var saved = INWK;
                    ZINF();
                    int type = int.Parse(parts[1]);
                    INWK.Z = (parts.Length > 2 ? int.Parse(parts[2]) : 8) << 8;
                    INWK.X = parts.Length > 3 ? int.Parse(parts[3]) : 0;
                    INWK.Y = parts.Length > 4 ? int.Parse(parts[4]) : 0;
                    INWK.Ai = parts.Length > 5 ? Convert.ToInt32(parts[5], 16) : 0;
                    INWK.Newb = parts.Length > 6 ? Convert.ToInt32(parts[6], 16) : 0;
                    INWK.Speed = 0;
                    NWSHP(type);
                    INWK = saved;
                    break;
                }

                case "equip":
                    ECM = 0xFF;
                    BOMB = 0x7F;
                    ESCP = 0xFF;
                    GHYP = 0xFF;
                    break;

                case "tp":
                    TP = Convert.ToInt32(parts[1], 16);
                    break;

                case "sys":
                    // Pretend we are in the system at galactic coordinates (x, y)
                    QQ0 = int.Parse(parts[1]);
                    QQ1 = int.Parse(parts[2]);
                    break;

                case "enter":
                    // Dock immediately (GOIN)
                    DOENTRY();
                    break;

                case "kill":
                {
                    var ship = Slots[int.Parse(parts[1])];
                    if (ship != null)
                    {
                        ship.Flags |= Ship.FlagKilled;
                    }

                    break;
                }

                case "witch":
                    // Force a mis-jump into witchspace, as in TT18
                    TT66(0);
                    LL164();
                    MJP();
                    break;

                case "tally":
                    TALLY = int.Parse(parts[1]);
                    break;

                case "dock":
                    DKCMP = 0xFF;
                    break;

                case "trace":
                    _trace = new StreamWriter(parts[1]) { AutoFlush = true };
                    break;

                case "cash":
                    CASH = uint.Parse(parts[1]);
                    break;
            }
        }
    }
}
