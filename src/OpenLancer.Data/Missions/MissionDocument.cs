using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions;

/// <summary>The section types of a mission script that the game reads (section 14.2 of the M6a design).</summary>
public enum MissionSectionKind
{
    Mission,
    Trigger,
    Ship,
    Formation,
    Solar,
    Loot,
    OrderList,
    Objective,
    Npc,
    Conversation,
    RandomEncounter,
    MetaBehavior,

    /// <summary>A section type that the game does not read in a mission script, for example <c>[Constants]</c>.</summary>
    Other,
}

/// <summary>One section of a mission script, with its identity in the file.</summary>
/// <param name="Index">The index of the section in the file.</param>
/// <param name="Section">The section of the parser, with its entries in file order.</param>
public sealed class MissionSection(int index, Section section, SectionRef reference)
{
    public int Index { get; } = index;

    public Section Section { get; } = section;

    /// <summary>The section in its file. A repeated nickname has its ordinal, so each section has its own
    /// reference.</summary>
    public SectionRef Ref { get; } = reference;

    /// <summary>The section type as written, for example <c>Objlist</c>.</summary>
    public string Type => Section.Name;

    public MissionSectionKind Kind { get; } = KindOf(section.Name);

    /// <summary>The value of the first <c>nickname</c> line, or null.</summary>
    public string? Nickname { get; } = section.FirstOrDefault(e => Is(e.Name, "nickname") && e.Count > 0)?[0].ToString();

    /// <summary>The kind of a section type, without letter case.</summary>
    public static MissionSectionKind KindOf(string type) => type.Trim().ToLowerInvariant() switch
    {
        "mission" => MissionSectionKind.Mission,
        "trigger" => MissionSectionKind.Trigger,
        "msnship" => MissionSectionKind.Ship,
        "msnformation" => MissionSectionKind.Formation,
        "msnsolar" => MissionSectionKind.Solar,
        "msnloot" => MissionSectionKind.Loot,
        "objlist" => MissionSectionKind.OrderList,
        "nnobjective" => MissionSectionKind.Objective,
        "npc" => MissionSectionKind.Npc,
        "dialog" => MissionSectionKind.Conversation,
        "msnrandenc" => MissionSectionKind.RandomEncounter,
        "metabehavior" => MissionSectionKind.MetaBehavior,
        _ => MissionSectionKind.Other,
    };

    public override string ToString() => Ref.Id.ToString();

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The raw layer of one mission script (section 14.2 of the M6a design): the sections of the parser in file order. It
/// keeps each section and each line as the parser gives it: unknown section types, lines before <c>nickname</c>, lines
/// without <c>=</c>, the letter case of each key, doubled backslashes, values that start with <c>=</c> and empty last
/// values. The document is immutable. Each run of the checks makes new documents.
/// </summary>
public sealed class MissionDocument
{
    private MissionDocument(string path, bool isBinary, IReadOnlyList<MissionSection> sections)
    {
        Path = path;
        IsBinary = isBinary;
        Sections = sections;
    }

    /// <summary>The install path of the script in normal form.</summary>
    public string Path { get; }

    /// <summary>True for a binary INI file.</summary>
    public bool IsBinary { get; }

    public IReadOnlyList<MissionSection> Sections { get; }

    /// <summary>Makes the document of the sections of a script.</summary>
    public static MissionDocument Of(string path, IReadOnlyList<Section> sections, bool isBinary)
    {
        var normal = VfsPath.Normalize(path) ?? path;
        var ids = SectionIdentity.Identify(sections);
        return new MissionDocument(normal, isBinary,
            sections.Select((s, i) => new MissionSection(i, s, new SectionRef(normal, ids[i]))).ToList());
    }

    /// <summary>Parses the bytes of a script with the engine parser.</summary>
    public static MissionDocument Parse(string path, byte[] bytes)
    {
        var document = OpenLancer.Data.Ini.IniDocument.Parse(path, bytes);
        return Of(path, document.Sections, document.IsBinary);
    }

    /// <summary>The sections of a kind, in file order.</summary>
    public IEnumerable<MissionSection> OfKind(MissionSectionKind kind) => Sections.Where(s => s.Kind == kind);

    /// <summary>The section of a reference, or null.</summary>
    public MissionSection? Find(SectionRef reference) =>
        VfsPath.Comparer.Equals(reference.Path, Path) ? Sections.FirstOrDefault(s => s.Ref.Id.Equals(reference.Id)) : null;
}
