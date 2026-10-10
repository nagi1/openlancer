# OpenLancer

OpenLancer makes Freelancer (2003) openly moddable. It adds a mod overlay with a load order, and an editor for the game content. The editor has data editors and a 3D scene editor for star systems. OpenLancer runs on the [Librelancer](https://github.com/Librelancer/Librelancer) engine. Librelancer is an unmodified git submodule.

OpenLancer contains no Microsoft game files. You supply the game data from your own copy of Freelancer.

## Folder layout

The repo is one folder in a larger workspace. The game data stays outside the repo.

```
<workspace>\
  Freelancer.iso      your retail disc image
  GameData\           private, never in git
    base\             extracted vanilla data (read-only)
    mods\  profiles\  out\  fonts\
  OpenLancer\         this repo
    external\Librelancer\   submodule (see docs/UPSTREAM.md)
    tests\
    tools\
```

## Set up

1. Clone with submodules: `git clone --recursive <url> OpenLancer`.
2. Extract the game data to `GameData\base` and check it with `python tools\verify_base.py ..\GameData\base`. The English v1.0 retail disc gives `8418 5b4ee64cf64bb9c5525cee786f54380170dbde5617d1aaf5ef3655237c9cfaeb`.
3. Apply OpenLancer's local Librelancer fixes and build Librelancer once. See [docs/UPSTREAM.md](docs/UPSTREAM.md).
4. Run `dotnet test OpenLancer.slnx`.

The data tests read `..\GameData\base`. Set `OPENLANCER_BASE` to use a different folder. Without game data, the data tests are skipped.

## Make a mod

See [docs/MOD_FORMAT.md](docs/MOD_FORMAT.md). `samples/mods/ol-sample` is a complete example: it changes a price, and it adds a new ship that Manhattan sells.

1. Make a profile, for example `GameData\profiles\my.json` with `{ "mods": [ "../mods/my-mod" ] }`.
2. Run `oltool validate GameData\profiles\my.json`.
3. Run `oltool deploy GameData\profiles\my.json`.
4. Start the game with `lancer.exe GameData\profiles\my.lancer.ini`.
