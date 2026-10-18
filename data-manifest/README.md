# Build manifests

Each file `<build id>.tsv` lists the files of one game build, for example `vanilla-1.0.tsv` for the English retail 1.0 disc. The file name is the build id. Each line is `<lowercase path with '/'>` TAB `<size>` TAB `<lowercase SHA-256>`, the format of `tools/verify_base.py` and `DataDigest`. The editor and `oltool game` use the manifests to identify a linked game and to find its changed INI files (see [docs/GAME_LINK.md](../docs/GAME_LINK.md)). The build embeds them in `OpenLancer.Data`. A manifest contains paths, sizes and hashes only. A hash is not game content, so the manifests can stay in git.

The export and `tools/content_scan.py` also read each manifest. A file with the bytes of a file of a manifest is a game file, so a package or the repository must not contain it.

| Manifest | Source |
|---|---|
| `vanilla-1.0.tsv` | The English retail 1.0 disc, extracted. |
| `vanilla-1.1.tsv` | An English retail install with patch 1.1. The file lists only the files that the patch changes: the five code files `Common.dll`, `Server.dll`, `DALib.dll`, `FLServer.exe` and `DLLS\BIN\Content.dll`, and `RemoteClient.dll`. Each other file of the install has the hash of `vanilla-1.0.tsv`. |
| `hde-0.7.1.tsv` | An English install of HD Edition 0.7.1. The options of the HD Edition installer change some files, among them `EXE\freelancer.ini` and `EXE\dacom.ini`. So the install of another user can differ in these files. |

A manifest of a patch can list only the files that the patch changes. `vanilla-1.1` takes its other files from `vanilla-1.0`. To add the hashes of a build, run `oltool game "<game folder>" --hashes` on that build, and put the lines in `<build id>.tsv`.

To write the manifest of a whole game folder, run `oltool game "<game folder>" --write-manifest <file>`. The file must be outside each game folder. The list leaves out the uninstaller of the installer (`unins*` in the root), the `SAVE` folder and the `*.log` files. These files differ between two installs of one build.
