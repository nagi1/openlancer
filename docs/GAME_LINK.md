# Game link

OpenLancer does not include game files. Each user links their own copy of Freelancer (2003). The editor reads the files in the linked game folder. It never changes them.

## Link your game

At the first start, the Welcome page shows "Link your Freelancer game".

1. Click **Select game folder...**. Select the folder that contains the `EXE` and `DATA` folders of the game, for example `C:\Program Files (x86)\Microsoft Games\Freelancer`. You can also drag the folder to the editor window.
2. The editor checks the folder and shows the game that it found, with its notes. **Details** shows a table with each line of the check. Point to a short SHA-256 value to see all of it, or click the copy button after it.
3. Make sure that the profile name is correct. Click **Create profile and open**.

The editor makes a profile for the game and an empty mod, `my-mod`, for your changes. Then it opens the profile. Your first edit goes into `my-mod`.

When you select a folder inside the game, for example `EXE` or `DATA`, the editor finds the game folder. When you select the folder above it, for example `Microsoft Games`, the editor finds the game in it. When you select the installation disc or a copy of it, the editor tells you to install the game first.

**Skip** shows the normal Welcome page for this start. **Open a profile file...** opens a profile that you have already.

## What the editor finds

| Game | How the editor knows it | Export target of mods |
|---|---|---|
| Freelancer 1.0 | The five code files (`Common.dll`, `Server.dll`, `DALib.dll`, `FLServer.exe`, `Content.dll`) have the hashes of the English retail disc. Else the file version of `Common.dll` is 1.0.1223.11. | `vanilla-1.1` |
| Freelancer 1.1 | The file version of `Common.dll` is after 1.0.1223.11. OpenLancer has no hashes of 1.1 yet. | `vanilla-1.1` |
| With JFLP | `EXE\dacom.ini` names `jflp.dll`, and the `EXE` folder contains it. | the same as without JFLP |
| Freelancer: HD Edition | `EXE\freelancer.ini` names `Controls.dll` (0.7.1) or `HDEResources.dll` (after 0.7.1) as a resource DLL. The version comes from a text of that DLL. | `hde-0.7.1` for 0.7.1 only |
| Discovery | The game folder contains `DSLauncher.exe`. OpenLancer does not support it. | none |
| Unknown version | The code files have no known hash and no known version. You can use the folder. Some checks can be wrong. | none |

The editor also compares the INI files of the game with the file list of its version. The Game link window shows "INI files: not changed" or the number of changed files. OpenLancer uses the files in the game folder, also when they are changed.

OpenLancer has the file lists of the English retail 1.0 disc and of HD Edition 0.7.1. The options of the HD Edition installer change some files, for example `EXE\freelancer.ini` and `EXE\dacom.ini`. So an HD Edition install with other options shows changed files. The export of mods warns when the base of a target has changed files (see "Export" in [MOD_FORMAT.md](MOD_FORMAT.md)).

A folder without `Freelancer.exe` contains game data only. You can make mods with it.

## Where your files go

The editor puts new profiles and mods in the workspace folder, by default `Documents\OpenLancer`:

```
Documents\OpenLancer\
  profiles\freelancer-1-1.json     names the game folder and your mods
  profiles\freelancer-1-1.play\    the settings and saves of Play
  mods\my-mod\                     your mod
```

**Change...** on the profile screen selects another workspace folder. The new folder applies to new profiles only. A workspace folder in a game folder is not possible, because OpenLancer does not write into the game folder.

A developer who keeps the `GameData` layout can set the workspace folder to `GameData`. The editor also lists the profiles of `GameData\profiles`.

## The Game link window

**File > Game link...**, the game segment of the status bar and the Welcome page open the Game link window. It shows one card for each linked game:

- the game and the state of its INI files,
- its folder, with the link **Open folder**, which opens the folder in the file manager,
- the export target of its mods, the game program and the profiles that use the game,
- the notes of the check,
- **Details** with a table of the check: the file or the rule in the column Check, and what the check found in the column Result.

The buttons of a card:

- **New profile...** makes one more profile with an empty mod for the game, and opens it.
- **Check again** reads the game folder again.
- **Relink...** selects the new folder of the game, for example after you moved it.
- **Remove link** removes the game from the list. It deletes no file. A profile that uses the folder links it again when it opens.

**Link a game folder...** at the top right of the window links one more game. You can also drag a game folder to the editor window.

## A game folder that moved

When the game folder moved or its drive is not there, the card shows "Not found", and a profile of the game does not open. The Welcome page then shows "OpenLancer cannot find the game folder of profile ...".

1. Click **Open Game link**.
2. On the card of the game, click **Relink...**. Select the new folder. You can also drag the folder to the card.

When the new folder contains the same version of the game, the editor changes the `base` of each profile of the old folder. When the version is different, the editor asks first. The Console lists each profile that changed. The editor does not change a profile file in a game folder, a profile file in the OpenLancer repo, or a profile file with a JSON comment.

## oltool game

```
oltool game <folder> [--json] [--hashes] [--write-manifest <file>]
```

The command prints the same check as the editor. `--json` writes the full report with the evidence as JSON. `--hashes` adds the file list lines of the code files and of `Controls.dll`, `HDEResources.dll` and `jflp.dll`. The lines contain paths, sizes and hashes only, no game content.

`--write-manifest` writes the file list of the whole game folder to a file outside each game folder. The file has one line for each file, in the format of `data-manifest`. The list leaves out the uninstaller of the installer (`unins*` in the root, in any letter case), the `SAVE` folder and the `*.log` files. The command refuses an OpenLancer play copy.

The exit code is 0 for a game, 1 for a folder without a game, and 2 for a folder that does not exist or a wrong command line.

If you have Freelancer 1.1, please run `oltool game "<game folder>" --hashes` and send the output. OpenLancer then gets the hashes of 1.1.

## What the check reads

The check reads `EXE\freelancer.ini`, `EXE\dacom.ini`, the five code files, the marker DLLs and each INI file under `DATA` and `EXE`. It reads the version resource of a DLL from its bytes.

The check never starts a program of the game and never loads a game DLL as code. It never writes, renames or deletes a file in the game folder. It does not read the registry, does not search the disk and does not use the network.
