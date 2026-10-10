# OpenLancer mod format (version 1)

An OpenLancer mod changes the game data without a change to the base files. A profile lists the mods. OpenLancer merges them over the extracted vanilla data in load order. The result is a merged view of the data. The base folder never changes.

## Mod folder

```
<mod>/
  mod.json                     required
  strings.json                 optional: text for names and infocards
  patches/<install path>.ini   entry-level changes to an INI file
  files/<install path>         a file that the mod adds or replaces
  .olsave/                     the editor's save folder (see "The editor"). The merge ignores it.
```

An install path is relative to the game root, for example `DATA/EQUIPMENT/goods.ini` or `EXE/freelancer.ini`. Letter case does not matter.

### mod.json

```json
{
  "id": "my-mod",
  "name": "My mod",
  "version": "1.0.0",
  "formatVersion": 1,
  "dependsOn": [ "other-mod" ],
  "loadAfter": [ "optional-mod" ]
}
```

| Field | Rule |
|---|---|
| `id` | Lowercase letters, digits, `-` and `_`. It starts with a letter. |
| `formatVersion` | Must be `1`. |
| `dependsOn` | Mods that must be in the profile. They load before this mod. |
| `loadAfter` | Mods that load before this mod when they are in the profile. |

## Patches

A patch file is a text INI file. Each section in it changes one section of the target file.

### How a patch section finds its target

Most sections are found by their `nickname` entry:

```ini
[Good]
nickname = le_hull
price = 9000
```

Some section types use another key:

| Section | Key |
|---|---|
| `[FactionProps]` | `affiliation` |
| `[fuse]` | `name` |
| `[RepChangeEffects]` | `group` |
| `[Nebula]`, `[Asteroids]` | `zone` |
| `[SystemInfo]`, `[Music]`, `[Dust]`, `[Ambient]`, `[Background]`, `[TexturePanels]` | none: the file has one of each |
| `[BaseGood]` | none: patch sections are always added (see below) |

If the key matches no section, or more than one section, the merge fails with an error.

### What a patch section does

- The patch's entries replace all target entries with the same names. For example, three `addon` lines replace all `addon` lines of the target. So list every line of a name that you change.
- The patch's entries are written as one block, in patch order, at the position of the first entry that they replace. When the target has none of the names, the block goes at the end of the section.
- Entries that the patch does not name stay as they are.

### Lines that belong together

The engine attaches some lines to the line before them. For example, each `faction` and `faction_weight` line of a `[Zone]` belongs to the `encounter` line before it. When a patch changes one name of such a group, it must list all lines of all names in the group, in the right order. Otherwise the merge fails with an error.

| Section | Group |
|---|---|
| `[Zone]` | `encounter`, `faction`, `faction_weight` |
| `[GF_NPC]` | `know`, `knowdb` |
| `[GF_NPC]` | `rumor`, `rumor_type2`, `rumorknowdb` |
| `[EncounterFormation]` | `ship_by_class`, `ship_by_npc_arch`, `pilot_job`, `make_class` |

### Value types in binary files

Most retail INI files are binary. A binary file stores each value as a boolean, an integer (32 bits), a float or text. A patch is text, so the merge gets the type of a patch value from its text: `5` is an integer, `5.0` is a float, `true` is a boolean and `li_gun` is text.

In a patch section, the first line of a name replaces the first line of that name in the target. The second line replaces the second line, and so on. Each value replaces the value at the same position in its line. A value that replaces no value, for example in a new section, keeps the type of its text. The exceptions are `true`, `false` and large numbers (see below).

The retail binary files store no booleans. They store `true` and `false` as text. So the merge stores `true` and `false` as text in these cases:

- `true` or `false` that replaces text stays text. When the old text is `true` or `false` in any letter case and the truth value does not change, the old text stays, for example `TRUE`. Other text, for example `yes`, becomes `true` or `false`.
- `true` or `false` that replaces no value becomes the text `true` or `false`, in lowercase.

An integer is a whole number from -2147483648 to 2147483647. The value that an integer replaces can change its type:

- An integer that replaces a float becomes a float when it is from -16777216 to 16777216. A float holds every whole number in this range exactly. Other integers stay integers, for example a hash such as `-1980013182`.

A whole number outside the integer range is a large number. A binary file has no type that holds every large number. So the value that it replaces sets its type:

- A large number from 2147483648 to 4294967295 that replaces an integer is a hash, for example `2314954114`. The file stores the integer with the same 32 bits.
- A large number that replaces a float becomes a float. When the float cannot hold the number exactly, the merge reports a warning.
- In all other cases, the merge fails with an error. For example, `4294967296` that replaces an integer is an error, and so is `4000000000` in a new section.

To write a hash, use its signed form: `-1980013182` for `2314954114`. The signed form is an integer, so the engine reads the same hash in all cases. To write a large number as a float, write it with a dot: `4000000000.0`.

A line of a binary file holds at most 255 values. When a patch line has more, the merge reports an error and keeps the first 255 values.

### Directives

Directives start with `ol.`. Do not use `@`: the INI parser ignores lines that start with `@`.

| Directive | Effect |
|---|---|
| `ol.new = true` | Add this section to the file. A section with the same key must not exist. |
| `ol.delete = true` | Remove the target section and its child sections. The patch section has only the key and this directive. |
| `ol.remove = name1, name2` | Remove all target entries with these names. |
| `ol.parent = <key>` | With `ol.new`, add a child section to the parent section with this key (see below). |

An unknown `ol.` directive is an error.

### Child sections

The engine attaches some section types to the section of the parent type before them. OpenLancer treats the run of these sections after a parent as its children:

| Child section | Parent |
|---|---|
| `[CollisionGroup]` | `[Ship]`, `[Solar]` |
| `[LOD]` | equipment |
| `[start_effect]`, `[destroy_group]`, `[destroy_hp_attachment]`, `[start_cam_particles]`, `[ignite_fuse]`, `[impulse]`, `[destroy_root]`, and the retail sections `[tumble]`, `[damage_group]`, `[damage_root]`, `[make_invincible]`, `[dump_cargo]` | `[fuse]` |
| `[MVendor]`, `[MRoom]`, `[GF_NPC]`, `[BaseFaction]` | `[MBase]` |

- `ol.delete` on a parent also removes its child sections.
- Child sections that directly follow a new parent section (`ol.new`) in the patch belong to that parent.
- To add a child section to an existing parent, use `ol.new = true` and `ol.parent = <parent key>`. The section goes after the parent's other child sections. A child with a key must not repeat the key of another child of the same type under that parent.
- A child section with a key (for example `[GF_NPC] nickname = ...`) can be patched like any other section.

### Markets

The engine adds the goods of all `[BaseGood]` sections for one base. So a `[BaseGood]` patch section is always added as a new section. Use it to sell more goods at a base:

```ini
[BaseGood]
base = li01_01_base
marketgood = my_package, 0, -1, 1, 1, 0, 1, 1
```

The values of `marketgood` are: good, rank, reputation, min, max, preserve, price multiplier. The engine sells a ship only when min or max is not 0. Version 1 cannot change or remove a vanilla market line. Use a whole-file override for that.

### Two mods that change the same entry

The mod that loads later wins. The merge reports a warning that names both mods.

### freelancer.ini

In format version 1, `EXE/freelancer.ini` cannot be patched. A mod can replace it only as a whole file in `files/`, and it cannot use text placeholders. OpenLancer adds its text DLL to `[Resources]` of the file that results.

## Text: strings.json

```json
{
  "strings":   { "ship_name": "Starrunner" },
  "infocards": { "ship_info": "<?xml version=\"1.0\" encoding=\"UTF-16\"?><RDL><PUSH/><TEXT>...</TEXT><PARA/><POP/></RDL>" }
}
```

In a patch, or in an INI file in `files/`, write `$str:ship_name` for a string id (`ids_name`) and `$ifc:ship_info` for an infocard id (`ids_info`). OpenLancer puts the text of all mods into one generated DLL, `EXE/openlancer_strings.dll`, and adds it to `[Resources]` in `freelancer.ini`. The ids come from the load order and the keys. Refer to mod text only through placeholders, never through the numbers.

These keys hold a text id. The retail data confirms the kind of text of each key:

| Key | Placeholder |
|---|---|
| `ids_name`, `ids_short_name`, `strid_name`, `tradelane_space_name` | `$str:` (a string) |
| `ids_info`, `ids_info1`, `ids_info2`, `ids_info3` | `$ifc:` (an infocard) |
| `ids_info` of a `[KeyCmd]` | `$str:` (a string) |

Freelancer also shows a string id in `ids_info` as an infocard with plain text, and some retail solars, objects and commodities use that. Librelancer shows only infocards, so use `$ifc:` for `ids_info`.

## Whole files

A file in `files/` adds a new file, or replaces the file at the same install path. Patches of later mods apply to the replaced file. A replacement discards the changes of the mods that load before it, and the merge reports a warning that names them. Put new assets under `DATA/MODS/<mod id>/` so that they do not collide with other mods.

## Rules

- INI content in a mod must be ASCII. Text in `strings.json` can use any characters.
- Do not copy vanilla files or vanilla sections into a mod. Change only the entries that you need.
- `tools/content_scan.py` rejects retail file types and files that are byte-identical to retail files. It cannot find decoded INI text or copied sections, so follow the rule above yourself.

## Profiles

A profile is a JSON file:

```json
{ "base": "../base", "mods": [ "../mods/my-mod", "../mods/other-mod" ] }
```

Paths are relative to the profile file. `base` defaults to `../base`.

## The editor

The editor saves each edit as a patch in the active mod. The patch holds only the difference from the data that the mods before the active mod give. The editor never writes the base data or another mod.

### The active mod

The Mod menu selects the active mod of the profile. The editor stores the choice for each profile. Without a choice, the last mod of the profile is the active mod. "None (read-only)" shows the whole profile and takes no edits. "New mod" makes the folder `<id>` with a `mod.json` in the `mods` folder next to the `profiles` folder, adds it to the end of the profile, and makes it the active mod.

The editor shows the edit view: the base data with the mods up to the active mod. The game also applies the mods that load after the active mod. When the profile has such mods, the Console and the Mod menu name them, and a warning glyph marks each value that one of them changes. The game uses the value of the last mod.

The files of a mod in the OpenLancer repo, for example `samples/mods/ol-sample`, are tracked files. For such a mod, the editor takes no edit, undo, redo or save until you allow the edits one time in each session: in the Inspector or in the Mod menu. A restore of the unsaved edits of an earlier session also allows them, and the restore dialog tells this.

### Edits in the Inspector

The Inspector edits ships, equipment, goods, the other sections of the item files, and the market lines of the active mod. A field shows each line of a key on its own text line, with the values separated by `, `. The editor reads the text as the merge reads a patch line, so a value gets the type that a save and a reload give it (see "Value types in binary files"). For example, in a binary file, `true` that replaces the text `TRUE` stays `TRUE`, and `200` that replaces a float is a float. An empty field removes the key.

The editor checks the text against the type that the engine reads: a number, a whole number, `true` or `false`, a name of a list, or the number of values of a vector or a color. The editor refuses text that a patch line cannot hold, for example a value with `;` or with characters outside ASCII.

Each field edit is one entry of the edit history (Edit > Undo, `Ctrl+Z`, and Redo, `Ctrl+Y` or `Ctrl+Shift+Z`). Each of the X, Y and Z fields of a vector is a field of its own. A field that ends with the text it started with is no edit. So it keeps the line of the patch. Escape ends a field and cancels its edit.

Each change of the text shows at once. While a field is active, it stays in its place, also when its text adds or removes the key. After the field ends, the key moves to its list, for example from "Unset keys" to "Keys".

A blue label marks a value that the active mod sets. The tooltip of the label names the layer that sets the value: the base data, a mod that loads before the active mod, or the active mod. A lock tells why a row cannot change, for example the key of a section, or a section that format version 1 cannot patch.

Revert, in the menu of a row or in the item menu, puts back the lines of the lower layer: the base data with the mods that load before the active mod. The next save then writes no line for them. A section that the active mod adds has no lower lines, so it has no Revert. A key of a group of lines that belong together (see "Lines that belong together") reverts only with its whole section.

"Sold at" lists the market lines that sell a good. The lines of a lower layer are read-only, because format version 1 cannot change them. "Add to base" adds a line to a `[BaseGood]` of that base that the active mod adds. Without one, a new `[BaseGood]` goes into the market file in which the base sells goods of the same category, else into the first market file of the base. The editor refuses a base that already sells the good in a market file.

### After a save

`Ctrl+S` saves the edits of the active mod. The Console lists the files that the save wrote and deleted, with the number of the save, or the problems that stopped it. After a save that changed files, the game data loads again in the background, and `oltool validate --active <mod> --json` checks the save in its own process. One validation runs at a time. The oltool process reads the files of the mod, so a save of new edits stops the validation of the save before it, before the save moves files. When that save then changes no file, the stopped validation starts again. The Console labels each result with the number of its save.

When a file does not move into place, for example because a program uses it, the save is not complete. The game data then does not load again, and no validation runs. The next save completes the files first. Then the game data loads again, and the validation runs.

### What stays in a patch file

- The lines above the comment block of the first section, the comment block directly above each section, and each line that no edit touched stay with their text and their bytes, comments included.
- A line of the patch stays also when its value equals the value of a lower mod. So the mod keeps its value when the lower mod changes. Only an edit of that name can remove the line.
- A line whose value an edit changes gets new text, without its inline comment.
- A patch file whose content the edits do not change keeps its bytes.
- When a patch has no sections after the edits, the editor deletes the file and its empty folders below `patches/`.

The editor writes an integer as a decimal number. In a patch for a binary file, it writes a hash in its signed form, for example `-1980013182`. A float always has a `.` or an exponent, for example `100.0`, so that the merge reads it as a float. A boolean is `true` or `false`.

### Sections in which the line order counts

The engine reads the lines of some sections in order, for example a mission `[Trigger]` or `[ObjList]`. When the editor changes such a section, its patch holds all lines of the section. The merge puts these lines at the place of the first line that they replace, and the `nickname` line stays in its place. So when a line comes before the `nickname` line, a patch from the editor cannot keep the order. The editor shows such a section, but it does not change it.

When a hand-written patch of the mod changes such a section, the editor writes that section again at each save of the file. So the editor cannot save an edit of that file, and the save reports the section. Change that file by hand.

### How a save writes the files

A save writes all changed files of the mod, or none of them:

1. The editor makes each patch and checks that the merge gives the edited data from it. Any problem stops the save before it writes a file.
2. The editor writes the new files to the `.olsave/` folder of the mod. Then it writes the journal, `.olsave/journal`: each move and delete, the hash of each file before the save, and the hash of its new content.
3. The editor moves each new file into place, deletes the files to delete, and then deletes the journal.

When the editor stops after step 2, it completes the moves when it opens the profile again. Do not delete `.olsave/` while the journal is in it. Open the profile in the editor once, so that the editor completes the save. Until then, `oltool build`, `validate` and `deploy` show a warning for the mod.

When the editor completes a save, it changes a file only when the file still has its content from before the save. A file that changed after the editor stopped, for example a file that a backup put back, stays as it is, and the editor reports it. When the editor cannot complete a save, for example because a file is in use, the profile opens and the editor tells why. If the save is of the active mod or of a mod that loads before it, the editor then shows the data but does not take edits.

A save also stops in these cases:

- A file of the active mod changed outside the editor after the editor read it. The save replaces such a file only when you confirm it. Cancel is the default button of that dialog.
- The base data, a mod that loads before the active mod, or the mod list of the profile changed after the profile opened. This includes a change of the load order that `dependsOn` and `loadAfter` give, and a change of the text ids of those mods. The edits then apply to old data. Open the profile again.
- A patch or a whole INI file of the mod uses a text placeholder that the mod's `strings.json` does not have after the save. When the save changes `strings.json`, it checks each patch and whole INI file of the mod, also the files that it does not write.
- A patch line of a key that holds a text id holds the id of a text of a mod as a number. The id comes from the position of the text in the generated DLL, so it changes when a mod adds text. Copy this text into the mod and use its placeholder.
- A file that the save writes is a folder now, a folder of its path is a file, or the file is read-only.

While the editor has unsaved edits, it keeps them in `.olsave/recovery.json`: a moment after each edit, and when the window closes. When the window closes, an active field ends first, so that the file keeps its edit. When no unsaved edits remain, for example after an undo, the editor deletes the file. A close of the profile, a change of the active mod and File > Exit ask first: Save, Discard or Cancel. After a save, the action continues, also when the save needed your confirmation to overwrite a file. The close button of the window cannot ask, so the next open of the mod offers to restore the edits. The editor restores them only when the files of the mod did not change after it wrote the recovery file. Until you restore or discard them, the editor does not take new edits for the mod. So a second recovery file is never necessary.

### strings.json and profile files

The editor writes `strings.json` with sorted keys and an indent of two spaces. It keeps the other fields of the file, its byte order mark and its line ends. A new file has no byte order mark. The editor does not write a `strings.json` that has comments, because the reader skips comments and a new file cannot keep them.

The editor adds a new mod to the end of the `mods` list of a profile with the same rules. It keeps the fields that it does not know, and it does not write a profile file that has comments. It adds the mod only when the profile loads with it: the mods in its `dependsOn` list must be in the profile, and the load order must have no cycle.

### New items, renames and text

New from template copies a ship or equipment with the sections that belong to it:

- A ship: the `[Ship]` with its child sections, the hull good that the engine uses (the last one in load order), and the packages of that hull.
- Equipment: its section with its child sections, its good, and the munition that `projectile_archetype` names, with the good of the munition.

Each copy goes into the file of the section that it copies, because a patch cannot change the file list of `freelancer.ini`. The ship or the equipment and its good get the new nickname. The hull good gets `<new>_hull`, a package gets `<new>_package`, and a munition and its good get `<new>_ammo`. A line of a copy that names another copy names the new copy: `projectile_archetype`, `equipment`, `ship` and `hull`. In a binary file, a copy writes `true` and `false` in lowercase, because the merge stores a new boolean that way.

A copy keeps a text id of the base data. A text of a mod gets a copy in `strings.json` of the active mod, with a key from the nickname of the copy, and the line of the copy gets its placeholder:

- A placeholder of the active mod: so a text edit of the copy does not change the template.
- The id of a text of a lower mod: the merge gives that id from the position of the text in the generated DLL, so the id changes when a mod adds text.

Copies of sections that share a text share the copy of the text.

The editor has no menu item for Rename yet. Only the data API (`ItemEdits.Rename` in OpenLancer.Data) renames an item. Rename is only for an item that the mod adds. Equipment and its good keep the same nickname, because the engine finds the equipment of a good by that nickname. The editor changes each line that names the item in a section that the mod can change: the `ship`, `hull`, `equipment` and `addon` lines of goods, `projectile_archetype`, and `marketgood`. The new nickname must be a value that a patch line can hold, for example without `,` or `;`.

When you change the text of a key that holds a text id (see "Text: strings.json"), the editor writes the text into `strings.json` and sets the line to its placeholder. The text of no other section changes:

- A line with a placeholder of the right kind keeps its key, when no other section uses that key.
- Otherwise the editor makes a new key: the nickname in lowercase for `ids_name` and `strid_name`, and the nickname with a suffix for the other keys, for example `li_gun01.info` for `ids_info`. When another section uses that key, or `strings.json` has an unused text with it, the key gets the section type, for example `li_gun01.good`, and then a number, for example `li_gun01.good_2`.

A retail gun and its good have the same `ids_name`, but the Inspector shows the gun and a shop shows the good. So the editor offers to set the same placeholder on each section of the item that has the same id. The sections share one text only when you accept the offer. An infocard from plain text gets one paragraph for each line.

A new item, a rename and a text edit read the data when you start them. When the data changes before the edit runs, the edit does not run, because it would undo the change.

A boolean line without a value, for example `separable =` of a `[CollisionGroup]`, is true. The engine reads the value of such a line when it has one, so the editor writes `separable = false` to make the flag false.

## Tools

| Command | Effect |
|---|---|
| `oltool build <profile.json>` | Merge the mods. Show the changed files and the merge problems. |
| `oltool validate <profile.json> [--active <mod id>] [--json]` | Merge, then report the data problems that the mods add to vanilla. With `--active`, report only the problems that this mod adds to the mods that load before it, without the later mods. With `--json`, write only a JSON list of `{"severity", "source", "message"}` to stdout. |
| `oltool deploy <profile.json>` | Validate, then write the merged data to `<profile name>/` next to the profile, and write `<profile name>.lancer.ini`. |
| `oltool ini <profile.json\|folder> <install path>` | Print a merged INI file as text. Binary INI files are decoded. |
| `oltool dump <profile.json\|folder> <out.json>` | Write a sorted JSON dump of the data that the engine resolves. |
| `oltool thn <profile.json\|folder> <install path>` | Print a cutscene script (`.thn`) as readable Lua text. |
| `oltool verify-base <folder>` | Check that a folder is the unchanged, read-only retail data. |

Exit codes: 0 = OK, 1 = the data has problems, 2 = the command could not run.

Start the game on a deployed profile: `lancer.exe <profiles folder>\<profile name>.lancer.ini`.

The deploy folder is managed by `oltool`. A deploy deletes files in it that are not in the merged view. Do not keep your own files there. To protect other data, the deploy stops before it writes anything when:

- the folder exists, is not empty, and has no `.openlancer-deploy` marker from an earlier deploy;
- the folder overlaps the base data, a mod folder, or the profile file;
- the folder or one of its parents is a link, or the folder contains a link;
- the path contains `,` or `;`, which lancer.exe cannot read;
- a file in the folder is read-only (it can be a link to the base data).

When a deploy stops part of the way, the marker records it. Run the deploy again to complete the folder.

When the deploy folder path has characters outside ASCII, lancer.exe reads the path correctly only until the game saves its settings, because the game then writes the config file in a form that loses those characters. After that, run `oltool deploy` again, or move the workspace to a path with only ASCII characters.

## What the validator checks

Some broken references stop the engine load. Many others make the engine drop data and log nothing. The validator reports both. It compares the merged data with vanilla, so it reports only the problems that the mods add.

First, the INI checks:

- A ship package whose hull good does not exist or is not a ship hull (the engine load stops), or whose addon is not equipment (the engine drops it).
- A hull good without a ship (the load stops), or with a ship that is not in shiparch (the engine drops its packages).
- A ship package without an engine or a power plant.
- A market for a base that does not exist, a market good that does not exist, or a market good that the engine cannot sell (a ship hull, or a good without equipment).
- A market ship whose letter case differs from the ship package. The engine finds ship packages with a case-sensitive match.
- A ship that a market lists with min = max = 0 (warning: it is not for sale).
- A duplicate nickname, two nicknames with the same hash, a nickname that is not ASCII, or an equipment section without a nickname. (The engine drops a `[Good]` or `[Ship]` without a nickname and logs `Missing required field nickname`.)
- A ship or good whose text id has no text.

When the INI checks find errors, the validator stops there. It then also reports the new lines of the engine's INI parser, because a parse error can cause a reference error: for example, an `addon` line with too few values is dropped, and the package then has no engine. When the INI checks find no errors, the validator loads the data as the game does, and reports:

- A load that throws, and ship packages that the engine did not resolve.
- Every new warning or error line of the engine, for example `Unknown entry` for a misspelled key. An engine error line is an error.
- For every ship package: an addon hardpoint that the ship model does not have, a model that does not load, and a gun or shield that does not fit its hardpoint by the server's rule, also on `internal` (the server rejects the purchase).
