# Missions

Each mission is a YAML file in this folder. The game loads them all when it
starts, checks them, and refuses to start if anything is wrong, with a message
that gives the file, the mission, the field and the line.

The game reads the files from the `Assets/Missions` folder next to
`EliteSharp.exe` (the build copies them there from this folder), so you can
edit them there and restart the game, without rebuilding it.

- `constrictor.yml`: mission 1, the stolen Constrictor.
- `thargoid-plans.yml`: mission 2, the Thargoid defence plans.
- `common.yml`: sequences that any mission can use, such as the "INCOMING
  MESSAGE" screen.

The missions are checked in file name order. When you dock, the first docking
event (in that order) whose conditions are met happens, and then the game
goes to the docking bay as usual.

## Adding a mission

Create `my-mission.yml` in this folder:

```yaml
id: my-mission          # the same as the file name
name: My Mission

progress:
  bits: [4, 5]          # bits of the mission byte not used by another mission
  stages:
    not-started: 0
    under-way: 1
    complete: 2

requires:               # optional: only offer this after mission 1
  constrictor: complete

docking:
  - name: briefing
    when:
      stage: not-started
      galaxy: 1
      system: Lave
    steps:
      - setStage: under-way
      - run: incomingMessage
      - clearScreen
      - text:
          row: 6
          justify: true
          lines:
            - "Greetings Commander {{commanderName}}."
            - " Please go to Diso."
      - run: messageEnds
      - waitForKey

  - name: debriefing
    when:
      stage: under-way
      galaxy: 1
      system: Diso
    steps:
      - setStage: complete
      - addCash: 1000
      - run: incomingMessage
      - clearScreen
      - text:
          row: 10
          lines:
            - "Well done, Commander."
      - run: messageEnds
      - waitForKey
```

## Mission fields

| Field | Required | What it is |
| --- | --- | --- |
| `id` | yes | The mission's id, which must be the same as the file name (without `.yml`). |
| `name` | yes | The mission's name, for people. |
| `progress` | yes | Where the mission's stage is saved, and its stages (see below). |
| `requires` | no | Other missions' stages that must be reached before any of this mission's docking events can happen, as `mission-id: stage`. |
| `values` | no | Named text that the mission's text can use (see below). |
| `docking` | no | A list of docking events (see below). |
| `target` | no | A ship that the mission sends us after (see below). |
| `shipRemoved` | no | What happens when particular ships leave the local bubble (see below). |
| `encounters` | no | Ships that the mission sends after us (see below). |
| `systemDescriptions` | no | Replacement descriptions for the Data on System screen (see below). |

### progress

The game saves one byte (the original's `TP`) for all the missions, in the
commander file. Each mission keeps its stage in some of its bits, so that saved
games stay compatible with the original's.

```yaml
progress:
  bits: [0, 1]            # neighbouring bits of the byte (0-7), not shared with another mission
  stages:                 # a name for each value of those bits
    not-started: 0        # one stage must be 0, as a new commander starts at 0
    hunting: 1
```

Stage names use lower case letters, digits and `-`. Mission 1 uses bits 0-1,
and mission 2 uses bits 2-3.

### values

Named text, which any text in the mission can use by writing `{{name}}`. A
value is one of:

```yaml
values:
  planetName: Diso                    # the same text every time
  captainName:
    byGalaxy:                         # text that depends on the galaxy we're in (1-8)
      1: Curruthers
      2: Fosdyke Smythe
  strange:
    oneOf: [FUNNY, WEIRD, UNUSUAL]    # chosen at random each time it's used
```

`{{commanderName}}` is built in: it is the commander's name.

Values can use other values. Random choices use the game's random number
generator (so they are part of the game's random sequence, as in the
original): the choices are made from left to right as the text is printed,
each using one random number from 0 to 255, split into equal parts (with five
choices, 0-50 picks the first, 51-101 the second, and so on). As in the
original, each of these random numbers is drawn with the 6502's C flag clear,
so the choice doesn't depend on what used the random number generator before.

The loader checks that every `byGalaxy` value has text for each galaxy where
it can be shown (for example, the galaxies in a docking event's `when`).

### docking

A list of events, each of which happens when we dock if its conditions are
met (only the first such event across all the missions happens):

```yaml
docking:
  - name: briefing          # for people and error messages
    when:
      stage: not-started    # required: the stage this mission must be at
      galaxy: [1, 2]        # optional: the galaxy (or a list of galaxies) we must be in
      system: Lave          # optional: the system we must be docked at (needs a single galaxy)
      minimumKillTally: 256 # optional: the lowest kill tally (see below)
    steps: [...]            # what happens (see Steps)
```

The kill tally (the original's `TALLY`) is the combat score that sets our
rank; each kill adds to it. For comparison, "Competent" starts at 128 and
"Dangerous" at 512, and mission 1 is offered at 256.

### target

A ship that the mission sends us after. While the mission is at the given
stage, the ship turns up in the given system in place of a group of pirates,
whenever there isn't one already in the local bubble.

```yaml
target:
  ship: constrictor
  system: { galaxy: 2, name: Orarra }
  stage: hunting
  aggression: 60            # 0-63
  ecm: true                 # whether it has an E.C.M.
```

To notice when the target has gone, use `shipRemoved`.

### shipRemoved

What happens when a ship of a particular type leaves the local bubble, which
is usually because it has been destroyed:

```yaml
shipRemoved:
  - ship: constrictor
    steps:
      - changeStage:
          hunting: constrictor-destroyed
      - addKillTally: 256
```

### encounters

Ships that come after us while the mission is at a particular stage. Each time
the game considers spawning extra ships, it draws a random number, and the ship
appears if the number is in the top `chanceIn256` of the 256 possibilities.
A Thargoid always brings a Thargon with it.

```yaml
encounters:
  - stage: carrying-plans
    ship: thargoid
    chanceIn256: 36
```

### systemDescriptions

Replacement descriptions for the Data on System screen, shown while we are
docked at the system and the mission is at one of the given stages. The game
justifies each one and adds a full stop. Like the original's, these are written
in capitals.

```yaml
systemDescriptions:
  stages: [hunting]
  systems:
    - galaxy: 1
      system: Xeer
      text: "THE CONSTRICTOR WAS LAST SEEN AT REESDICE, COMMANDER"
```

## Steps

Steps are carried out in order. Those without a value are written on their own
(`- clearScreen`), and the others as `- command: value`.

| Step | What it does |
| --- | --- |
| `setStage: stage` | Move this mission to a stage. |
| `changeStage: {from: to, ...}` | Move this mission from each of the given stages to another; if it is at any other stage, it stays there. |
| `addCash: credits` | Add cash to our account (to one decimal place, such as `5000` or `12.5`). |
| `addKillTally: amount` | Add to our kill tally. |
| `fitEquipment: navalEnergyUnit` | Fit the naval energy unit (the only equipment a mission can fit at the moment). |
| `run: sequence` | Carry out a shared sequence from `common.yml`. |
| `clearScreen` | Clear the screen. |
| `text: {...}` | Print text (see below). |
| `pause: seconds` | Pause, for a number of seconds from 0.02 to 5.1, in fiftieths of a second. |
| `waitForKey` | Wait for a key press. |
| `introduceShip: ship` | Clear the screen and show the ship flying in close, spinning and moving off to the top of the screen, where it stays spinning. |
| `showShipUntilKey` | Keep the introduced ship spinning until a key is pressed, then clear the text and move to row 10. Needs an `introduceShip` earlier in the same list of steps. |

Ships are named as in `Assets/Ships` (the file names without the number), such
as `constrictor`, `thargoid` or `cobra-mk3`. Systems are named as on the charts,
in any case.

### text

Prints lines of text in cyan, exactly as written (so write capitals where you
want them). Each line is followed by a new line.

```yaml
- text:
    row: 10               # optional: move to this text row first (1-23)
    column: 6             # optional: move to this column first (1-32)
    justify: true         # optional: justify the text in lines of 30 characters (default false)
    newlineAtEnd: false   # optional: don't start a new line after the last line (default true)
    lines:
      - "Greetings Commander {{commanderName}}."
      - " We would like you to do a little job for us."
```

Put each line in double quotes, so any spaces at the start are kept (the
original starts each paragraph after the first with a space). A long line can
be split over several lines in the file, as YAML joins them with a single
space. With `justify: true`, the game wraps long lines, spreading out the words
on each line to fill it; without it, the text is printed as it is.

When the text follows on from other text (for example, after
`showShipUntilKey`), leave out `row` and `column`, and it carries on from where
the cursor is.

## Shared sequences

`common.yml` has lists of steps that any mission can run with `run: name`:

- `incomingMessage`: clear the screen, show "INCOMING MESSAGE" and pause for 2
  seconds.
- `messageEnds`: print "MESSAGE ENDS" at the end of a message.

A file without an `id` is a shared file, containing only `sequences`. Shared
sequences can't use steps that belong to a mission (such as `setStage`), and
their text can only use `{{commanderName}}`.
