# EliteSharp

**The original Elite, rebuilt from the BBC Master source for modern Windows PCs.**

![A pirate Cobra Mk III sweeps across the planet, with a Mamba alongside](images/close-quarters.png)

Elite is the 1984 space trading and combat game that started it all. You begin
with a Cobra Mk III, 100 credits and a tank of fuel, docked at the space station
orbiting Lave. From there the eight galaxies are yours: trade between the stars,
hunt pirates for bounties (or turn pirate yourself), take on missions for the
Navy, and work your way up the ranks from Harmless all the way to Elite.

It is my aim to make EliteSharp the best way to play the original Elite on a PC
in 2026. It starts from the source code of the BBC Master version, released in
1986, and keeps its gameplay intact. The ships, the universe, the markets, the
combat and the missions all behave just as they did on the Master. Elite's
flight model, its economy and its difficulty are part of what made it great, so
EliteSharp doesn't rebalance or modernise any of them.

What has changed is everything around the game. Space is now a real 3D scene
drawn by your graphics card. You still see Elite's classic wireframes, but with
true perspective, planets that hide what's behind them, smooth motion and a
view that fills a widescreen display. The PC also allows some quality-of-life
improvements the Master had no room for: you can play in a window or full
screen at any size, fly with an Xbox controller, a HOTAS or a flight yoke, keep
as many labelled saves as you like, and change settings from a proper menu.

EliteSharp also draws inspiration from the other 8-bit versions of Elite, such
as those for the Commodore 64 and the NES.

## What you need

- A 64-bit Windows 10 or 11 PC
- The free [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
  from Microsoft (if it isn't installed, the game offers to take you there)
- A graphics card with Vulkan 1.3 support (almost any card from the last ten years,
  with up-to-date drivers)
- Optionally, an Xbox controller (or any controller Windows recognises), a HOTAS
  such as the Saitek X52, or a flight yoke

## Getting the game

Install the [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
if you don't have it (on that page, it's the **.NET Runtime** download for
Windows x64). Then download the zip for the latest version from the
[Releases page](https://github.com/elaverick/EliteSharp/releases), unzip it
anywhere, and run `EliteSharp.exe` in the `EliteSharp` folder.

### Building the game yourself

To play the latest changes, or to work on the game, you can build it yourself,
which only takes a couple of minutes:

1. Install the free [.NET 10 SDK](https://dotnet.microsoft.com/download) from
   Microsoft.
2. Download the game, either with **Code → Download ZIP** on the GitHub page (then
   unzip it), or with Git:
   ```
   git clone https://github.com/elaverick/EliteSharp.git
   ```
3. Open a command prompt in the game's `src\EliteSharp` folder and run:
   ```
   dotnet run -c Release
   ```

After the first build, you'll find `EliteSharp.exe` in
`src\EliteSharp\bin\Release\net10.0`, and you can run that directly.

The missions are written in YAML in `src\EliteSharp\Assets\Missions` (see the
README there). The game reads them from the `Assets\Missions` folder next to
`EliteSharp.exe` when it starts, so you can change them or add your own there
without rebuilding the game (the build copies them from `src`). To run the tests, run `dotnet test` in the
`tests\EliteSharp.Tests` folder.

The game's fixed text (the screen titles, the commodity and equipment names,
the in-flight messages and so on) is in `src\EliteSharp\Assets\Strings\en-strings.yml`,
and the text it generates for each system (the "goat soup" descriptions and
the species) is in `en-descriptions.yml` beside it. The build copies them to
`Assets\Strings` next to `EliteSharp.exe`. A translation is another pair of
files named after its language (such as `fr-strings.yml` and
`fr-descriptions.yml`), which `--language fr` selects. The goods in the
markets and the equipment prices are in `src\EliteSharp\Assets\trading.yml`.

The sound effects are OGG files in `src\EliteSharp\Assets\Sounds`, recorded
from an emulation of the BBC Master's sound chip playing the original's sound
data (`python tools/render_sounds.py` makes them again). You can replace any
of them with your own OGG file, mono or stereo, with the same name.

The music, from the Commodore 64 version, is in `src\EliteSharp\Assets\Music`:
`title.ogg` plays on the title screens, and `docking.ogg` (The Blue Danube)
while the docking computer flies you in. Each file's `LOOPSTART` and
`LOOPLENGTH` (or `LOOPEND`) tags, in samples, say which part of it repeats,
so an introduction plays once; without them, the whole piece repeats. You
can replace either file, or leave it out for silence.

## Starting out

When the game starts, it asks if you want to load a saved commander. Press
**N** to start a new career as Commander Jameson, then **Space** to go to your
ship. Press **Y** to pick one of your saved commanders instead.

A few tips for new pilots:

- **Launch** with **F1**. You'll fly out of the station's slot into space.
- **Trade** by buying goods cheaply in one system (**F2** while docked) and
  selling them for more in another (**F3**). Agricultural worlds sell food
  cheaply, and industrial worlds pay well for it. Look at a system's data
  (**F7**) to see what kind of economy it has. On the buy and sell screens,
  move up and down the list with the cursor keys, and use left and right (or
  type a number) to choose how much goes in your basket; **Y** fills it with as
  much as you can. Press **Return** to buy or sell everything in the basket, or
  **Escape** to empty it. Equipment (**F4** while docked) works the same way:
  pick an item and press **Return** to buy it.
- **Travel** by picking a destination on the short-range chart (**F6**) with the
  cursor keys, then pressing **H** to start the hyperspace countdown. You can
  jump as far as your fuel allows (up to 7 light years).
- **Docking** is the hardest thing in Elite. Fly towards the station, line
  yourself up with the slot, match the station's spin, and fly in slowly. Until
  you get the hang of it, save up for a **docking computer**. Press **C** to let
  it fly you in.
- **Save** your commander while docked by pressing **S** on the Status screen
  (**F9**), and load one with **L**. Do this often! See
  [Saving and loading](#saving-and-loading) below.

## Keyboard controls

The game uses the original BBC Micro keys wherever possible. The BBC's red
function keys f0 to f9 are on your **F1** to **F10** keys.

### Flying

| Key | Action |
| --- | --- |
| **<** and **>** (the , and . keys) | Roll left and right |
| **S** and **X** | Dive and climb |
| **Space** and **?** (the / key) | Speed up and slow down |
| **A** | Fire laser |
| **T** | Target a missile |
| **M** | Fire the targeted missile |
| **U** | Unarm the missile |
| **E** | Fire the E.C.M. (destroys incoming missiles) |
| **Tab** | Set off the energy bomb |
| **J** | In-system jump (a quick hop towards the planet, if nothing is nearby) |
| **H** | Hyperspace to the selected system |
| **Ctrl + H** | Galactic hyperspace (needs a galactic hyperdrive) |
| **C** / **P** | Turn the docking computer on / off |
| **Escape** | Launch the escape pod |

### Screens

| Key | Docked | In flight |
| --- | --- | --- |
| **F1** | Launch | Front view |
| **F2** | Buy cargo | Rear view |
| **F3** | Sell cargo | Left view |
| **F4** | Equip ship | Right view |
| **F5** | Galactic chart | Galactic chart |
| **F6** | Short-range chart | Short-range chart |
| **F7** | Data on the selected system | Data on the selected system |
| **F8** | Market prices | Market prices |
| **F9** | Status | Status |
| **F10** | Inventory | Inventory |
| **S** / **L** | Save / load commanders (on the Status screen) | |
| **F12** | Settings | Settings |

On the charts, the **cursor keys** move the cross-hairs (hold **Shift** to move
them faster), **D** shows the distance to the selected system, **O** moves the
cross-hairs back to your current system, and **F** (while docked) lets you find
a system by typing its name.

### Pausing and options

Press **F11** (or **Pause**, or **End**) to pause the game. While paused:

| Key | Option |
| --- | --- |
| **Delete** or **Backspace** | Carry on playing |
| **Escape** | Quit to the title screen |
| **Q** / **S** | Sound off / on |
| **<** / **>** | Volume down / up |
| **Caps Lock** | Keyboard damping on / off |
| **A** | Keyboard auto-recentre on / off |
| **F** | Flashing console bars on / off |
| **X** | Show the authors' names on the title screen (and allow manual mis-jumps into witchspace) |
| **Y** | Reverse the controller's pitch axis |
| **J** | Reverse both controller axes |
| **K** | Controller-only flying (see below) |

Press **Alt + Enter** at any time to switch between a window and full screen.

### Settings

Press **F12** at any time (or **RB** on the controller while paused) for the
Settings screen, where you can switch between a window and full screen, change
the window's size and the screen's shape, turn vsync, smooth motion and the
controllers on and off, and set the volume of the sound effects and music. Move
up and down with the cursor keys, change the highlighted setting with left and
right, and press **Escape** (or **B**, or **F12** again) to carry on playing.
**Controls**, near the bottom, opens the Controls screen, where you can change
what your controller's buttons do (see below).

Your settings are saved in `settings.yml` in the data folder
(`%APPDATA%\EliteSharp`, unless you use `--data`), along with the window's
size if you resize it and whether you switched to full screen with
**Alt + Enter**, and the game starts with them next time. The options below
override them for one run, without changing the file.

### Saving and loading

While you're docked, press **S** on the Status screen (**F9**) to save your
commander, or **L** to load one. Both are also at the bottom of the Settings
screen while you're docked.

A commander can have as many saves as you like. The **Save Commander** screen
lists your commander's saves, newest first, below **New Save** and
**Save As New Commander**. Pick **New Save** to add a save, or pick an old save
to save over it. You can then label the save. It starts as where you're docked,
such as "Docked at Lave", and you can type over it or just press **Return** to
keep it.

The **Load Commander** screen lists every commander you've saved, with the most
recent first. Pick one to see their saves, and pick a save to load it. Under the
list are the highlighted save's details: when it was made, where, and the
commander's cash and rating. **Start Again As JAMESON** starts a new career.

On either screen, **Delete** deletes the highlighted save, or on the Load
Commander screen, a whole commander and all their saves. It asks first.
**Escape** goes back.

## Playing with a controller

Plug in an Xbox controller (or any controller Windows recognises), a HOTAS or a
flight yoke and it just works, even while the game is running. You can use the
keyboard and any number of controllers at the same time.

A PlayStation 4 or 5 controller works the same way, with its own buttons in
place of the Xbox controller's: Cross for A, Circle for B, Square for X,
Triangle for Y, L1 and R1 for LB and RB, L2 and R2 for the triggers, Create (or
Share) for Back and Options for Start.

The **left stick** flies the ship, just like the analogue joystick you could plug
into a BBC Master: push left and right to roll, and pull back to climb. The
**triggers** are your throttle: the harder you squeeze the **right trigger**, the
faster you speed up, and the **left trigger** slows you down. Let go of the
stick, and the keyboard takes over again. If you'd rather the stick was always in
charge, press **K** while paused.

On the charts, either stick moves the cross-hairs. On the buy and sell screens,
the D-pad or left stick moves up and down the list and changes how much is in
your basket; **A** buys or sells it, **B** empties it, and **Y** fills it with as
much as you can. On the equipment screen, **A** buys the highlighted item (and
picks the view for a laser), and **B** changes your mind about a laser. While
paused, **RB** opens the Settings screen, which has **Save Commander** and
**Load Commander** at the bottom while you're docked. On those screens, **A**
chooses, **X** deletes and **B** goes back.

![Xbox controller controls](images/xbox-controls.png)

You still need the keyboard for typing, such as naming a new commander or
changing a save's label (**A** takes the label as it is).

### HOTAS and flight yokes

The game recognises the Saitek (or Logitech) **X52** and **X52 Pro** and the
Saitek **Pro Flight Yoke**, and knows their buttons by name. The stick (or the
yoke, turned and pushed) flies the ship just as the left stick does, and the
**throttle** (on the yoke, the left-hand lever) sets your speed, the way the
Bitstik's twist does on a BBC Master: wherever it is, that's your speed, from
stopped with it fully back to full speed with it fully forward. The speed keys
only work while it's fully back, and the docking computer takes over the speed
while it's on. The other axes, such as the X52's twist, rotaries and slider, do
nothing until you give them something to do. Elite's ships only roll and pitch,
so no axis makes the ship yaw or slide.

On the X52, the trigger fires the laser, the hat on the top of the stick changes
the view, and holding the pinkie switch gives the buttons their second
functions. On the yoke, the button on the back of the left handle fires, and the
hat changes the view. The Controls screen shows the rest.

Thunderstick Studios' **GRS Star Wars Flight Yoke** works too, in its gamepad
mode. It has no throttle, so the left handle's thumb button speeds up and its
trigger slows down; the right trigger fires the laser and the right thumb button
launches a missile. Holding function button 1 gives the second functions
(targeting and unarming missiles, E.C.M., the in-system jump and, with function
button 2, the docking computer), and function button 2 pauses.

Any joystick the game doesn't recognise shows its USB ID on the Controls
screen, which is what a profile needs to recognise it (see Mods, below).

Any other joystick works too, with its buttons, axes and hats shown by number:
it rolls and pitches with its first two axes, and you can give it a throttle on
the Controls screen.

### The Controls screen

The **Controls** screen, from the Settings screen, shows your controller (by the
name it gives itself, such as "PS5 Controller") as an Elite-style wireframe,
with the list of what its controls do below it. The top row of the
list picks the controller (if you have more than one plugged in), and the next
row picks the page: the **Axes**, or the buttons **In Flight**, on the
**Other Screens**, as **Second Functions** (while the button at the top of that
page is held), while **Paused**, and while **Typing**. Left and right change the
page from anywhere in the list.

Whatever you press on the controller lights up green on the picture, and its
name, and what it does on the page, appear under the list, so you can see which
control is which. The highlighted row's controls are marked in magenta.

To change a row, highlight it and press **Return** (or **A**), then press the
button or hat you want for it, or on the Axes page, move the axis the way the
screen asks (which tells the game which way round it works). **Escape** cancels,
and **Delete** clears the row. **Reset To Defaults**, at the end of the list,
puts everything back. Your changes are saved in `controllers.yml` in the data
folder. Each model of controller has its own bindings, and so does each kind
of gamepad (Xbox, PlayStation 4 and PlayStation 5); any other gamepad shares
the Xbox controller's.

## Options

Most of the display settings are on the Settings screen too (see above). You
can add these options to the end of the command that starts the game, for
example `EliteSharp.exe --fullscreen --scale 3`, or add them to a Windows
shortcut:

| Option | What it does |
| --- | --- |
| `--fullscreen` | Start in full-screen mode |
| `--scale <1-8>` | The size of the window, as a multiple of the original screen size (default 4) |
| `--window <width>x<height>` | The size of the window in pixels, e.g. `--window 1920x1080` (any shape works; the 3D view fills the width) |
| `--frame 4:3` | Keep the display within the original's 4:3 frame, rather than stretching it to the full width of the window |
| `--simulation-rate <1-50>` | The game speed, in main loop updates per second (default 16, which feels like the original). The display is drawn once per screen refresh, whatever this is |
| `--novsync` | Draw the display as often as possible, rather than once per screen refresh (the game runs at the same speed either way) |
| `--nointerpolation` | Show each of the game's updates as it comes, rather than moving the 3D view smoothly between them (which shows the view up to one update late) |
| `--nosound` | Turn off the sound |
| `--nopad` | Ignore any game controllers |
| `--data <folder>` | Where to keep saved commanders |
| `--language <code>` | The language of the game's text, from `Assets\Strings\<code>-strings.yml` and `<code>-descriptions.yml` (default `en`) |
| `--game <folder>` | Play a mod: use the assets in this folder (usually a path relative to where you start the game) in place of the `Assets` folder (see below) |

### Mods

A mod is a folder laid out like the `Assets` folder next to `EliteSharp.exe`,
which `--game <folder>` (or `-game <folder>`) uses in its place, for example
`EliteSharp.exe --game mods\my-mod`. A mod only needs the files it changes;
anything it doesn't have comes from the `Assets` folder:

* `Strings`, `Images`, `Sounds` and `trading.yml`: each file the mod doesn't
  have (such as `Strings\en-strings.yml`) is the game's own.
* `Ships`: each ship type the mod doesn't define (by the `type` in its JSON
  file) is the game's own, and each model a ship uses (such as
  `Models\cobra-mk3.gltf`) comes from the mod if it has it, or else the game.
* `Missions`: if the mod has a `Missions` folder, its missions replace all of
  the game's (so an empty folder means no missions), and if it doesn't have
  `common.yml`, the game's is used. Without a `Missions` folder, the game's
  missions are used.
* `Controllers`: each controller profile the mod doesn't have (such as
  `x52.yml`) is the game's own, and the mod can add profiles for other
  controllers. A profile names a controller's buttons, axes and hats, says
  where they are on its picture and what they do by default, and recognises
  the controller by its USB IDs (the game's own profiles are examples). The
  game's pictures are wireframe models drawn by `tools\draw-controllers.py`,
  which also writes where each control is into the profiles. A gamepad
  profile says which kinds of gamepad it is for (as SDL names them, such as
  `ps5`).

Your saved commanders live in the `Commanders` folder in `%APPDATA%\EliteSharp`
(or the folder given with `--data`), one small file per save.

## Credits and thanks

**Elite was written by David Braben and Ian Bell**, and first published by
Acornsoft for the BBC Micro in 1984. The BBC Master version that EliteSharp
is built from was released by Acornsoft in 1986. Their game created a whole genre
and inspired generations of players and programmers, and EliteSharp exists
purely out of admiration for what they achieved.

**Ian Bell released the original source code for Elite** on his website, which
is what made a faithful recreation like this possible.

**Mark Moxon** has spent years turning that source code into a fully documented,
line-by-line commentary on how Elite works. His annotated BBC Master source
([github.com/markmoxon/elite-source-code-bbc-master](https://github.com/markmoxon/elite-source-code-bbc-master)),
and his wonderful website at [bbcelite.com](https://www.bbcelite.com), explain
every routine, every table and every trick in the game. EliteSharp was translated
from his commented code, and it was used at every step to check that the game
plays exactly like the original. This project simply wouldn't exist without
his amazing work, so thank you, Mark.

**Aidan Bell and Julie Dunn** wrote the music for the Commodore 64 version of
Elite, including its arrangement of The Blue Danube for docking. The music in
EliteSharp replicates their work.

**Claude**, by Anthropic, did much of the work of porting the game and has
been a partner in its development ever since.

EliteSharp also stands on the shoulders of:

- [Silk.NET](https://github.com/dotnet/Silk.NET), for Vulkan, windowing and input
- [SDL](https://www.libsdl.org), for game controller support
- [OpenAL Soft](https://openal-soft.org), for sound
- The Khronos Group, for [Vulkan](https://www.vulkan.org) and
  [glTF](https://www.khronos.org/gltf/), the format the ship models are stored in

EliteSharp is an unofficial, non-commercial fan project. It isn't affiliated
with, or endorsed by, David Braben, Ian Bell, Acornsoft or Frontier Developments.
Elite remains the work of its original authors.

Right on, Commander!
