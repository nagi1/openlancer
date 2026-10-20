using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>A cutscene script of the edit view (tool <c>list_cutscenes</c>).</summary>
/// <param name="Ref">The <c>thn:</c> reference.</param>
/// <param name="Category">The folder group of the Cutscenes tab, for example <c>bases</c> or <c>mods</c>.</param>
/// <param name="Kind"><c>base</c>, <c>lower_mod</c> (a lower mod changes it), <c>active_mod</c> (the active mod changes
/// it) or <c>new</c> (the active mod adds it).</param>
/// <param name="Duration">The duration in seconds, or null when the editor cannot read the script.</param>
/// <param name="Unsaved">True when the session has an edit of the script that it did not save.</param>
internal sealed record CutsceneRow(string Ref, string Path, CutsceneFolder Category, string Kind, float? Duration,
    bool Unsaved);

/// <summary>An entity or an event of a cutscene.</summary>
/// <param name="Ref">The <c>thn:</c> reference: <c>#entity/&lt;name&gt;</c>, or <c>#event/&lt;index&gt;</c>.</param>
/// <param name="Element"><c>entity</c> or <c>event</c>.</param>
/// <param name="Type">The THN name of the type, for example <c>CAMERA</c> or <c>START_SOUND</c>, or null.</param>
internal sealed record CutsceneElementView(string Ref, string Element, string? Type)
{
    /// <summary>The name of an entity, else null.</summary>
    public string? Name { get; init; }

    /// <summary>The template of an entity, else null.</summary>
    public string? Template { get; init; }

    /// <summary>The time of an event in seconds, else null.</summary>
    public float? Time { get; init; }

    /// <summary>The length of an event in seconds, or null when it has none.</summary>
    public float? Length { get; init; }

    /// <summary>The entity names that an event targets.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>The keys of the record of an entity or of the properties of an event, with their values as Lua
    /// text. <c>set_fields</c> later takes the same THN paths.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

/// <summary>A cutscene with its entities, its events and its problems (tool <c>get_cutscene</c>).</summary>
/// <param name="ReadOnlyReason">Why the editor cannot change the cutscene, or null.</param>
internal sealed record CutsceneView(string Ref, float Duration, string? ReadOnlyReason,
    IReadOnlyList<CutsceneElementView> Entities, IReadOnlyList<CutsceneElementView> Events, IReadOnlyList<Problem> Problems);

/// <summary>The cutscene query tools of section 7.1 of the MCP1 design.</summary>
internal static class CutsceneQueries
{
    /// <summary>The install paths of the cutscene scripts of each edit view. The edit view changes only with a save, so
    /// the scan of the files runs once for each one.</summary>
    private static readonly ConditionalWeakTable<ModOverlay, IReadOnlyList<string>> Paths = new();

    /// <summary>The words of the argument <c>category</c>.</summary>
    public static readonly IReadOnlyList<string> Categories =
        Enum.GetValues<CutsceneFolder>().Select(f => AgentTools.Snake(f.ToString())).ToList();

    public static ToolStep ListCutscenes(ToolCall call)
    {
        var session = call.Session;
        var category = call.Args.Text("category");
        var text = call.Args.Text("text");
        var files = Paths.GetValue(session.EditView,
            view => VfsFiles.Find(view.CreateFileSystem(), ".thn").Select(p => VfsPath.Normalize(p) ?? p).ToList());
        var rows = files.Concat(session.NewCutscenes).Distinct(VfsPath.Comparer)
            .Where(p => category == null || AgentTools.Snake(CutsceneCatalog.FolderOf(p).ToString()) == category)
            .Where(p => ToolCall.Matches(text, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ToolResult.Of(call.Paged(rows, path => new CutsceneRow(Ref(path), path, CutsceneCatalog.FolderOf(path),
            Kind(session.CutsceneSource(path)), session.Cutscene(path)?.Duration, session.HasUnsavedCutsceneEdits(path))));
    }

    public static ToolStep GetCutscene(ToolCall call)
    {
        var session = call.Session;
        var reference = ItemRefs.Parse(call.Args.Text("ref")!);
        var path = ItemRefs.ResolveCutscene(session, reference);
        var model = session.Cutscene(path)!;
        return ToolResult.Of(new CutsceneView(Ref(path), model.Duration, session.CutsceneReadOnlyReason(path),
            model.Entities.Select(e => Element(session, new ThnRef(path, ThnElementKind.Entity, e.Id))).ToList(),
            model.Events.Select(e => Element(session, new ThnRef(path, ThnElementKind.Event, e.Id))).ToList(),
            session.CutsceneErrors(path).Select(d => AgentRows.ProblemOf(session, d) with { Ref = Ref(path) }).ToList()));
    }

    /// <summary>The view of an entity or an event of W of a cutscene.</summary>
    internal static CutsceneElementView Element(EditSession session, ThnRef element)
    {
        var model = session.Cutscene(element.Path)!;
        var table = model.Element(element.Kind, element.Id)!.Table;
        var type = ThnSchema.TypeOf(element.Kind, table, session.ThnSymbols);
        var reference = ItemRefs.Of(session, element);
        if (element.Kind == ThnElementKind.Entity)
        {
            return new CutsceneElementView(reference, "entity", type)
            {
                Name = ThnScriptModel.NameOf(table),
                Template = (table["template_name"] as ThnText)?.Value,
                Fields = table.Record.ToDictionary(f => f.Key, f => ThnWriter.Value(f.Value)),
            };
        }
        var props = table.List.Length > 3 ? table.List[3] as ThnTable : null;
        return new CutsceneElementView(reference, "event", type)
        {
            Time = table.List.Length > 0 && table.List[0] is ThnNumber time ? time.Value : null,
            Length = props?["duration"] is ThnNumber length ? length.Value : null,
            Targets = table.List.Length > 2 ? Targets(table.List[2]) : [],
            Fields = props?.Record.ToDictionary(f => f.Key, f => ThnWriter.Value(f.Value)) ?? new Dictionary<string, string>(),
        };
    }

    private static string Ref(string path) => ItemRefs.Format(new ItemReference(ItemRefKind.Cutscene, "") { Path = path });

    private static IReadOnlyList<string> Targets(ThnValue value) => value switch
    {
        ThnText text => [text.Value],
        ThnTable table => table.List.OfType<ThnText>().Select(t => t.Value).ToList(),
        _ => [],
    };

    private static string Kind(ThnFileSource source) => source.Kind switch
    {
        ThnFileKind.NewHere => "new",
        ThnFileKind.PatchedHere or ThnFileKind.WholeFileHere => "active_mod",
        _ => source.ChangedByLowerMod ? "lower_mod" : "base",
    };
}
