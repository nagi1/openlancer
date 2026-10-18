# Build manifests

Each file `<build id>.tsv` lists the files of one game build, for example `vanilla-1.0.tsv` for the English retail 1.0 disc. The file name is the build id. Each line is `<lowercase path with '/'>` TAB `<size>` TAB `<lowercase SHA-256>`, the format of `tools/verify_base.py` and `DataDigest`. The editor and `oltool game` use the manifests to identify a linked game and to find its changed INI files (see [docs/GAME_LINK.md](../docs/GAME_LINK.md)). The build embeds them in `OpenLancer.Data`. A manifest contains paths, sizes and hashes only. A hash is not game content, so the manifests can stay in git.

A manifest of a patch can list only the files that the patch changes. `vanilla-1.1` takes its other files from `vanilla-1.0`. To add the hashes of a build, run `oltool game "<game folder>" --hashes` on that build, and put the lines in `<build id>.tsv`.
