using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private ItemIndex? items;

    /// <summary>The item files of the edit view: the ship, equipment, goods and market files.</summary>
    internal ItemIndex Items => items ??= new ItemIndex(ReadItemPaths(), path => TryDocument(path, out var d) ? d : null);

    /// <summary>
    /// The section of the files of a kind with a nickname, or null when no section has it, or when more than one
    /// section has it (the engine then uses only one of them). Market sections have no nickname.
    /// </summary>
    public SectionRef? Find(ItemFileKind kind, string nickname) => Items.Find(kind, nickname);

    /// <summary>The sections of the files of a kind that are not child sections, in load order.</summary>
    public IReadOnlyList<SectionRef> List(ItemFileKind kind) => Items.List(kind);

    /// <summary>The sections that a relation links an item to, in load order. The list is empty when the item has no
    /// such link.</summary>
    public IReadOnlyList<SectionRef> Related(SectionRef item, ItemRelation relation) => Items.Related(item, relation);

    /// <summary>The <c>marketgood</c> lines of all market files that sell a good, in load order. A line of a
    /// <c>[BaseGood]</c> section that the active mod does not add is read-only (see <see cref="Get"/>).</summary>
    public IReadOnlyList<MarketLine> MarketLines(string good) => Items.MarketLines(good);

    /// <summary>
    /// The keys that the engine reads in a section, in declaration order. The list is empty when the editor does not
    /// know the schema of the section. In format version 1, the editor knows the schema of the item files only.
    /// </summary>
    public IReadOnlyList<KeySchema> Schema(SectionRef target) => SchemaOf(target)?.Keys ?? [];

    /// <summary>What the engine reads from the lines of a name in a section, or null when the schema has no such
    /// key, or when the editor does not know the schema of the section.</summary>
    public KeySchema? Schema(SectionRef target, string name) => SchemaOf(target)?.Key(name);

    /// <summary>
    /// The fast checks of a section: an unknown key, a missing required key, a key with one value given two times,
    /// a line with too few values, a value that the engine cannot read as the type of its key, a text placeholder of
    /// the wrong kind or without text, and a nickname that another section of the same kind of file also has. The
    /// list is empty when the edit view has no such section, or when the editor does not know its schema.
    /// </summary>
    public IReadOnlyList<Diagnostic> Check(SectionRef target)
    {
        if (SchemaOf(target) is not { } schema || !TryFind(target, out var document, out var index))
            return [];
        var section = document.Working[index];
        var problems = SectionChecks.Check(schema, section, target);
        if (ActiveMod != null)
        {
            // The save refuses a placeholder without text (rule S8).
            problems.AddRange(section.SelectMany(MissingPlaceholders).Distinct().Select(p =>
                Diagnostic.Error(target.Path, $"{target.Id}: {MissingTextMessage(p)}") with { Target = target }));
        }
        var kind = Items.KindOf(target.Path)!.Value;
        var key = target.Id.Key;
        if (key != null && !SectionKeys.IsChild(target.Id.Type) && Items.Named(kind, key) is { Count: > 1 } same)
        {
            problems.Add(Diagnostic.Warning(target.Path,
                $"{target.Id}: {same.Count} sections of the {kind.ToString().ToLowerInvariant()} files have this " +
                $"nickname, so the engine uses only one of them: {string.Join(", ", same.Select(s => s.Path))}.") with
            {
                Target = target,
            });
        }
        return problems;
    }

    private SectionSchema? SchemaOf(SectionRef target) =>
        Items.KindOf(target.Path) is { } kind ? SchemaCatalog.Section(ItemIndex.SchemaOf(kind), target.Id.Type) : null;

    /// <summary>The item files that the <c>freelancer.ini</c> of the edit view lists.</summary>
    private List<(string Path, ItemFileKind Kind)> ReadItemPaths()
    {
        var fs = EditView.CreateFileSystem();
        if (!fs.FileExists(ModOverlay.FreelancerIniPath))
            return [];
        var bytes = fs.ReadAllBytes(ModOverlay.FreelancerIniPath);
        return ItemIndex.ReadPaths(IniDocument.Parse(ModOverlay.FreelancerIniPath, bytes).Sections);
    }
}
