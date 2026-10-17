# OpenLancer mod format (version 1)

An OpenLancer mod changes the game data without a change to the base files. A profile lists the mods. OpenLancer merges them over the extracted vanilla data in load order. The result is a merged view of the data. The base folder never changes.

## Mod folder

```
<mod>/
  mod.json                     required
  strings.json                 optional: text for names and infocards
  patches/<install path>.ini   entry-level changes to an INI file
  patches/<install path>.thn   changes to a cutscene script (see "Cutscenes")
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

A patch of an INI file is a text INI file. Each section in it changes one section of the target file. For a patch of a cutscene script, see "Cutscenes".

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

## Cutscenes

A cutscene script (`.thn`) is a Lua 3.2 script. The retail scripts are bytecode. The engine also reads Lua text, so a mod gives its cutscene changes as text. A mod changes a cutscene in one of two ways:

- `patches/<install path>.thn` is a THN patch. It holds only the changed values. Use it to change a retail cutscene or a cutscene of another mod.
- `files/<install path>` is a whole script as Lua text. Use it for a new cutscene, under `DATA/MODS/<mod id>/CUTSCENES/`. Do not copy a retail script into a mod.

New cutscene in the editor writes `files/DATA/MODS/<mod id>/CUTSCENES/<name>.thn`. An INI line names a cutscene with its path below `DATA`, for example `start_script = MODS\my-mod\CUTSCENES\bar_shot.thn`. The engine finds the file through the data folder of the merged files. A new cutscene plays in OpenLancer. It plays in the retail game only after the M11 check that the retail engine reads text cutscenes, so the editor marks it "OpenLancer only".

The merge reads the script as the mods before the patch leave it, and applies each THN patch in load order. Then it writes the result as canonical Lua text. `oltool thn` prints that text, and `oltool deploy` writes it into the deploy folder. The cutscene editor shows each cutscene, and it saves the edits of a lower cutscene as a THN patch (see [CUTSCENES.md](CUTSCENES.md)).

### Values

| Lua text | Value |
|---|---|
| `20`, `-1.5`, `1E-07` | A number. The engine stores it as a 32-bit float. |
| `"Cam_02"` | A text. Write `\"`, `\\` and `\n` for a quote, a backslash and a line end. |
| `Y`, `N` | True and false. |
| `CAMERA`, `SET_CAMERA`, `LIT_DYNAMIC` | A name of the THN environment. |
| `{ 1, 2, 3 }`, `{ fovh = 20 }` | A table: a list part, a record part, or both, for example `{ 1, 2; key = 3 }`. |

- THN content is ASCII. The merge refuses a patch with other characters.
- `-0` becomes `0`. A number that is not finite is an error, for example `1e39`, which the engine reads as infinity. NaN has no text.
- A name stays a name, and a number stays a number. The engine reads `type = 3` and `type = CAMERA` as the same type.

### The THN patch

```lua
-- My mod: a wider Trent shot, and a later cut to the medics.
ol_thn_patch = 1
entities = {
	{ ol_match = "Cam_02_Intro_Trent_A", ol_set = { ["cameraprops.fovh"] = 20 } }
}
events = {
	{ ol_match = { 25.75, SET_CAMERA, { "Cam_Monitor_s002xe", "Cam_03_ACOH_Trent_Medics_A" } }, ol_set = { time = 27 } }
}
```

This patch is `patches/DATA/SCRIPTS/STORY/s002xe_nrml_sinclair_li_01_Cityscape_01.thn`. It makes the first Trent shot of the Manhattan intro wider, and the cut to the medics 1.25 s later.

| Global | Meaning |
|---|---|
| `ol_thn_patch = 1` | Required. The version of the THN patch format. It is the first statement. Only comments (`--`) and empty lines can come before it. |
| `duration = <seconds>` | Optional. The new length of the script. |
| `entities = { ... }` | The entity entries, in order. |
| `events = { ... }` | The event entries, in order. |

| Entry | Meaning |
|---|---|
| `{ ol_match = <key>, ol_set = { [path] = value, ... }, ol_remove = { path, ... } }` | Changes one element. `ol_set` and `ol_remove` are optional. The removes run before the sets, so an entry can move a key to the end of its record. |
| `{ ol_match = <key>, ol_delete = Y }` | Deletes one element. |
| `{ ol_new = <table> }` | Adds an element at the end of its list. For an event, the table is `{ time, TYPE, { targets }, { props } }`. |
| `ol_nth = n, ol_count = m` | Use them in an entry whose key repeats in the script. The element is the n-th of the m elements with that key. Without them, the key must be unique. |

A key finds an element:

- An entity key is the entity name, for example `ol_match = "Cam_02"`. Letter case does not matter, because the engine compares names without it. An entity without a name has the key `""`.
- An event key is `{ time, type, { targets } }`, as the script has them. The time must be the same number. The type compares by its number, so `SET_CAMERA` and its number are the same type. The targets compare by their text and their order.

A path names a value of the element:

- A path is a text of record keys joined with `.`, for example `"cameraprops.fovh"` or `"spatialprops.pos"`.
- An event has four slots: `time`, `type`, `targets` and `props`, for example `"time"` or `"props.duration"`. Only `props` holds keys. A patch cannot remove `time`, `type` or `targets`.
- A path ends at a value. A list, for example `pos = { 1, 2, 3 }`, is one value. An empty table is a record, so a path can go into an empty `psysprops`.
- A set of a new key adds the key at the end of its record. A set also makes each missing record on its path. For example, `"props.duration"` makes the props of a `SET_CAMERA` event, which has none.

### How the merge applies a THN patch

1. All keys find their elements in the script before the patch. Then the removes, the sets and the deletes run. Then the new elements go at the end of their lists, in patch order. So a delete does not move a later match.
2. A key that finds no element is an error. So is a key whose number of elements differs from `ol_count`. The error names the file, the entry and the key. Any error stops the whole patch, and the script stays as the mods before it leave it.
3. A set into a value that is not a record is an error. A remove of a missing key is a warning.
4. When two mods change the same path, or one path is in the value of the other, the later mod wins. This is also true when two mods set `duration`. The merge reports a warning that names both mods.
5. A rename sets `entity_name`. The next patch in load order then uses the new name.
6. An unknown `ol_` key is an error. A key or a global that the format does not have is an error too.
7. A lower mod can delete or add an element with a key, or change the key of an element. A later entry with `ol_nth` for that key can then name another element, also when the count stays the same. The merge reports a warning that names both mods and the key, and the patch applies. Check the result.

An edit can give an event the time, the type and the targets of another event, for example a move of an event onto the start of the next motion of the same character. The key of these events then repeats. A patch of a later mod that names one of them without `ol_nth` then fails with rule 2, and that mod must save its patch again in the editor. Duplicate in the editor puts its copies after their sources, so a copy does not repeat the key of its source.

The engine runs the events of one time in their order in the script. A new event goes at the end of the list, so it runs after the other events of its time. To run an event before them, delete those events and add them again as new events after it. Do not do this when one of those events has a key that repeats, because the `ol_nth` of a later mod can then name another event.

A whole file of a later mod replaces the script and discards the patches before it, as for INI files. A tool that cannot run the engine VM cannot merge a THN patch. It reports "This tool cannot merge cutscene patches." THN patches are part of format version 1. An older OpenLancer refuses a mod with a THN patch: "A patch must be an .ini file."

### The validation of cutscenes

`oltool validate` checks each cutscene script that the mods add or change: each whole file and each merged patch. It reports the errors that the merged script has and that the lower data does not have. An error is a value that stops the engine, so the cutscene does not play, or a text that breaks the rules of this format:

- The script does not compile, or it holds a number that is not finite.
- A script text has a character outside ASCII, also in a comment. The engine runs the script, but its VM reads each text as ASCII.
- `duration` is not a number, or `entities` or `events` is not a table.
- An entity has no text `entity_name`, or a `type` that the engine cannot read.
- An event has no number time, no type with an event class of the engine, or no table of text targets. Or the props of an event are not a table.
- A key holds a value of another kind than the engine reads. An entity reads its keys with casts: `cameraprops.fovh = "20"` stops the engine, because the engine reads a number there. An event reads its props with conversions, which also take a text with a number or a THN name, for example `time_scale = "2"`.
- An entity with `lightprops` has no light type or no `spatialprops.pos`. A `path_data` text is not a path that the engine reads.
- An event has fewer targets than its type reads: two for SET_CAMERA, ATTACH_ENTITY and START_PATH_ANIMATION, and one for most other types. The second target of SET_CAMERA names an entity of the script that is not a CAMERA. The second target of START_PATH_ANIMATION names an entity of the script without `pathprops.path_data`.
- The scene cannot build an entity:
  - a LIGHT without `lightprops`, or a SCENE without an `ambient` color.
  - a COMPOUND with a template, but without a known `userprops.category`, or with a solar, a ship, a room or a cart that does not exist.
  - a DEFORMABLE without `userprops.Actor`, with a costume that does not exist.

The scene does not make a REFERENCE entity, or an entity that a later entity with the same name hides. So the checks of the scene skip these entities, and a target with the name of such an entity names an object of another script of the scene. `flags` gives REFERENCE also as a name of another group, because the engine casts the name to its number: `flags = CAMERA` is 3, which is REFERENCE and LIT_DYNAMIC. A room fills in the `$terrain_*` templates with the terrain of its base, so the check reads the template of each base that plays the script. The game makes no COMPOUND of a script that only `[Trigger] Act_CallThorn` lines name, because it plays such a script in space with the objects of the mission. So the checks of a COMPOUND skip such a script.

The check does not know the objects of the other scripts of a room. So a target that names no entity of the script is not an error, also for SET_CAMERA and START_PATH_ANIMATION. The engine stops when no script of the scene has that name.

A THN patch that does not apply is a merge error. Each problem of a cutscene has its place: the cutscene, the element with its key and the path of the value. A click on the problem in the Console of the editor opens the cutscene and selects the element. When an edit after the save moved the element, the key finds it. When the key names no element or more than one element, the click selects the cutscene.

## Rules

- INI content and THN content in a mod must be ASCII. Text in `strings.json` can use any characters.
- Do not copy vanilla files or vanilla sections into a mod. Change only the entries that you need.
- `tools/content_scan.py` rejects retail file types and files that are byte-identical to retail files. It cannot find decoded INI text or copied sections, so follow the rule above yourself.
- The scan accepts a `.thn` file only as a THN patch: ASCII text below the `patches/` folder of a mod (the `patches/` folder next to its `mod.json`) whose first statement after its comments is `ol_thn_patch = 1`. A `.thn` file below `files/` fails the scan, also in a folder with the name `patches`.

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

The Inspector edits ships, equipment, goods, the other sections of the item files, the market lines, and the objects, zones and lights of the open star system and the sections that set the fields of a system. The edits go into the active mod. A field shows each line of a key on its own text line, with the values separated by `, `. The editor reads the text as the merge reads a patch line, so a value gets the type that a save and a reload give it (see "Value types in binary files"). For example, in a binary file, `true` that replaces the text `TRUE` stays `TRUE`, and `200` that replaces a float is a float. An empty field removes the key.

The editor checks the text against the type that the engine reads: a number, a whole number, `true` or `false`, a name of a list, or the number of values of a vector or a color. The editor refuses text that a patch line cannot hold, for example a value with `;` or with characters outside ASCII.

Each field edit is one entry of the edit history (Edit > Undo, `Ctrl+Z`, and Redo, `Ctrl+Y` or `Ctrl+Shift+Z`). Each of the X, Y and Z fields of a vector is a field of its own. A field that ends with the text it started with is no edit. So it keeps the line of the patch. Escape ends a field and cancels its edit.

A vector row and the size row of a zone have a label in front of each value: X, Y and Z, or Radius, Height, Outer and Inner. Drag a label to the left or to the right to change its value. Each point of mouse motion changes the value by one step. `Shift` gives steps of 0.1 times the step, and `Ctrl` gives steps of 10 times the step. When you hold both keys, `Shift` wins. A key that you press during a drag changes the speed from that point on, and the value does not jump. The tooltip of a label gives the steps of its value.

| Value | Keys | Step for each point | `Shift` | `Ctrl` |
|---|---|---|---|---|
| Position | `pos` | 10 | 1 | 100 |
| Rotation | `rotate` | 0.5 degree | 0.05 degree | 5 degrees |
| Size of a zone | `size` of a `[Zone]` | 10 | 1 | 100 |
| Coefficient | `attenuation`, `emit_attenuation` | From the dragged value. 0.0000056 gives 0.00000001. A value of 0 takes the step of its line, as in the next row. | step x 0.1 | step x 10 |
| Other vector | for example `spin`, `direction`, `rotation_inertia` | From the largest value of the line. 2800 gives 10, 1 gives 0.01, and 0.002 gives 0.00001. A line of zeros gives 0.01. | step x 0.1 | step x 10 |

A step that comes from a value is its power of 10 divided by 100: 10^(e-2) for a value from 10^e to 10^(e+1). The step of the other vectors goes from 0.000001 to 10,000. The step of a coefficient goes from 0.000000001 to 10,000. The step stays the same from the start of a drag to its end. So after a drag from 950 to 1050, the next drag has a step 10 times larger. In the Scene, `Ctrl` reverses the snap. In the Inspector, `Ctrl` gives large steps.

One drag is one entry of the edit history. The Scene shows each change at once. `Esc` or the right mouse button cancels the drag, and the value goes back. A drag that ends where it started, or that comes back to its start, is no edit. The drag goes on when the mouse leaves the editor window, up to the edge of the screen. To go farther, release the button and drag again. A label does not drag when the row is read-only, or when a value of the line is not a number. Then its tooltip tells why. Correct the value in the field first. A single number, for example `edge_fraction`, has no label drag.

A color row with one line has a swatch in front of its text field. Click the swatch to open the color picker. The picker keeps the form of the key:

- Whole numbers from 0 to 255 for most color keys, for example `color` of a `[LightSource]`, `space_color`, `burn_color` and `property_fog_color`. The inputs of the picker show 0 to 255.
- Numbers from 0 to 1 with 3 decimals for a `FloatColor` key: `inner_color` and `outer_color` of a `[star_glow]` in `stararch.ini`. The inputs of the picker show 0 to 1. No Inspector page shows that file yet.

The picker changes the first three values of the line. A fourth value, for example the alpha `128` of `color = 120, 250, 255, 128`, stays. The text field can change it. The Scene shows each change at once, for example the light of the system. A click outside the picker keeps the color. One opening of the picker is one entry of the edit history. `Esc` puts back the color of the opening, also while an input of the picker is active. **Original** in the picker shows the color of the opening, and a click on it puts back that color.

The swatch is disabled, and its tooltip tells why, when the line has fewer than three values, when one of the first three values is not a number, or when one of them is outside the range of the form, for example `color = 300, 0, 0`. Correct such a line in the text field. A key without a line, or with two lines or more, has no swatch. Type the first value in the text field. A `Vector3` color factor, for example `color_shift`, has the label drag and no swatch.

Each change of the text shows at once. While a field is active, it stays in its place, also when its text adds or removes the key. After the field ends, the key moves to its list, for example from "Unset keys" to "Keys". Two kinds of field change the data only when the field ends: the text of a key that holds a text id, for example a name, and a market line.

A blue label marks a value that the active mod sets. The tooltip of the label names the layer that sets the value: the base data, a mod that loads before the active mod, or the active mod. A lock tells why a row cannot change, for example the key of a section, or a section that format version 1 cannot patch.

Revert, in the menu of a row or in the item menu, puts back the lines of the lower layer: the base data with the mods that load before the active mod. The next save then writes no line for them. A section that the active mod adds has no lower lines, so it has no Revert. A key of a group of lines that belong together (see "Lines that belong together") reverts only with its whole section. When the active mod sets the `file` of a `[system]` of `universe.ini`, the Revert of the whole item is not available, because the revert would move the contents of the system to another file.

"Sold at" lists the market lines that sell a good. The lines of a lower layer are read-only, because format version 1 cannot change them. "Add to base" adds a line to a `[BaseGood]` of that base that the active mod adds. Without one, a new `[BaseGood]` goes into the market file in which the base sells goods of the same category, else into the first market file of the base. The editor refuses a base that already sells the good in a market file.

### Edits in the Scene

The Scene edits the objects, zones and lights of the open star system. Click an item in the Scene or in the Hierarchy to select it. The Scene, the Hierarchy and the Inspector then show the same item. You can select one item at a time. The gizmo on the selected item has three tools, and each tool changes one key of the section of the item:

| Tool | Key | Items |
|---|---|---|
| Move (`W`) | `pos` | objects, zones and lights |
| Rotate (`E`) | `rotate` | objects and zones |
| Scale (`R`) | `size` | zones |

- The engine reads no rotation and no size for a light, and no size for an object. The tooltip of a tool button tells why the tool is not available.
- `X` changes between Local and World: a move or a turn along the axes of the item, or along the world axes. A scale always uses the axes of the item. A turn is about one axis.
- The scale handles are the values of `size` that the shape uses. A sphere has its radius, and a cylinder has its radius and its height. An ellipsoid, a box and a ring have three values. A scale changes only these values. When the zone has no `size` line, a scale writes all the values of the shape.
- Scale is not available for an ellipsoid, a box or a ring with two values in `size`. The engine then gives one axis the size 0. Give the line three values first. Scale is also not available when a value that the shape uses is 0 or less.
- A key does what its toolbar button does. So `W`, `E` and `R` select only a tool that the item takes, and `X` does nothing for the scale tool. The keys work while the mouse is over the Scene or the Scene has the focus, and no text field is active. They do not work during a drag, or with `Ctrl` or `Alt`. While the right mouse button is down, `W`, `A`, `S`, `D`, `Q` and `E` fly the camera.

The Snap button of the Scene toolbar (a magnet) makes a drag move by steps. The default steps are 100 for a move, 15 degrees for a turn and 100 for a size. The Snap steps menu next to it changes the steps. `Ctrl` during a drag snaps when the snap is off, and does not snap when it is on. A move snaps along the axes of its space, so a snapped Local move of a turned item can give fractions in `pos`. A snapped size is never below 1.

One drag is one entry of the edit history. The item moves at once, and the Inspector shows the new line. The drag ends when the left mouse button goes up, also over another panel. `Esc` or the right mouse button cancels the drag, and the item goes back. Undo, redo, save and the recovery file wait until the drag ends. A drag that ends where it started is no edit.

A move or a turn changes only the selected item. The zones, the trade lane paths and the other objects that belong to the item stay in place, for example the docking ring of a planet. The editor cannot add or delete an item of a system, or change its nickname, yet.

The numbers that a drag writes:

- The drag writes only its key. The other lines of the section stay as they are, and so does each value of the line that the drag does not change.
- A changed value is an integer when it is a whole number from -16777216 to 16777216 and it replaces an integer or no value. Each other changed value is a float, also a whole number that replaces a float, for example `200.0`. `-0` becomes `0`.
- A changed angle of `rotate` rounds to 0.001 degree. Each rotation has two sets of angles. The drag takes the set that is nearest to the angles before the drag, from -180 to 180 degrees.
- A label drag in the Inspector and the color picker follow the same rules (see "Edits in the Inspector"). A label drag adds its steps to the exact text of the value at its start, so a drag of `0.3` by one step of 0.1 writes `0.4`. A changed angle wraps into the range from -180 to 180 degrees, then rounds to 0.001 degree. So a drag of `170` by 20 degrees writes `-170`. A changed size of a zone is 1 or more, as for a scale in the Scene.
- The picker rounds only the channels that it changes: to a whole number from 0 to 255, or to 3 decimals from 0 to 1. A channel whose rounded value does not change keeps its value, also a value that is not on that grid, for example `0.784314` or `120.5`.

For example, a snapped move of `Li01_14_depot_water` by 1,000 along the world X axis writes `pos = -37675, 0, -27705`. The save then writes `patches/DATA/Universe/Systems/Li01/Li01.ini` with the `nickname` line and the `pos` line of the depot.

The Scene shows each edit of an item or of a section of the system in the next frame. This includes a drag, an edit in the Inspector, an undo, a redo and a restore of the unsaved edits of an earlier session. A key that loads a model, for example `archetype` or `loadout`, shows after the field or the drag ends. While the game data loads again after a save, it shows after the load. A new name shows in the label, the Hierarchy, the window title and the system list before the save. The unsaved edits stay in the Scene when you open another system and then open the system again. When the Scene cannot show the state of a section, it keeps the last valid state of the item. The Inspector and the Console tell why. For example, the game drops an object whose `archetype` does not exist, and the editor keeps its last model. Such an archetype is an error that stops the save (see "The save gate"). An archetype that the active mod adds and did not save is in the list, so it is no error. The Scene shows it after the save and the game data reload.

In these cases a section is read-only. The gizmo does not show, and the lock of each row tells why.

- Two sections of the same type in the file have the same nickname, for example the eight `[LightSource]` sections `light_lair_core_lower` of St02c. A patch cannot find one of them.
- The section has no key, for example `[Archetype]` of a system file.
- The active mod replaces the system file as a whole file.
- The editor takes no edits for the mod, for example when another editor has the mod open.

These keys are locked, and the other keys of the section take edits:

- The key of the section, for example `nickname`.
- The `encounter`, `faction` and `faction_weight` lines of a `[Zone]` (see "Lines that belong together").
- The `file` of a `[system]` of `universe.ini`, because a change moves all the contents of the system.

The gizmo also does not show when a line that it reads has an error, for example a `pos` line with text. Correct the line in the Inspector first.

### Data lists and pickers

A key that names another section, for example the `archetype`, the `loadout` or the `reputation` of an `[Object]`, gets a picker. The picker lists the sections that the engine reads for the key: the base data, the mods that load before the active mod, and the active mod with its unsaved edits. So a loadout that the active mod adds is in the list before a save. A mod extends a list when it adds sections. Only a set that the engine fixes, for example the type of a docking sphere, comes from the editor.

Click the field to open the picker. Type to filter the rows by name or nickname. A click on a row sets the value at once, and so do the up and down arrow keys. The Scene shows each value. `Enter` sets the selected row and closes the picker. A double-click and a click outside close the picker and keep the value. `Esc` closes it and puts back the value of the opening. One opening is one entry of the edit history. The first row is "None" for a key that the engine does not need. "None" removes the line. The footer tells the file of the hovered entry, the layer that gives it, and the number of lines that name it. A button at the right of the field selects the section of the entry.

A line with more than one listed value, for example `goto = Li02, Li02_to_Li01, gate_tunnel_bretonia`, gets one slot for each value: System, Exit object and Tunnel. The list of the exit object holds the objects of the system of the first value. A key with more than one line, for example `equip` of a `[Loadout]`, keeps its text field. The check marks each bad line with its number, and "Pick..." sets the first bad line.

A value outside its list shows as "Missing (Loadout): li_foo" in red, as Unity shows a missing reference. The row then offers Pick..., None and Revert. The rule data marks the lists that the engine uses to find the value. For these lists, a value of the active mod outside the list is an error. For the other lists, it is a warning: unlisted values are allowed. A value of a lower layer outside a list is a warning, because the active mod did not set it.

The Inspector also edits three sections of each room file that a `[Room]` of a base file names: `[Room_Info]`, `[CharacterPlacement]` and `[PlayerShipPlacement]`. A room file has each of these sections one time at most, so a patch section finds its target by its type. The other sections of a room file stay read-only.

The script keys of these sections use the list of the cutscenes. The list holds each `.thn` file of the edit view and each new cutscene of the active mod, also before its save. A value is a path below `DATA`, for example `Scripts\Bases\Li_01_bar_enter_01.thn`. The list compares the values as paths: without letter case, and with `\` or `/`. In the picker, a new cutscene that is not saved has the tag "new".

| Key | Field | A value of the active mod outside the list |
|---|---|---|
| `[Room_Info] set_script` | Picker without None | Error: the game crashes when the player enters this room. A room of the active mod without the key is an error too. |
| `[Room_Info] scene` | Text field | Error: the game crashes when the player enters this room. |
| `[CharacterPlacement] start_script` | Picker | Error: the game skips this script when the player enters the room. |
| `[PlayerShipPlacement] landing_script` | Picker | Error: the game skips the landing scene. |
| `[PlayerShipPlacement] launching_script` | Picker | Error: the game skips the launch scene. |
| `[Room_Info] goodscart_script` | Picker | Warning: the game does not play cart scripts. |

A `scene` line names its script by its place: value 2 when value 0 is `all`, else value 1. The check reads that value, as the engine does, also when the value does not end with `.thn`. A `scene` line with fewer values gets no check, because the engine skips it.

The editor reads some files only for the lists, for example `solararch.ini` and `loadouts.ini`. Their sections are read-only in the Inspector until a later milestone gives them their pages.

### The raw INI block

Each Inspector page that edits sections ends with "Loaded values (read-only)" and then "Raw INI". The raw block shows `[Type]` and one `name = values` line for each line of the section. Edit the lines, then click outside the block. The edit changes only the names whose lines you changed. One raw edit is one entry of the edit history, and one Undo puts back all its lines. The order of different names stays the file order. A new name goes at the end of the section.

The block refuses an edit and keeps nothing when the text has a problem: a second header, a comment, a line without `=`, a changed nickname line, a line that the type of its key cannot hold, or a key that no row can change. The Console line then holds the problem and your whole text, so you can copy it back. When the section changed while you typed, for example by an undo, the block also refuses the edit. A value outside its list does not stop the edit: the row above then shows the error. The block is read-only when the section is read-only.

### The save gate

Each save runs the save gate first, also a save with nothing to write, and also the save before Play. A save writes nothing while the gate finds an error:

- A value of the active mod outside a list that the engine uses to find it, for example an `archetype` or a `loadout` that no section has.
- A required key without a valid line in a section that the active mod adds or changes, for example an `[Object]` without an `archetype`. The game drops such an object.
- A value of any layer that names an entry that the active mod removes, for example a retail object whose loadout the active mod deletes with `ol.delete`.
- A value of any layer that names an entry that the active mod makes unusable, also without a delete. For example, the active mod removes the `point` lines of a curve, and a retail light names the curve. The game stops loading with such a light.
- A light of the active mod that names a curve that the engine cannot load, or a name of the active mod with the FLHash of another name of its list. The engine load stops.
- A problem of the field checks in a section that the active mod adds or changes, for example a number that the engine cannot read.
- An error of the checks of a cutscene that the active mod changes, when the lower cutscene does not have the error. For example, a LIGHT has no `spatialprops.pos`. A whole cutscene file of the active mod that the engine cannot read is an error too. The game crashes when it plays such a cutscene, so the message starts with "The game crashes when it plays this cutscene." These checks run without the game data, so they do not check templates and costumes.

An entry is unusable when the engine cannot use it. A curve without points is unusable, and so is a name that two curves have. An entry is also unusable when its own checks give an error that makes the game drop it, crash, or stop loading. Some examples:

- A `[Munition]` with a `motor` and no `explosion_arch`. The game stops loading.
- A `[Gun]` without a valid `projectile_archetype`. The game drops the gun.
- An `[Object]` without a valid `archetype`, or with a `dock_with` that names no base. The game drops the object, or crashes when the player docks there.

A line that names an unusable entry can make its own section unusable. The gate follows such lines. For example, a missile without its explosion makes each gun that fires it an error. The game drops such a gun, so each loadout line that names the gun is an error too. When a lower mod or the base data made the entry unusable, the line gives a warning only.

Each error tells what the game does with the data, for example "The game stops loading with this value.", "The game crashes when the player docks here." or "The game drops this object." An error about an unusable entry tells why the entry is not usable, and how to correct the line:

`[LightSource] nickname = System_LIGHT: 'atten_curve = DYNAMIC_DIRECTION': Curve 'DYNAMIC_DIRECTION' is not usable: the [IGraph] 'DYNAMIC_DIRECTION' has no 'point' line. The game stops loading with this value. Fix 'DYNAMIC_DIRECTION', or pick another value.`

The Console lists each error with its section. A click on the line selects the section, and the Inspector shows the error below the row of its key. The Select button of the row selects the unusable entry. An edit with an error stays in the editor and in the recovery file until you correct it or undo it. A delete never stops at a check: the references to the deleted entry show as "Missing", as in Unity, and one Undo heals them. One Undo of the edit that made an entry unusable also heals each line that names the entry.

### Cutscenes in the editor

The Cutscenes tab lists the cutscene scripts, and the Cutscene tab previews one. The Timeline tab and the Inspector show and edit its entities and events. A save writes a THN patch for each changed cutscene (see "Cutscenes" and "What stays in a patch file"). New cutscene in the Cutscenes tab makes a new script in the active mod, and a save writes it as a whole file. See [CUTSCENES.md](CUTSCENES.md).

### After a save

`Ctrl+S` saves the edits of the active mod. The Console lists the files that the save wrote and deleted, with the number of the save, or the problems that stopped it. After a save that changed files, the game data loads again in the background, and `oltool validate --active <mod> --json` checks the save in its own process. One validation runs at a time. The oltool process reads the files of the mod, so a save of new edits stops the validation of the save before it, before the save moves files. When that save then changes no file, the stopped validation starts again. The Console labels each result with the number of its save. A click on a problem of a cutscene opens the cutscene and selects the element of the problem (see "The validation of cutscenes").

When a file does not move into place, for example because a program uses it, the save is not complete. The game data then does not load again, and no validation runs. The next save completes the files first. Then the game data loads again, and the validation runs.

While the game of Play runs, a save whose validation finds no errors also reloads the game. See [PLAY.md](PLAY.md).

### What stays in a patch file

- The lines above the comment block of the first section, the comment block directly above each section, and each line that no edit touched stay with their text and their bytes, comments included.
- A line of the patch stays also when its value equals the value of a lower mod. So the mod keeps its value when the lower mod changes. Only an edit of that name can remove the line.
- A line whose value an edit changes gets new text, without its inline comment.
- A patch file whose content the edits do not change keeps its bytes.
- When a patch has no sections after the edits, the editor deletes the file and its empty folders below `patches/`.

The editor writes an integer as a decimal number. In a patch for a binary file, it writes a hash in its signed form, for example `-1980013182`. A float always has a `.` or an exponent, for example `100.0`, so that the merge reads it as a float. A boolean is `true` or `false`.

The editor writes a THN patch in canonical form (see "Cutscenes"). It keeps the comment block at the top of the file. It drops the other comments, and the Console tells it. A path that the patch sets or removes stays in the patch also when its value equals the value of a lower mod, until an edit or a revert of that path. When the patch sets a whole record, for example `cameraprops`, an edit of a value in it keeps the whole record in the patch, with the new value. A revert of a value in it takes only that value out of the patch: the other values of the record stay in the patch, each with its own path. A THN patch whose content the edits do not change keeps its bytes. When the active mod has a whole file for a cutscene, the editor writes the whole file as canonical Lua text.

### Sections in which the line order counts

The engine reads the lines of some sections in order, for example a mission `[Trigger]` or `[ObjList]`. When the editor changes such a section, its patch holds all lines of the section. The merge puts these lines at the place of the first line that they replace, and the `nickname` line stays in its place. So when a line comes before the `nickname` line, a patch from the editor cannot keep the order. The editor shows such a section, but it does not change it.

When a hand-written patch of the mod changes such a section, the editor writes that section again at each save of the file. So the editor cannot save an edit of that file, and the save reports the section. Change that file by hand.

### How a save writes the files

A save writes all changed files of the mod, or none of them:

1. The editor makes each patch and checks that the merge gives the edited data from it. Any problem stops the save before it writes a file.
2. The editor writes the new files to the `.olsave/` folder of the mod. Then it writes the journal, `.olsave/journal`: each move and delete, the hash of each file before the save, and the hash of its new content.
3. The editor moves each new file into place, deletes the files to delete, and then deletes the journal. Each move replaces its file in one step. oltool and the game of Play read mod files so that a move can replace a file while they read it. When another program writes a mod file in place at that moment, their read of the file fails, so they never read a part of it.

When the editor stops after step 2, it completes the moves when it opens the profile again. Do not delete `.olsave/` while the journal is in it. Open the profile in the editor once, so that the editor completes the save. Until then, `oltool build`, `validate` and `deploy` show a warning for the mod.

When the editor completes a save, it changes a file only when the file still has its content from before the save. A file that changed after the editor stopped, for example a file that a backup put back, stays as it is, and the editor reports it. When the editor cannot complete a save, for example because a file is in use, the profile opens and the editor tells why. If the save is of the active mod or of a mod that loads before it, the editor then shows the data but does not take edits.

A save also stops in these cases:

- A file of the active mod changed outside the editor after the editor read it. The save replaces such a file only when you confirm it. Cancel is the default button of that dialog.
- The base data, a mod that loads before the active mod, or the mod list of the profile changed after the profile opened. This includes a change of the load order that `dependsOn` and `loadAfter` give, and a change of the text ids of those mods. The edits then apply to old data. Open the profile again.
- A patch or a whole INI file of the mod uses a text placeholder that the mod's `strings.json` does not have after the save. When the save changes `strings.json`, it checks each patch and whole INI file of the mod, also the files that it does not write.
- A patch line of a key that holds a text id holds the id of a text of a mod as a number. The id comes from the position of the text in the generated DLL, so it changes when a mod adds text. Copy this text into the mod and use its placeholder.
- A file that the save writes is a folder now, a folder of its path is a file, or the file is read-only.
- The active mod is in the OpenLancer repo, and the save would write a whole cutscene file into it. The content scan cannot tell such a file from a copy of a retail script. A THN patch is allowed.
- The save gate finds an error (see "The save gate"). For example, a light names an attenuation curve (`atten_curve`) that the engine cannot load: no FLOAT graph of `igraph.ini` has the name, more than one graph has it, or the graph has no points. The light can be a light of the active mod, or a light of any layer when the active mod removed the points of the curve. The game stops loading with such a light, so each later load of the profile would fail. The Console names the light, the curve and the file. The gate reads the curves from the edit view, so it needs no loaded game data.

While the editor has unsaved edits, it keeps them in `.olsave/recovery.json`, a moment after each edit. When no unsaved edits remain, for example after an undo, the editor deletes the file. A close of the profile and a change of the active mod ask first: Save, Discard or Cancel. After a save, the action continues, also when the save needed your confirmation to overwrite a file.

A close of the editor window asks too: the close button, `Alt+F4`, the taskbar and File > Exit. An active field or drag ends first. With unsaved edits, the window stays open, and the dialog asks `Save changes to <mod name> before you close?`. The name is the `name` of `mod.json`, or its `id` when it has no `name`. Save is the save of `Ctrl+S`, with its checks. When the save succeeds, the window closes. When the save fails or a check stops it, the window stays open, and the Console tells why. Don't Save closes the window and deletes the recovery file, so the next open does not offer the edits. Cancel and `Esc` keep the window open. Without unsaved edits, the window closes at once. The field that ends at the close asks no question, so a new text goes only to the section of the field, also when other sections of the item show the same text.

A crash or a kill of the editor cannot ask. Then the recovery file keeps the unsaved edits, and the next open of the mod offers to restore them. The editor restores them only when the files of the mod did not change after it wrote the recovery file. Until you restore or discard them, the editor does not take new edits for the mod. So a second recovery file is never necessary.

One editor at a time edits a mod. While an editor has a mod open as the active mod, it holds a lock on `.olsave/lock`, and `.olsave/lock.json` names its process and the time it opened the mod. A second editor on the same mod shows the data, but it takes no edits and does not save. Its Console names the first editor. The second editor does not change the save files or the recovery file of the first editor. A crash can leave the two lock files, but they do not block the next editor.

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
| `oltool validate <profile.json> [--active <mod id>] [--json]` | Merge, then report the data problems that the mods add to vanilla. With `--active`, report only the problems that this mod adds to the mods that load before it, without the later mods. With `--json`, write only a JSON list of `{"severity", "source", "message"}` to stdout. A problem of a cutscene also has `"cutscene": {"path", "kind", "index", "value", "key"}`: the install path, `entity` or `event` and the place of the element in its list from 0, the path of the value, and the key of the element as `ol_match` writes it. Without a kind, the problem is about the whole script. |
| `oltool deploy <profile.json>` | Validate, then write the merged data to `<profile name>/` next to the profile, and write `<profile name>.lancer.ini`. |
| `oltool ini <profile.json\|folder> <install path>` | Print a merged INI file as text. Binary INI files are decoded. |
| `oltool dump <profile.json\|folder> <out.json>` | Write a sorted JSON dump of the data that the engine resolves. |
| `oltool thn <profile.json\|folder> <install path>` | Print a cutscene script (`.thn`) as canonical Lua text. The script comes from the merged view, so the text shows the changes of the mods. |
| `oltool thn roundtrip <profile.json\|folder> [<install path>]` | Check the canonical text of each `.thn` file of the source, or of one file. The text must give the same tables, with exact numbers and the same key order, and the text of the result must be the same text. Each problem names the first value that differs. |
| `oltool verify-base <folder>` | Check that a folder is the unchanged, read-only retail data. |

Exit codes: 0 = OK, 1 = the data has problems, 2 = the command could not run.

Start the game on a deployed profile: `lancer.exe <profiles folder>\<profile name>.lancer.ini`.

Play in the editor needs no deploy. The game of Play reads the mods through the overlay, and it writes its settings and saves to `<profile name>.play/` next to the profile. See [PLAY.md](PLAY.md).

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

The editor also checks the references before each save, with the data lists (see "Data lists and pickers" and "The save gate"). The rule data of the lists is `src/OpenLancer.Data/Editing/Lists/list-rules.json`. Each rule maps one value of one key to a list. A rule is closed only when a line of the engine shows that the engine finds the value through the list. Each closed rule names that line. A rule of a file that Librelancer does not read, for example `gate_tunnel.ini`, stays open. The retail data gives no list error and four warnings: two `parent` lines in Li01 that name an object that does not exist, and two `lens_glow` lines in `stararch.ini`.

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
