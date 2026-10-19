using System;
using System.Globalization;

namespace OpenLancer.Data.Publishing;

/// <summary>
/// The texts of the publish flow (section 16 of the M11c design), in strict STE. The data classes, the editor and
/// <c>oltool</c> use the same texts.
/// </summary>
public static class PublishMessages
{
    public const string DisplayNameEmpty =
        "Give the mod a display name. Players see it on Nexus, in FLMM and in the README.";

    public const string StarterName =
        "Change the display name. OpenLancer gives the name \"My mod\" to the first mod of each new profile.";

    public const string DisplayNameOneLine = "The display name must be one line.";

    public const string VersionRule =
        "Use the form 1.2.3: the major, minor and patch numbers. A pre-release such as 1.0.0-beta.1 is also correct.";

    public const string VersionLetterCase =
        "Use lowercase letters in the pre-release. Windows uses the same file name for uppercase and lowercase letters.";

    public const string PageLink = "Use a link that starts with https://.";

    public const string TagWithComma = "A tag cannot have a comma. Add each tag as a separate row.";

    public const string ImageInRepoMod =
        "This mod is part of the OpenLancer repo. OpenLancer does not add images of the game to the repo.";

    public const string ModJsonComment =
        "mod.json has a comment. OpenLancer cannot write the file and keep the comment. Remove the comments, then " +
        "click Refresh.";

    public const string RepoMod =
        "This mod is part of the OpenLancer repo. Its files are tracked files. Allow edits of this repo mod first.";

    public const string FormatVersion2Fix =
        "Write formatVersion 2 into mod.json. Then the mod can name its target games. Older OpenLancer versions do " +
        "not load a mod of format version 2.";

    public const string PendingSave =
        "The last save of this mod is not complete. Open the profile once, so that the editor completes the save.";

    public const string CheckRefusedPendingSave =
        "The last save of this mod is not complete. Open the profile once. Then check the mod.";

    public const string RenamePendingSave =
        "The last save of this mod is not complete. Open the profile once. Then rename the mod.";

    public const string RenameBusy = "Stop the game, or wait until the export ends. Then rename the mod.";

    public const string RenameRepoMod =
        "This mod is part of the OpenLancer repo. OpenLancer does not rename tracked files.";

    public static string NameIsId(string id) =>
        $"Give the mod a name that players can read. The display name is now the mod id {id}.";

    public static string VersionUnsafe(string version) =>
        $"The export cannot use version '{version}' in a file name. Use the form 1.2.3.";

    public static string VersionPublished(string version) =>
        $"Version {version} is on Nexus. A new release needs a higher version.";

    public static string SummaryLong(int length) =>
        $"The summary has {length} characters. Nexus can cut a summary after {PublishLimits.SummaryLength} characters.";

    public static string ImageRefused(string file) =>
        $"{file} is not a PNG or JPEG image. OpenLancer adds only PNG and JPEG images.";

    public static string ImageTooLarge(string file, int width, int height) =>
        $"{file} has {width} x {height} pixels. Use an image with 8,192 pixels or fewer on each side.";

    public static string ImageInGameFolder(string file, string root) =>
        $"{file} is in the game folder {root}. A mod must not contain game files. Use a screenshot that you made.";

    public static string ImageOutsideFolder(string path) =>
        $"publish.json names {path} as a screenshot. A screenshot must be in the folder publish\\screenshots of the mod.";

    public static string ImageMissing(string path) =>
        $"publish.json names {path} as a screenshot, but the file does not exist. Add the image again, or remove the entry.";

    public static string DeleteImages(int count) =>
        $"Delete {count} images from publish\\screenshots? No entry of the list uses them.";

    public static string FlmmCharacter(string character, string field) =>
        $"The FLMM package cannot show the character '{character}' in {field}. FLMM shows '?'.";

    public static string DraftQuestion(string name) => $"Apply the publish settings of {name}?";

    public static string FileChanged(string file) =>
        $"{file} changed outside this window. Load the file again, or replace it with your changes?";

    public static string PublishJsonVersion(int version) =>
        $"OpenLancer cannot read publish.json version {version}. It reads version 1. The window shows the settings " +
        "read-only.";

    public static string ModInGameFolder(string root) =>
        $"This mod is in the game folder {root}. OpenLancer does not write into a game folder.";

    public static string CheckOld(DateTime time) =>
        $"This preview is from the check of {Time(time)}. Click Check now to make a new preview.";

    public static string GuardPublished(string version) =>
        $"Version {version} is on Nexus. A new export with other game files needs a new version. Players cannot see " +
        "which package they have.";

    public static string GuardExported(string version, DateTime time) =>
        $"The export of {version} on {Time(time)} has other game files. Players cannot see which of the two packages " +
        "they have. Export as a new version?";

    public static string GuardPartialReplace(string version, DateTime time, string targets) =>
        $"The export of {version} on {Time(time)} also has packages for {targets}. Check these targets too, or export " +
        "as a new version.";

    /// <summary>The text of <c>oltool export --same-version</c> for a version with the mark "published".</summary>
    public static string SameVersionPublished(string version) =>
        $"Version {version} is on Nexus. Write a higher version into mod.json.";

    public static string SamePackages(string version, DateTime time) =>
        $"The packages are the same as in the export of {version} on {Time(time)}.";

    public static string TextsOnly(string version, DateTime time) =>
        $"Only the texts changed. The game files are the same as in the export of {version} on {Time(time)}. The " +
        $"export keeps version {version}.";

    public static string TextsOnlyPublished(string version) =>
        $"Version {version} is on Nexus. Only the texts of the packages changed. Upload the new packages to Nexus, or " +
        "keep the old ones.";

    public static string ExportNextStep(string version) =>
        $"Next: upload the files to Nexus. Then mark version {version} as published in the History tab of the Mods " +
        "window.";

    public static string MarkAsTested(string name) =>
        $"Mark the current content of {name} as tested in the game? Do this only when you or a tester played this " +
        "content outside OpenLancer.";

    public static string RemoveRefused(string other) => $"Mod '{other}' needs this mod. Remove '{other}' first.";

    public static string RenameRefusedDependsOn(string other, string oldId) =>
        $"Mod '{other}' needs '{oldId}' in its dependsOn list. OpenLancer does not change the files of another mod. " +
        $"Remove '{oldId}' from that list first.";

    public static string RenameWarningLoadAfter(string other, string oldId) =>
        $"Mod '{other}' loads after '{oldId}'. After the rename, '{other}' does not find the mod, and the load order " +
        "can change.";

    public static string RenameAfterRelease(string version, string oldId) =>
        $"Players of version {version} have files with the old id {oldId}, for example EXE\\ol_{oldId}.dll. An update " +
        "with the new id does not remove these files. Rename the mod only before the first release.";

    public static string RenameProfileNotFound(string path) =>
        $"OpenLancer cannot find the profile {path}. When it names this mod, it does not load after the rename.";

    public static string RenameHistoryStays(string folder) =>
        $"{folder} exists. The publish history of this mod stays in its old folder.";

    public static string RenameFailed(string step, string reason) =>
        $"The rename stopped at {step}: {Games.GameRoot.Sentence(reason)} OpenLancer restored each changed file.";

    public static string IdInUse(string id) => $"A mod with the id {id} exists. Use another id.";

    public static string HistoryBad(string file, string badFile) =>
        $"{file} did not load. OpenLancer moved it to {badFile} and started a new history.";

    public static string HistoryUnreadable(string file) =>
        $"{file} did not load. The next record moves it to {PublishHistory.BadName} and starts a new history.";

    public static string HistoryVersion(string file, int version) =>
        $"OpenLancer cannot read {file} version {version}. It reads version 1. OpenLancer does not write the file.";

    public static string HistoryBusy(string record) =>
        $"Another program writes the publish history now. OpenLancer did not record this {record}.";

    public static string HistoryOfAnotherFolder(string folder) => $"This history belongs to the mod in {folder}.";

    /// <summary>A time for a text: the local date and time, without seconds.</summary>
    public static string Time(DateTime time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>The limits of the publish texts (decision D575 and section 8.1 of the M11c design).</summary>
public static class PublishLimits
{
    /// <summary>The length of a summary above which Nexus can cut it. No source confirms it (question Q1).</summary>
    public const int SummaryLength = 350;

    /// <summary>The longest description in UTF-8 bytes.</summary>
    public const int DescriptionBytes = 64 * 1024;

    /// <summary>The longest display name, as <c>NewModDialog</c> allows.</summary>
    public const int NameLength = 128;

    /// <summary>The longest file description of a Nexus page (question Q1).</summary>
    public const int FileDescriptionLength = 255;
}
