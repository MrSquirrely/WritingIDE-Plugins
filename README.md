# WritingIDE Plugins

Four extensions for [WritingIDE](https://github.com/MrSquirrely/WritingIDE), built on its extension SDK (version 1.2 or later). They live here, not in the main program, because they go beyond what a writing IDE needs to do itself.

| Plugin | What it adds |
|---|---|
| [Ambient Soundscapes](#ambient-soundscapes) | Looping background sound that can follow the place a chapter is set in |
| [Map Maker](#map-maker) | A map of your world with a pin for each place, travel times, and a continuity rule for journeys |
| [Gamification](#gamification) | Experience, levels and achievements |
| [Genre Rule Packs](#genre-rule-packs) | Rules and manuscript checks for romance, mystery, fantasy, science fiction and thrillers |

Each plugin adds a window to WritingIDE's **Extensions** menu. A window opens as a tab; **Window > Split: Open in Second Pane** puts it beside the chapter you are writing.

## Install

Check out this repository next to WritingIDE's source (`..\WritingIDE`), close WritingIDE, and run:

```powershell
.\build\install.ps1
```

The script builds every plugin and copies each into its own folder under `%LocalAppData%\WritingIDE\Extensions`. Start WritingIDE again; **Tools > Extensions and API** lists the plugins and lets you switch any of them off.

- Install only some: `.\build\install.ps1 -Plugin MapMaker, Gamification`
- Remove them: `.\build\install.ps1 -Uninstall`
- WritingIDE's source is somewhere else: `.\build\install.ps1 -WritingIdeRoot C:\path\to\WritingIDE`

## Ambient Soundscapes

**Extensions > Soundscape.** Nine sounds, each with its own volume slider: Rain, Storm, Wind, Ocean, River, Fireplace, Night, Forest and Tavern. Move a slider to add a sound to the mix. The sounds are made on your PC from noise and tones, so the plugin ships no recordings and downloads nothing. They loop without a gap and fade when the mix changes.

- **Follow the story.** With this ticked, the mix changes with the chapter in front. A chapter plays its own mix if you saved one (**Use for this chapter**); otherwise it plays the soundscape of the place the chapter names most often. A chapter with neither leaves the sound as it is.
- **A place's soundscape** is its `Soundscape` attribute, such as `Ocean 70, Wind 30`. Type it on the place's page, or mix it by ear and press **Use as this place's soundscape**.
- **Your own sounds.** Put WAV files in the Sounds folder (**Open Sounds folder**) and press **Reload**: each becomes a sound with its own slider. Only uncompressed (PCM) WAV files work; MP3 and compressed WAV files are refused with a message.
- **Extensions > Soundscape: Play or Pause** starts and stops the sound without opening the window.

The mix and the volume are remembered per user; chapter mixes are saved in the project.

## Map Maker

**Extensions > Map.**

- **The map.** Import a picture (PNG, JPEG or BMP), draw one with the pen, or both. The picture is copied into the project's `Maps` folder, and the drawing is saved there too, so the map travels with the project. Five pens: coast and borders, water, forest, mountains, roads. The eraser removes a line with one click.
- **Pins.** Every place in the project is listed. Press **Pin**, then click the map. Type a name and press **Add** to create a new place and pin it. Drag a pin to move it; double-click it to open the place's page. Right-click a place name in a chapter and choose **Show on Map** to find it.
- **Scale and journeys.** Say how wide the map is (in km, miles or leagues). Click two pins and the bar under the map shows the straight-line distance and how long the journey takes on foot (30 km a day), on horseback (60), by cart (25) and by sailing ship (160).

### MAP001: Journey does not fit the map

A continuity rule. When a sentence says how long a journey between two pinned places took, the rule compares the time with the map:

> She rode from Greyhaven Lighthouse to Port Ashby in an hour.
>
> *'Greyhaven Lighthouse' to 'Port Ashby' is about 588 km on the map: 10 days on horseback. The text says an hour, which is too fast.*

It reads the way of travelling from the sentence (rode, walked, sailed, by cart); without one it accepts anything between walking and riding. It allows a factor of two and a half either way for winding roads and rests, and it ignores journeys by air (dragons, airships, portals). The distance is a straight line, so a road around a mountain range is longer than the rule thinks: switch MAP001 off for a project in **Tools > Options > Grammar Rules** if that makes it more noise than help.

## Gamification

**Extensions > Achievements.**

- **Experience.** One XP for every word you write, 100 for reaching your daily goal, 50 for finishing a writing sprint, and more for each achievement. Words are credited once: cutting a paragraph and typing it again earns nothing.
- **Levels.** Each level takes longer than the last (500 XP for level 2, 1,500 for level 3, 3,000 for level 4), and every third level brings a new title, from Scribbler to Legend.
- **30 achievements** for word counts (First Words to Doorstopper), big days, daily goals, streaks, sprints, writing at night or at dawn, worldbuilding (characters, places, lore, relationships, killing off a character) and finishing chapters.

The profile belongs to you, not to one project: it is kept per user and counts words across every project you open. Opening an existing project unlocks what it has already earned, in one notification.

## Genre Rule Packs

**Extensions > Genre Packs.** Each pack adds rules to the Error List and checks to a genre report. The packs follow the project's genre (**Project > Properties**): "Cozy mystery" turns on Mystery, "Romantic fantasy" turns on Romance and Fantasy. Untick **Choose the packs from the project's genre** to pick them yourself.

The rules give advice, not errors. Switch off any one of them in **Tools > Options > Grammar Rules**.

| Pack | Rules |
|---|---|
| Romance | `ROM001` worn-out phrases ("let out a breath she didn't know she was holding"), `ROM002` love at first sight, `ROM003` a love interest who first appears after the first quarter |
| Mystery | `MYS001` a culprit who first appears in the last third, `MYS002` luck solving the case ("as luck would have it"), `MYS003` a clue kept from the reader ("little did she know") |
| Fantasy | `FAN001` worn-out tropes (the chosen one, an ancient prophecy), `FAN002` modern words in a pre-industrial world ("okay", "adrenaline", "full throttle") |
| Science Fiction | `SCI001` metric and imperial units in one chapter, `SCI002` sound in a vacuum, `SCI003` light-years or parsecs used as time |
| Thriller | `THR001` a chapter far longer than the rest, `THR002` "suddenly" three times or more in a chapter, `THR003` a chapter that ends with someone falling asleep |

**The genre report** (**Run genre report** in the window) checks what needs the whole book:

- Romance: the love interest arrives in the first quarter and is in the last chapter.
- Mystery: the culprit arrives in the first two thirds; every clue is on the page before the last chapter.
- Fantasy: the magic has a cost or a limit.
- Science fiction: the manuscript measures in one unit system.
- Thriller: typical chapter length, chapters that run long, and whether the last third slows down.

The report and the late-arrival rules read your story elements:

- A character's **Role** attribute names the love interest (`Love interest`) and the culprit (`Culprit`, `Murderer` or `Killer`).
- An item with a **Clue** attribute is a clue.
- A Lore element with "magic" in its name or summary describes the magic; give it a **Cost** or **Limits** attribute.

## Build and test

```bash
dotnet build
```

```bash
dotnet test
```

The plugins need WritingIDE's SDK. `Directory.Build.props` looks for WritingIDE's source in `..\WritingIDE`; pass `-p:WritingIdeRoot=<folder>` if it is elsewhere. Without the source, the build uses an installed WritingIDE's `WritingIDE.Sdk.dll` and `WritingIDE.Core.dll` (`-p:WritingIdeInstallDir=<folder>`).

```
src/AmbientSoundscapes   Sound synthesis, a wave-out mixer, the mixer window
src/MapMaker             The map window, distances and travel times, rule MAP001
src/Gamification         Experience, levels, achievements, the Achievements window
src/GenreRulePacks       Five packs of rules, the genre report, the Genre Packs window
tests/Plugins.Tests      xUnit tests for the logic of all four
build/install.ps1        Builds and installs the plugins into WritingIDE
```

Plugins run with WritingIDE's rights, like any extension. Map Maker only opens map files inside the project folder, so a project from someone else cannot make it read files elsewhere.