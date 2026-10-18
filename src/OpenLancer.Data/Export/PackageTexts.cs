using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Export;

/// <summary>The kind of a package, for its README.</summary>
public enum PackageForm
{
    /// <summary>A zip with the folders DATA and EXE at its top. The player extracts it over the game folder.</summary>
    DropIn,

    /// <summary>An FLMM package: a <c>.flmod</c> file with a script for the Freelancer Mod Manager.</summary>
    Flmm,

    /// <summary>A zip of the mod folder for OpenLancer.</summary>
    ModFolder,
}

/// <summary>
/// The texts of an export (milestone M11): the README of each package, the text of the Nexus page and the checklist
/// for testers. No text has an AI tag or a request for Donation Points.
/// </summary>
public static class PackageTexts
{
    /// <summary>The notice that the Game Content Usage Rules of Microsoft ask for, with the link to the rules.</summary>
    public const string RulesLink = "https://www.xbox.com/en-US/developers/rules";

    /// <summary>The file name of the README of a mod in a package.</summary>
    public static string ReadmeName(ModManifest mod) => $"{mod.Id} README.txt";

    /// <summary>The notice of the Game Content Usage Rules. The rules give its words.</summary>
    public static string Notice(ModManifest mod) =>
        $"Freelancer © Microsoft Corporation. {mod.Name} was created under Microsoft's \"Game Content Usage Rules\" using " +
        $"assets from Freelancer, and it is not endorsed by or affiliated with Microsoft. The rules: {RulesLink}";

    /// <summary>The README of a package.</summary>
    /// <param name="files">The files of the target build, or null for the target OpenLancer.</param>
    /// <param name="entries">The files of the package, by their path in the game folder.</param>
    /// <param name="probe">True for the FLMM probe of the FLMM check.</param>
    public static string Readme(ModManifest mod, ExportTarget target, PackageForm form, TargetFiles? files,
        IReadOnlyList<string> entries, bool probe = false)
    {
        var lines = new List<string>
        {
            mod.Name,
            $"Version {mod.Version} for {target.Name}. Mod id: {mod.Id}.",
        };
        if (probe)
            lines.Add("Probe for the FLMM check. Do not publish.");
        lines.Add("");
        lines.Add("Requirements:");
        lines.AddRange(Requirements(target, files).Select(r => "- " + r));
        lines.Add("");
        switch (form)
        {
            case PackageForm.DropIn:
                lines.Add("Files:");
                lines.AddRange(entries.Select(e => "- " + e));
                lines.Add("");
                lines.Add("Install:");
                lines.Add("1. Copy the files of the list from your game folder to a backup folder.");
                lines.Add("2. Extract the zip into your game folder. Let it replace the files.");
                lines.Add("");
                lines.Add("Remove:");
                lines.Add("1. Copy the files of the backup folder back into your game folder.");
                lines.Add("2. Delete the files of the list that the backup folder does not have.");
                lines.Add("");
                lines.Add("This zip replaces whole files. It does not combine with other mods that change the same files.");
                if (files != null && entries.Any(e => VfsPath.Comparer.Equals(e, ModOverlay.FreelancerIniPath)))
                    lines.AddRange(FreelancerIniLines(target, files));
                break;
            case PackageForm.Flmm:
                lines.Add("Install:");
                lines.Add($"1. In the Freelancer Mod Manager, import {mod.Id}.flmod.");
                lines.Add("2. Activate the mod.");
                lines.Add("");
                lines.Add("Remove:");
                lines.Add("1. In the Freelancer Mod Manager, deactivate the mod. FLMM restores the files of the game.");
                break;
            case PackageForm.ModFolder:
                lines.Add("Install:");
                lines.Add("1. Extract the zip into the mods folder of your OpenLancer workspace.");
                lines.Add($"2. Add the mod {mod.Id} to a profile.");
                lines.Add("");
                lines.Add("Remove:");
                lines.Add($"1. Remove the mod from the profile, and delete its folder {mod.Id}.");
                break;
        }
        lines.Add("");
        lines.Add(Notice(mod));
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>The requirements of a package for players.</summary>
    private static IEnumerable<string> Requirements(ExportTarget target, TargetFiles? files)
    {
        if (target.Id == ModTargets.HdEdition)
            yield return "Freelancer: HD Edition 0.7.1 (Nexus mod 29).";
        else if (target.Id == ModTargets.Vanilla)
            yield return "Freelancer 1.1, with or without JFLP.";
        else
            yield return "OpenLancer.";
        if (files == null)
            yield break;
        if (TargetChecks.ChangesGameplay(files))
            yield return TargetChecks.GameplayNote;
        if (ChangesStart(files))
            yield return "A new game.";
        yield return "Not compatible with total conversions.";
    }

    /// <summary>True when the mod changes the files of a new character or the loadouts of the start ship.</summary>
    private static bool ChangesStart(TargetFiles files)
    {
        string[] startFiles = [@"EXE\newplayer.fl", @"EXE\mpnewcharacter.fl", @"DATA\Characters\NewCharacter.ini"];
        if (files.Files.Any(f => startFiles.Contains(f.Path, VfsPath.Comparer)))
            return true;
        var fs = files.Base.OpenFileSystem();
        if (!fs.FileExists(startFiles[2]))
            return false;
        var start = IniDocument.Parse(startFiles[2], fs.ReadAllBytes(startFiles[2])).Sections
            .SelectMany(s => s).Where(e => e.Name.Equals("loadout", StringComparison.OrdinalIgnoreCase) && e.Count > 0)
            .Select(e => e[0].ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Files.Where(f => f.Diff != null).Any(f =>
            f.Diff!.Changed.Select(p => f.Diff.MergedSections[p.Merged])
                .Concat(f.Diff.Removed.Select(k => f.Diff.BaseSections[k]))
                .Any(s => s.Name.Equals("Loadout", StringComparison.OrdinalIgnoreCase) &&
                          SectionIdentity.KeyOf(s) is { } key && start.Contains(key)));
    }

    /// <summary>The lines of the README about <c>EXE\freelancer.ini</c>: the lines that the mod adds, with their
    /// place.</summary>
    private static IEnumerable<string> FreelancerIniLines(ExportTarget target, TargetFiles files)
    {
        var file = files.File(ModOverlay.FreelancerIniPath);
        if (file?.Diff == null)
            yield break;
        yield return "";
        yield return target.Id == ModTargets.HdEdition
            ? @"EXE\freelancer.ini of this zip keeps the options that the author picked in the HD Edition installer, for " +
              "example the intro movies. To keep your own options, do not copy this file. Add these lines to your own file:"
            : @"EXE\freelancer.ini of this zip is the file of the game of the author. To keep your own file, do not copy " +
              "this file. Add these lines to your own file:";
        foreach (var pair in file.Diff.Changed)
        {
            var section = file.Diff.MergedSections[pair.Merged];
            foreach (var j in pair.Inserted)
            {
                var line = IniTextEdit.Line(section[j].Name, section[j]);
                if (section.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase))
                {
                    var number = section.Take(j + 1).Count(e => e.Name.Equals("DLL", StringComparison.OrdinalIgnoreCase));
                    yield return $"- In [Resources], add \"{line}\" as the {Ordinal(number)} DLL line.";
                }
                else
                    yield return $"- In [{section.Name}], add \"{line}\" after the last {section[j].Name} line.";
            }
        }
        if (files.StringIds.Strings.Count + files.StringIds.Infocards.Count > 0)
            yield return "Two OpenLancer mods with text do not work together as drop-in zips, because both use the same " +
                         "text index. Use the FLMM package for that.";
    }

    /// <summary>The text of a Nexus page: the items in the order of the page.</summary>
    public static string NexusPage(ModManifest mod, IReadOnlyList<(ExportTarget Target, IReadOnlyList<string> Packages)> packages)
    {
        var lines = new List<string>
        {
            $"Name: {mod.Name}",
            $"Version: {mod.Version}",
            "",
            "Files:",
        };
        foreach (var (target, names) in packages)
            lines.AddRange(names.Select(n => $"- {n} ({target.Name})"));
        lines.Add("");
        lines.Add("Requirements:");
        foreach (var (target, _) in packages)
            lines.Add($"- {target.Name}: " + target switch
            {
                _ when target.Id == ModTargets.HdEdition => "Freelancer: HD Edition 0.7.1 (Nexus mod 29).",
                _ when target.Id == ModTargets.Vanilla => "Freelancer 1.1, with or without JFLP.",
                _ => "OpenLancer.",
            });
        lines.Add("");
        lines.Add("Install: see the README in each package.");
        lines.Add("");
        lines.Add(Notice(mod));
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// The checklist for testers: one line for each entry that the mods add, change or remove, in plain words, by file.
    /// </summary>
    public static IReadOnlyList<string> Checklist(TargetFiles files)
    {
        var lines = new List<string>();
        foreach (var file in files.Files)
        {
            var items = Items(file, files).ToList();
            if (items.Count == 0)
                continue;
            lines.Add(file.Path);
            lines.AddRange(items.Select(i => "- " + i));
        }
        return lines;
    }

    private static IEnumerable<string> Items(TargetFile file, TargetFiles files)
    {
        switch (file.Kind)
        {
            case TargetFileKind.TextDll:
                yield return $"Text: {Count(files.StringIds.Strings.Count, "string")} and " +
                             $"{Count(files.StringIds.Infocards.Count, "infocard")} in {file.Path}.";
                yield break;
            case TargetFileKind.Cutscene:
                yield return Cutscene(file);
                yield break;
            case TargetFileKind.ModFile:
                yield return file.HasBase ? $"The mod replaces {file.Path}." : $"New file {file.Path}.";
                yield break;
        }
        if (file.Diff is not { } diff)
            yield break;
        if (!file.HasBase)
        {
            yield return $"New file {file.Path} with {diff.MergedSections.Count} sections.";
            yield break;
        }
        foreach (var k in diff.Removed)
            yield return $"Removed {Label(diff.BaseSections, k)}.";
        foreach (var pair in diff.Changed)
        {
            var label = Label(diff.MergedSections, pair.Merged);
            var section = diff.MergedSections[pair.Merged];
            var old = diff.BaseSections[pair.Base];
            foreach (var i in pair.Changed)
                yield return $"{label}: {old[i].Name} {Values(old[i])} -> {Values(section[pair.MergedOf[i]])}.";
            foreach (var i in pair.Removed)
                yield return $"{label}: removed {IniTextEdit.Line(old[i].Name, old[i])}.";
            foreach (var j in pair.Inserted)
                yield return $"{label}: new {IniTextEdit.Line(section[j].Name, section[j])}.";
        }
        foreach (var m in diff.Added)
        {
            var section = diff.MergedSections[m];
            var market = section.Name.Equals("BaseGood", StringComparison.OrdinalIgnoreCase)
                ? section.FirstOrDefault(e => e.Name.Equals("base", StringComparison.OrdinalIgnoreCase))
                : null;
            if (market is { Count: > 0 })
            {
                foreach (var good in section.Where(e => e.Name.Equals("marketgood", StringComparison.OrdinalIgnoreCase) &&
                                                        e.Count > 0))
                    yield return $"New market good '{good[0]}' at base '{market[0]}'.";
                continue;
            }
            yield return $"New {Label(diff.MergedSections, m)}.";
        }
    }

    /// <summary>The line of a changed cutscene: the number of changed, new and removed entities and events.</summary>
    private static string Cutscene(TargetFile file)
    {
        if (file.BaseTree == null || file.MergedTree == null)
            return $"New cutscene {file.Path}.";
        var ids = ThnElementId.Counter();
        ThnScriptModel before, after;
        try
        {
            before = ThnScriptModel.From(file.BaseTree, ids);
            after = ThnScriptModel.From(file.MergedTree, ids);
        }
        catch (FormatException)
        {
            // A whole file of a mod can have another form than a cutscene of the game.
            return $"Cutscene {file.Path}: changed.";
        }
        var parts = new List<string>();
        var entities = Changes(before.Entities.Select(e => e.Table), after.Entities.Select(e => e.Table));
        var events = Changes(before.Events.Select(e => e.Table), after.Events.Select(e => e.Table));
        if (entities > 0)
            parts.Add(entities == 1 ? "1 entity changed" : $"{entities} entities changed");
        if (events > 0)
            parts.Add(events == 1 ? "1 event changed" : $"{events} events changed");
        if (before.Duration != after.Duration)
            parts.Add("the duration changed");
        return $"Cutscene {file.Path}: {(parts.Count == 0 ? "changed" : string.Join(", ", parts))}.";
    }

    /// <summary>The number of changed elements. It is the larger of two counts: the elements of the new list that the
    /// old list does not have, and the elements of the old list that the new list does not have.</summary>
    private static int Changes(IEnumerable<ThnTable> before, IEnumerable<ThnTable> after)
    {
        var left = before.ToList();
        var added = 0;
        foreach (var table in after)
        {
            var index = left.IndexOf(table);
            if (index < 0)
                added++;
            else
                left.RemoveAt(index);
        }
        return Math.Max(added, left.Count);
    }

    /// <summary>A section for a person, for example "Good 'lf_hull'" or "[SystemInfo]".</summary>
    private static string Label(IReadOnlyList<Section> sections, int index)
    {
        var id = SectionIdentity.Of(sections, index);
        return id.Key != null && id.Parent == null && id.Ordinal == 0 ? $"{id.Type} '{id.Key}'" : id.ToString();
    }

    private static string Values(Entry entry) => string.Join(", ", entry.Select(RetailText.Of));

    /// <summary>A number and a noun, for example "1 string" or "2 strings".</summary>
    private static string Count(int number, string noun) => number == 1 ? $"1 {noun}" : $"{number} {noun}s";

    private static string Ordinal(int number) => (number % 100) switch
    {
        11 or 12 or 13 => $"{number}th",
        _ => (number % 10) switch
        {
            1 => $"{number}st",
            2 => $"{number}nd",
            3 => $"{number}rd",
            _ => $"{number}th",
        },
    };
}
