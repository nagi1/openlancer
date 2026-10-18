# OpenLancer

OpenLancer makes Freelancer (2003) openly moddable. It adds a mod overlay with a load order, and an editor for the game content. The editor has data editors, a 3D scene editor for star systems, and a cutscene editor. OpenLancer runs on its own fork of the [Librelancer](https://codeberg.org/Librelancer/Librelancer) engine. The fork is a git submodule at `external/Librelancer` (see docs/UPSTREAM.md).

> **No game files are in this repository.** It holds only OpenLancer's own code and documentation. There is no game data, no game executable and no game DLL. To use OpenLancer, you link your own copy of Freelancer (2003).

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first. Fork the repository and open a pull request. The owner reviews each pull request before it goes into `master`. [ROADMAP.md](ROADMAP.md) shows the milestones.

## Legal

OpenLancer is an independent project. It is not affiliated with, endorsed by or sponsored by Microsoft, Digital Anvil, the Librelancer project or any holder of a "Freelancer" trademark. OpenLancer uses the name "Freelancer" only to identify the 2003 game that it works with. All trademarks belong to their owners.

This repository contains no game files and no game data. You need your own copy of Freelancer (2003). OpenLancer is licensed under the MIT License (see LICENSE). The third-party notices are in NOTICE.

## Folder layout

The repo is one folder in a larger workspace. The game data stays outside the repo.

```
<workspace>\
  Freelancer.iso      your retail disc image
  GameData\           private, never in git
    base\             extracted vanilla data (read-only)
    mods\  profiles\  out\  fonts\
  OpenLancer\         this repo
    assets\icon\            the icon of the programs: openlancer.svg and the files of tools/make_icon.py
    external\Librelancer\   submodule (see docs/UPSTREAM.md)
    release\                the texts and the license data of a binary release (tools/release.ps1)
    tests\
    tools\
```

## Set up

### Players

You need your own installed copy of Freelancer (2003): the retail game 1.0 or 1.1, with or without JFLP, or Freelancer: HD Edition.

1. Unzip the OpenLancer release to a folder of your choice.
2. Start `OpenLancer.Editor.exe`.
3. On the page "Link your Freelancer game", click **Select game folder...**. Select the folder that contains the `EXE` and `DATA` folders of your game, for example `C:\Program Files (x86)\Microsoft Games\Freelancer`. You can also drag the folder to the editor window.
4. The editor shows the game and the version that it found. Click **Create profile and open**.

The editor makes a profile for your game and an empty mod, `my-mod`, and opens the profile. Your edits go into `my-mod`. OpenLancer reads the files of the game folder. It never changes them. Your profiles and mods go to `Documents\OpenLancer`. **File > Game link...** shows the linked games and their profiles. When you move the game, **Relink...** in that window gives the profiles the new folder. See [docs/GAME_LINK.md](docs/GAME_LINK.md).

### Developers

1. Clone with submodules: `git clone --recursive <url> OpenLancer`.
2. Extract the game data to `GameData\base` and check it with `python tools\verify_base.py ..\GameData\base`. The English v1.0 retail disc gives `8418 5b4ee64cf64bb9c5525cee786f54380170dbde5617d1aaf5ef3655237c9cfaeb`.
3. Build the engine submodule once. See "Build the submodule" in [docs/UPSTREAM.md](docs/UPSTREAM.md).
4. Run `dotnet test OpenLancer.slnx`.
5. Run `start.bat` to build and start the editor. The editor lists the profiles of `GameData\profiles` and of the workspace folder. To link the game as a player does, click **File > Game link... > Link a game folder...** and select `GameData`. The editor finds `GameData\base`.

The data tests read `..\GameData\base`. Set `OPENLANCER_BASE` to use a different folder. Without game data, the data tests are skipped. `oltool game <folder>` prints the check of a game folder (see [docs/GAME_LINK.md](docs/GAME_LINK.md)).

## Make a mod

See [docs/MOD_FORMAT.md](docs/MOD_FORMAT.md). `samples/mods/ol-sample` is a complete example: it changes a price, and it adds a new ship that Manhattan sells.

1. Make a profile, for example `GameData\profiles\my.json` with `{ "mods": [ "../mods/my-mod" ] }`.
2. Run `oltool validate GameData\profiles\my.json`.
3. Run `oltool deploy GameData\profiles\my.json`.
4. Start the game with `lancer.exe GameData\profiles\my.lancer.ini`.

## Play from the editor

Run `start.bat` to build and start the editor. Open a profile and a system, then press F5. The game starts at the Scene camera with the edits of the active mod, and a save reloads the running game. See [docs/PLAY.md](docs/PLAY.md).

## Study and edit cutscenes

Open the Cutscenes tab of the editor to browse the retail cutscenes and the cutscenes of the mods. Open one to preview it on a timeline and to see each value. With Mod > None (read-only), the editor takes no edits. With an active mod, a save writes the changes of a cutscene as a small THN patch. See [docs/CUTSCENES.md](docs/CUTSCENES.md).
