OpenLancer @VERSION_TITLE@
@TITLE_RULE@

OpenLancer is a mod editor for Freelancer (2003). It is an independent fan
project. It is not affiliated with, endorsed by or sponsored by Microsoft,
Digital Anvil or the Librelancer project. OpenLancer uses the name
"Freelancer" only to identify the game. All trademarks belong to their owners.

This is a preview. Expect bugs. Keep a backup copy of your mods.


YOU NEED YOUR OWN COPY OF THE GAME
----------------------------------
This download contains no game files. OpenLancer reads the game data from
your own installation of Freelancer. OpenLancer does not write into your
game folder.


SYSTEM REQUIREMENTS
-------------------
- Windows 10 or Windows 11, 64-bit.
- A graphics card and a driver with OpenGL 3.2 or later.
- An installed copy of Freelancer. We tested this preview with the data of
  the English version 1.0.
- About 130 MB of disk space.
You do not need to install .NET.


START THE EDITOR
----------------
1. Extract the zip file to a folder, for example C:\Games.
2. Open the folder OpenLancer-v@VERSION@.
3. Double-click OpenLancer.Editor.exe.
4. If Windows shows "Windows protected your PC", click "More info". Then
   click "Run anyway". The preview has no digital signature.
5. The editor shows "Link your Freelancer game". Click
   "Select game folder...".
6. Select the folder of your game. This folder contains the EXE and DATA
   folders. You can also drag the folder to the editor window.
7. Make sure that the editor shows your game and its version, for example
   "Freelancer 1.0".
8. Click "Create profile and open". The editor makes a profile and an
   empty mod, my-mod, in Documents\OpenLancer. Then the Scene shows a star
   system in 3D.

On Windows 11, Smart App Control can block a program that has no digital
signature. Windows then shows no "Run anyway" button, and this preview
cannot start.


MAKE A CHANGE AND PLAY IT
-------------------------
1. Click an object in the Scene. Drag the arrows to move it.
2. Press Ctrl+S. The editor saves your change in the mod my-mod.
3. Press F5. The game starts at the camera of the Scene, with your change.
4. Press Shift+F5 to stop the game.
To make one more mod, click Mod > New mod.


WHAT WORKS IN THIS PREVIEW
--------------------------
- Link your game folder. The editor shows the game and its version. It
  identifies Freelancer 1.0 and 1.1, with or without JFLP, and
  Freelancer: HD Edition.
- Look at the star systems in a 3D Scene, with a Hierarchy and an
  Inspector.
- Look at ships, equipment, bases, factions and more in the Database.
- Change values in the Inspector. Lists show the valid values. Color
  pickers and drags on the X, Y and Z labels help.
- Move and rotate objects, and change the size of zones, with snap and
  undo.
- Select nebulas and asteroid fields in the Scene.               [if built]
- Create, delete and rename objects.                             [if built]
- Add objects from an asset browser.                             [if built]
- Save your work as a mod. The editor writes only your changes. Before a
  save, the editor checks the mod. When it finds a value that stops or
  crashes the game, it stops the save and tells you why. It does not find
  all such values.
- Test your mod with F5 (Play). A save reloads the running game.
- Look at the cutscenes of the game, and preview them. Change the values of
  a cutscene in your mod.
- oltool checks your game folder, and checks, merges and shows mods from
  the command line.


WHAT DOES NOT WORK YET
----------------------
- Export. No tool installs a mod into the original game or into
  HD Edition yet. You can test a mod only with Play of the editor.
- Play is a test run of the editor, not a release of a game. It uses the
  OpenLancer runtime, a fork of the Librelancer engine. It is not the
  original game program. It does not play the whole story. Some missions
  stop, ships have fewer AI behaviors, and some effects and cutscenes look
  different.
- Version 1.1 and HD Edition: the editor detects them, but we did not test
  the editor or Play on them. Play does not run the HD Edition plugins.
- OpenLancer does not support total conversions such as Discovery. The
  editor can open their data, with a warning.
- There is no mission editor and no dialogue editor yet.
- The mod format can change before version 1.0.


WHERE OPENLANCER KEEPS FILES
----------------------------
- Your profiles and mods: Documents\OpenLancer\profiles and
  Documents\OpenLancer\mods. You can select another folder at the first
  start.
- Play settings and saves: the folder <profile name>.play next to each
  profile.
- Editor settings and the list of linked games:
  %LOCALAPPDATA%\OpenLancer\editor.json
- Log files: %LOCALAPPDATA%\OpenLancer\logs
- Saves of the game main menu: Documents\SavedGames\OpenLancer
To remove OpenLancer, delete its folder, the folder
%LOCALAPPDATA%\OpenLancer and the folder Documents\SavedGames\OpenLancer.
Your mods are in Documents\OpenLancer. Delete that folder only if you do
not want to keep your mods.


REPORT A PROBLEM
----------------
Open an issue at https://github.com/spool1/openlancer/issues
Tell us:
- the OpenLancer version (Help > About),                             [W4]
- the game and the version that the editor shows,
- what you did, what you expected, and what happened.
Attach the log files from the logs folder. The log files show the paths
of your folders. A path can contain your Windows user name. Remove the
text that you do not want to share.
To tell us about your game folder, open a command prompt in the
OpenLancer folder. Run: oltool game "<your game folder>"
Attach the output. It names the game, its version, its folder and the
changed INI files. It contains no game content.
Do not attach game files.
Do not send OpenLancer problems to the Librelancer project.


LICENSES
--------
OpenLancer is free software under the MIT License (LICENSE.txt). It
contains third-party software. NOTICE.txt and the folder "licenses" name
each part and its license.
x64\soft_oal.dll is OpenAL Soft. It is under the GNU Library General Public
License version 2. You can replace this file with your own build of
OpenAL Soft. Its source code is the file openal-soft-1.25.2.tar.gz on the
release page.
