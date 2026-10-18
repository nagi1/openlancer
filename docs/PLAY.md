# Play in the editor

Play starts the game from the editor with the edits of the active mod. The game reads the profile through the mod overlay, as the editor does. It needs no deploy. While the game runs, a save of the editor reloads the game, and the player continues at the same place.

The game is a second program, `OpenLancer.Play.exe`, next to the editor. The build of the editor builds it too.

## The Play controls

The main menu bar has a Play menu after the Mod menu. Three buttons in the middle of the bar do the same as the menu: Play, an arrow that opens the Play menu, and Stop.

| Control | Key | Effect |
|---|---|---|
| Play button, or Play > Play from camera | F5 | Plays at the Scene camera. While a game runs, the game closes and a new game starts. |
| Play > Play at base | | Plays docked at the base that you click. |
| Play > Ship: ... | | Shows the ship of the profile. It is not a command. |
| Play > Use the default ship | | Sets the ship of the profile back to the default. |
| Play > Reload game | | Reloads the running game without a save. |
| Stop button, or Play > Stop | Shift+F5 | Stops the game. Stop becomes Force stop when the game does not close. |

F5 and Shift+F5 do not work while a text field or a dialog is open. A click on the Play button works also while a field of the Inspector is open: the click ends the field, and the save of Play has its edit. The tooltip of a control that is not available tells why.

## Play from camera

1. Open a profile and a system.
2. Move the Scene camera to the start place. The ship starts at the camera and points where the camera looks.
3. Press F5, or click the Play button in the middle of the main menu bar. The Play menu has the same command: Play from camera.

Play then does these steps:

1. Play saves the edits of the active mod. The Console shows the save.
2. The game window opens at once, and the game loads its data. At the same time, the editor validates the save.
3. When the validation finds no errors, the ship spawns. The Console shows the place, the ship and the time from Play to flight, for example "Playing in New York (Li01) with Starflier ge_fighter. From Play to flight: 3.9 s."

Validation errors stop Play. Warnings do not stop it. Only the problems that the active mod adds count. A problem that vanilla or a lower mod has does not stop Play. When the validation cannot run, Play goes on with a warning.

Play loads the base data and the mods up to and with the active mod. These are the mods that the editor shows. When the profile has mods after the active mod, the Console names them. Without an active mod, Play loads all mods.

### What Play saves

| Case | What Play does |
|---|---|
| The active mod has unsaved edits | Play saves them, with all checks of Ctrl+S. When the save does not complete, Play stops. |
| No mod is active | Play saves nothing and plays the saved files of all mods. |
| Another editor has the mod open, or a recovery file waits | Play saves nothing and plays the saved files of the mod. The Console tells why. |
| The mod is in the OpenLancer repo, and you did not allow its edits | Play saves nothing and shows no dialog. It plays the saved files of the mod. |

### The start check

The camera can be inside a planet, a station or a death zone. A death zone is a zone with 1,000 or more damage per second, for example the zone around a sun. Then the start moves along the view axis to the nearer exit, plus 100 units. The Console tells how far the start moved, ahead of the camera or behind it.

A zone with less damage, for example a radiation cloud, does not move the start. The Console warns when the ship starts in such a zone.

The check knows the objects and the zones of the system. It does not know mines, asteroids or other ships.

## Play at base

Open the Play menu and select Play at base. The list shows the bases of the open system first. The other bases are in one submenu for each system. Click a base. The game starts with the ship docked at that base. Play at base needs no open system.

## The ship

The default ship is the ship of a new multiplayer character. In vanilla, this is the Starflier (`ge_fighter`) with the loadout `MP_ge_fighter` and 2,000 credits.

To fly another ship:

1. Open the Database and click Goods.
2. Right-click a row of category `ship`. Such a row is a ship package.
3. Click Use for Play.

The Play menu shows the ship of the profile, for example "Ship: Starrunner". To go back to the default ship, click Use the default ship in the Play menu. Each profile keeps its own ship.

The equipment of the package goes on its hardpoints. An item with an amount above 1 goes into the hold. A good that the engine cannot mount, for example a battery, also goes into the hold.

## In the game

A character of a play is an admin. These chat commands work:

| Command | Effect |
|---|---|
| `/pos` | Shows the position of the ship. |
| `/warp x y z` | Moves the ship to the position. |
| `/base <nickname>` | Docks the ship at the base. |
| `/godmode` | Makes the ship take no damage. |

The game window shows the debug view. Its Debug window shows the position of the player.

## The status of the game

The status bar of the editor shows the state of the game:

| Text | Meaning |
|---|---|
| Play: starting... | The game loads its data, or the ship spawns. |
| Play: validating save 2... | The game waits for the validation of the save. |
| Playing: New York (Li01), save 2 | The player is in the game. The game shows the files of save 2. |
| Playing: New York (Li01), parts of save 3 | Save 3 came while the game read the mod files. The game can show parts of save 3 and parts of the save before it. A reload follows when the validation of save 3 passes. |
| Play: reloading save 3... | The game loads the files of save 3 again. |
| Play: the game does not respond | The game did not load its data in 60 s, or it did not end a reload in 30 s. |
| Play: stopping... | The game closes. |

## Stop

Press Shift+F5, or click the Stop button. The game closes.

When the game does not close in 5 s, the Stop button becomes Force stop. Force stop ends the game process at once. When the game does not respond, Stop is Force stop at once.

Press F5 while the game runs to play again. The game closes, and a new game starts at the new camera place.

The game also closes in these cases:

- You close the profile.
- You change the active mod, or you make a new mod.
- You close the editor. The game gets 2 s to close, then Windows ends it.
- The editor ends in another way, for example by a crash. Windows then ends the game too.

## The reload

A save of the editor reloads the running game:

1. Press Ctrl+S.
2. The editor validates the save, as after each save.
3. When the validation finds no errors, the game shows its loading screen for less than 1 s.
4. The player continues in the same system, at the same place, with the same heading. At a base, the player is docked at the same base.

The Console then says, for example, "The running game shows save 3 (reload 0.6 s, 3.4 s from the save)." The first time is the time of the reload in the game. The second time is the time from the save.

When the validation finds errors, the game does not reload. The Console says which save the game keeps, for example "The running game keeps save 2. The validation of save 3 found 1 error."

When the validation cannot run, the reload goes on with a warning, as Play does.

A reload runs only while the player is in space or at a base. A save before the spawn reloads the game after the spawn.

One reload runs at a time. Saves during a reload wait. When the reload ends, the game reloads the last save. The older saves drop.

### Reload game

Click Play > Reload game to reload the game without a save. Use it after you change a mod file outside the editor. Reload game loads the last save also when its validation found errors.

Reload game is not available while the last save is not complete, because the game cannot load the files of such a save. Correct the problem of the file and save again. That save reloads the game.

### When a reload fails

The game keeps its data and continues at the same place. The Console gives the reason. These are the usual reasons:

- The new data does not have the system, the base or the ship of the player. For example, you renamed the ship of the player in the Database. Correct the data and save again, or press F5 to play again.
- A save of the mod is not complete. Correct the problem of the file and save again.
- The game left the play, for example with Quit to menu or Load game. Stop the game and press Play.

The editor does not send a failed reload again by itself. The next save, or Reload game, sends a reload.

### Saves while the game reads the mod files

The game reads the mod files while it loads and while it reloads. A save in that time can give the game parts of two saves. The editor knows this. After the load or the reload, the game reloads the last save when its validation passes. When the validation finds errors, the Console says "The running game can show parts of save 3. The validation of save 3 found 1 error."

## What needs Stop and Play

A reload loads all INI data and the texts of the mods again. Some data loads only at the start of the game. For a change to such data, stop the game and press Play.

| Data | After a reload |
|---|---|
| INI data: system objects, zones, lights, ships, equipment, loadouts, goods, prices, markets, factions, encounters and missions | New. A new price shows in the shops after the reload. |
| Texts of mods (`strings.json`) | New, also in the user interface of the game. |
| A changed cutscene (`.thn`) file that the mod had at the last load or reload | New, also without a reload. The game reads the file each time it plays the cutscene. |
| A cutscene (`.thn`) file that is new in the mod, also the first change of a vanilla cutscene | New after a reload. The game makes the list of the mod files when it loads them. |
| Models, textures and `.sur` files | Old. Stop and Play. |
| Sounds | Old for a sound that the game played before. Stop and Play. |
| Fonts, the key map, the cursors, effect shapes and character animations | Old. Stop and Play. |
| The interface code, its stylesheet and its resources | Old. Stop and Play. |

The editor writes only INI patches, cutscene (`.thn`) files and `strings.json`. So each save of the editor needs only a reload.

A reload keeps what a save keeps: the position, the cargo, the reputation, the story and the active mission triggers. Ships that a mission made come again from the triggers, so they can differ from the ships before the reload. A trade lane or a jump does not continue after a reload.

Equipment of the player that the new data does not have goes away without a message. When the new data makes a mounted good combinable, that good goes into the hold, because the engine cannot mount it.

## The play folder

Each profile has a play folder next to the profile file: `<profile folder>\<profile name>.play\`.

| File or folder | Content |
|---|---|
| `lancer.ini` | The game settings of the play. The game options write it, so the volumes and the graphics options stay between plays. The game always starts in a window with the debug view. |
| `saves\` | The saves of the player, `AutoSave.fl` of the reloads, and the files of the scripts. |

The main menu of the game is not part of a play. When you quit to the menu, the menu lists the saves of `Documents\SavedGames\OpenLancer`, and New Game saves there. The key map is in that folder too.

## When the game stops by itself

The Console tells why the game stopped:

| Exit code | Meaning |
|---|---|
| 0 | The game ended normally, for example with Exit in the game. The Console says "The game closed." |
| 1 | An error ended the game. |
| 2 | The editor gave wrong arguments. Report this as a bug. |
| 3 | The data cannot start the play: merge errors, a save that is not complete, or an unknown mod, system, base or ship package. |
| 4 | The save or the server stop of a reload did not end in 10 s. |
| 5 | The game did not start, for example because the game window did not open. |
| other | A crash. |

For an exit code other than 0 or 3, the Console gives the cause and the path of the game log, `%LOCALAPPDATA%\OpenLancer\logs\OpenLancer.Play.log.txt`. A crash writes the whole exception to that log.

When the validation finds errors and the game closes during its load, the Console names the validation errors as the cause.

The warnings and errors of the game log also go to the Console with the source "Play". The Console adds each line only once.
