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
    public void DebugCommand(string command)
    {
        _debugCommands.Enqueue(command);
    }

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
                    var saved = _currentShip;
                    ResetWorkspace();
                    int type = int.Parse(parts[1]);
                    _currentShip.Z = (parts.Length > 2 ? int.Parse(parts[2]) : 8) << 8;
                    _currentShip.X = parts.Length > 3 ? int.Parse(parts[3]) : 0;
                    _currentShip.Y = parts.Length > 4 ? int.Parse(parts[4]) : 0;
                    _currentShip.Ai = parts.Length > 5 ? Convert.ToInt32(parts[5], 16) : 0;
                    _currentShip.Behaviour = parts.Length > 6 ? Convert.ToInt32(parts[6], 16) : 0;
                    _currentShip.Speed = 0;
                    AddShip(type);
                    _currentShip = saved;
                    break;
                }

                case "equip":
                    _ecm = 0xFF;
                    _energyBomb = 0x7F;
                    _escapePod = 0xFF;
                    _galacticHyperdrive = 0xFF;
                    break;

                case "tp":
                    _missionStatus = Convert.ToInt32(parts[1], 16);
                    break;

                case "sys":
                    // Pretend we are in the system at galactic coordinates (x, y)
                    _currentSystemX = int.Parse(parts[1]);
                    _currentSystemY = int.Parse(parts[2]);
                    break;

                case "enter":
                    // Dock immediately (GOIN)
                    DockAtStation();
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
                    ClearScreen(0);
                    HyperspaceTunnel();
                    MisJump();
                    break;

                case "tally":
                    _killTally = int.Parse(parts[1]);
                    break;

                case "dock":
                    _dockingComputer = 0xFF;
                    break;

                case "facecheck":
                    // Compare the GPU's face visibility test with LL9's (needs trace)
                    _faceCheck = true;
                    break;

                case "trace":
                    _trace = new StreamWriter(parts[1]) { AutoFlush = true };
                    break;

                case "cash":
                    _cash = uint.Parse(parts[1]);
                    break;
            }
        }
    }
}
