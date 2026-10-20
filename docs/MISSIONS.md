# The mission studio

The editor shows each mission script of the game data and of the mods. It shows a mission as a graph of triggers beside the Scene, with named fields in the Inspector and the checks of the script. With an active mod, it also edits the mission. A save writes the changes into the mod as a patch of the script. The format of a mission patch is in [MOD_FORMAT.md](MOD_FORMAT.md), section "Mission files".

Without an active mod, the studio shows each mission and takes no edits. So you can study how the retail missions work, and no mod changes.

This version has these limits:

- The graph of prefab mode shows a slot of a field as its label in brackets, for example "[Enemy formation]". The ports of a prefab do not show as pins.
- The Scene part does not drag mission items yet. Use "Pick in Scene" to set a position.
- The editor writes the lines of a mission. OpenLancer does not run each line yet. The column OpenLancer of the Missions tab tells what it runs.

## Open a mission

1. Run `start.bat` to build and start the editor.
2. Click File > Open profile, then click a profile.
3. Click the Missions tab at the bottom of the window. The 14 story missions come first, in the order of the game. The other scripts of the game data and the mods follow.
4. Type a part of a name, a nickname or a value in the search field, for example `m04` or a trigger name.
5. Double-click a row. The mission opens in the Mission tab, beside the Scene tab. One mission is open at a time.

## The Missions tab

Each row is one mission script.

| Column | Content |
|---|---|
| Slot | The story slot. The game code has 14 slots in a fixed order. |
| Mission script | The file that the game loads for the slot. |
| Start stage, Accept stage | The story stages when the mission starts, and after the player accepts it. |
| Next mission | The time when the next mission starts after a success. The game code sets it. A mod cannot change it. |
| State | Base game, Changed (a patch of a mod changes the script), Replaced (a mod has its own file) or Missing. |
| Triggers, Errors, Warnings | The counts of the script. The number in brackets counts the hidden base-game messages. |
| OpenLancer | What the OpenLancer engine runs of the script. The retail game runs all lines. |

The toggle "base-game messages" shows the problems that the base game has too. They are hidden by default, because a mod does not cause them. The Console and all mission views use the same toggle.

## The Mission tab

The Mission tab shows the open mission as a graph. The Scene part is at its right, and a splitter is between them. The toolbar has the mission menu, the switch between Graph and Raw INI, the lens, "Show lines" and "Frame all". It also has the search, the menu Systems, the toggle Library, "Add..." and the toggle Scene.

### The graph

- Each node is one trigger. Its header has the glyph of its event, its name, and the chips "Start" and "Repeats". Its body shows the event and the actions as a summary. With "Show lines", it shows each line.
- The pins at the left take a start, a stop, a load from a checkpoint and a watch. The pins at the right give them. The "+" pin in the header starts or stops another trigger.
- A frame groups triggers: an act, the frame "Not started by the mission", or a behavior prefab. Click the arrow of a frame to collapse it into one node.
- The marks at the right of a header show errors, warnings, the muted base-game messages, "Never starts" and "Watched state". Hover a mark to read the reasons.

A lens shows one part of the mission. A lens does not change data.

| Lens | Shows |
|---|---|
| Beats | Each behavior prefab as one box. This is the default lens. |
| Logic | All triggers, with all frames open. |
| Battle | Spawns, orders, sides and combat conditions. Other triggers fade. |
| Conversation | Comm lines, conversations and the triggers that wait for them. |
| Cutscene | Space cutscenes, cameras and base scenes, in the order of the mission. |
| Objectives | Objectives, titles, offers and log texts, in the order of the mission. |

The keys work while the graph has the focus:

| Key | Action |
|---|---|
| F | Frame the selection. |
| A or Home | Frame the whole graph. |
| Ctrl+F | Go to the search. Enter frames the next result, and Shift+Enter the one before. |
| F3, Shift+F3 | Go to the next or the previous item that the selection uses. |
| Space | Open quick add. |
| F2 | Rename the selected trigger. |
| Ctrl+D | Duplicate the selected triggers and prefab instances. |
| Delete | Delete the selected triggers, links, acts and prefab instances. |
| Ctrl+C, Ctrl+V | Copy and paste triggers as INI text. |
| Esc | End the path view or the neighbor view. In prefab mode, go back to the mission. |

### The Scene part

The Scene part shows the system of the mission. The toggle "Mission items" of its toolbar shows the ships, points, volumes and paths of the mission in the color of the mission items. Select a trigger, and the Scene shows what it uses. Select an item in the Scene, and the graph outlines the triggers that use it. Double-click a node to frame its items in the Scene. The menu "Mission systems" opens another system of the mission.

When the Mission tab is narrow, the Scene part closes. The toggle Scene of the toolbar opens it again.

### The Raw INI view

The switch Raw INI shows the whole script as text, with the colors of the INI keys and the marks of the checks. A cursor in a section selects its item. With an active mod, you can edit the text. The editor checks the text when you leave the field. A problem keeps nothing: the Console holds the problem and your text, so you can copy it back.

## The Hierarchy and the Inspector

In Mission mode, the Hierarchy lists the items of the open mission in groups. The groups are Logic, Cast, Labels, Orders, Objectives, Conversations and Links. Then come Random encounters, Behavior prefabs, Placed near the player, System unknown and Other sections.

The Inspector shows the page of the selected item. Each page has the same parts:

1. The header with the glyph, the name, the kind and the info icon. The info icon tells what the item is and what it writes to the game files.
2. The completeness panel, for example "4 of 5 steps complete". Each missing step tells why it is necessary. "Fix" makes the change when it needs no choice.
3. The sections with named fields, for example "Starts when" and one section for each group of actions.
4. The INI block of the section, with the marks of the checks.

A field that the active mod sets has a blue bar at the left of its label. A prefab override has a violet bar and a bold label (see "Behavior prefabs").

## Edit a mission

The studio takes edits when the profile has an active mod. Each gesture is one entry of the edit history. Undo and Redo work as in the Scene. A save writes the patch of each changed script and the editor files of the mod.

| Where | Gesture |
|---|---|
| Graph | Drag from the "+" pin of a trigger to the Start pin or the Stop pin of another trigger. Drag from an output pin to an empty place, then pick an item in quick add. The new trigger links to the pin. |
| Graph | Right-click a link to insert a trigger on it or to remove it. Right-click a node for "Starts with the mission", "Repeats", "Add action...", "Set condition...", Rename, Duplicate and Delete. |
| Graph | Drag nodes to move them. Right-click the background for "New act from selection". |
| Library | Drag an item of the strip at the left of the graph onto a trigger or onto an empty place. An item is a prefab, an event, an action or a condition. A double-click adds it to the selected trigger, or at the center of the view. |
| Quick add | Press Space, type a part of the name, then press Enter. |
| Inspector | Change a field. A name has a picker, a word has a dropdown and a list has the buttons Add and Remove. "Pick in Scene" sets a position with a click in the Scene. |
| Inspector | Click "Fix" on a problem or on a missing step. |
| Hierarchy | Use the "+" menu to add an item. Drag a ship from the Assets panel onto the group Cast to add a mission ship. |
| Raw INI | Edit the text of the script or of the INI block of a page. |

A delete of a trigger keeps each line that names it, and the line shows "Missing". A rename changes each line that names the trigger, in the same entry of the history.

The save gate stops a save when a script has an error that the edit made, for example a trigger without a condition. A problem that the base game has too is a warning. The Console lists the problems after each edit.

## Behavior prefabs

A behavior prefab is a group of triggers with named fields, for example "Spawn wave" with the fields Ships, "Spawn when", Orders and Sides. The studio knows three forms:

- A **matched instance** is a group of retail triggers that has the pattern of a prefab. The graph shows it in a frame. Its fields read the lines of its triggers. A field with a write rule writes those lines.
- A **linked instance** is a group of triggers that the editor made from a prefab. The graph file of the mod records its prefab, its field values and its triggers. A change of the prefab changes each linked instance.
- A **prefab asset** is a prefab file of a mod, `prefabs/missions/<id>.ini`. The editor also has 17 built-in prefabs. They are Checkpoint, Offer mission, Play space cutscene, Branch, Ambush, Spawn wave, Destroy targets, Conversation and Mission success. Then come Mission fail, Keep applying, Set reputation, Lock or unlock gate, Objective, Watched state, Escort and Patrol.

### Place a prefab

1. Open a mission, and make sure that the profile has an active mod.
2. Open the group "Prefabs" of the library strip. The built-in prefabs come first, then the prefabs of the active mod, then the prefabs of the lower mods with a lock.
3. Drag a prefab onto an empty place of the graph. Or drag a tile of the category "Mission prefabs" of the Assets panel.
4. Select the new frame. The Inspector shows the fields and the completeness panel of the instance.
5. Set the fields. Each edit compiles the triggers of the instance again.

### Overrides

A field of a linked instance that differs from the prefab is an override. Its label is bold with a violet bar. An override that is not saved also has the blue bar. A field that each instance sets, for example the ships of a spawn wave, never shows as an override.

The menu of an override has:

- **Revert**: use the value of the prefab.
- **Apply to prefab '{name}'**: make the value the value of the prefab. Each instance of the prefab in the active mod without an override of the field gets it.
- **Make a variant with this value**: for a built-in prefab or a prefab of a lower mod, which cannot change. The editor copies the prefab into the active mod with the value, and the instance uses the copy.

The page menu "Overrides ({n})" lists each override with Revert and Apply. It also has "Revert all" and "Apply all", as the Overrides list of Unity does.

A trigger of a linked instance takes no line edits, because the prefab gives its lines. Change a field of the instance, open the prefab, or unpack the instance. A delete of such a trigger is allowed. It shows as a removed trigger with the violet bar, and Revert brings it back.

### Save as prefab and unpack

- **Save as prefab...** in the menu of a matched instance makes a prefab asset from its triggers. Each value that a field reads becomes a field of the prefab, with the value of the instance as its default. The instance becomes a linked instance of the new prefab, with no override.
- **Unpack** removes the link of a linked instance. The triggers stay, and the studio can find them again as a matched instance.

### Prefab mode

Prefab mode edits a prefab asset of the active mod. A change there changes each linked instance of the prefab in the active mod.

1. Click "Open prefab" in the menu of a frame or on the page of an instance. Or double-click a tile of "Mission prefabs" in the Assets panel.
2. The bar at the top of the Mission tab names the mission and the prefab. It tells how many instances change, for example "Changes here change 3 instances in 2 missions of mod my-mod."
3. Edit the triggers of the prefab in the graph and in the Inspector, as in a mission. The Scene part closes, because a prefab has no world.
4. To make a value a field, open the menu of the value and click "Field: {label}". Click "Fixed value" to give it one value again.
5. Select the prefab row of the Hierarchy to see the Prefab page. It shows the fields with their kind and default, the ports and the sections. Change a default there, or remove a field.
6. Press Esc or click the back arrow to go back to the mission.

Each edit in prefab mode and the compile of all instances are one entry of the history. The Console says how many instances changed, and names each instance that did not change. A built-in prefab and a prefab of a lower mod open read-only, with the button "Make variant".

### The Assets panel

The category "Mission prefabs" of the Assets panel shows the prefab assets of the active mod and of the lower mods. A prefab of a lower mod has a lock.

- F2 renames a prefab of the active mod. The file, its id and each instance of the active mod change.
- Delete removes a prefab of the active mod. The editor asks first when instances use it. The instances keep their lines and get the warning P02. Undo brings the prefab back.
- A drag of a tile into the graph places the prefab.

### Warnings of linked instances

| Rule | Cause | Fix |
|---|---|---|
| P01 | The lines of the instance differ from the last compile, for example after an edit of the patch with a text editor. A change of the prefab does not change this instance. | "Compile again" or "Unpack" |
| P02 | No mod has the prefab, or the prefab file has a problem. The triggers stay as they are. | "Unpack", or add the prefab again |
| P03 | The prefab changed after the last compile of the instance. | "Compile again" |

## Files of the studio

The editor keeps the places of the nodes, the acts and the records of the linked instances in a graph file of the mod. It keeps each prefab asset in a prefab file. The game does not read these files. See "Editor files of missions" in [MOD_FORMAT.md](MOD_FORMAT.md).
