using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The only write path for mission files (section 4 of the M6b design, decision D660). The edit session owns one
/// instance (<see cref="EditSession.Missions"/>). The studio, the completeness fixes, the M3 rename and delete, the
/// tests and the agent tools call it. It makes no ImGui call.
/// </summary>
/// <remarks>
/// <para>The editor keeps a model of each mission script by the state of W (section 7 of the M6b design). The model
/// of a state comes from the cache, so an undo finds the model of its state again.</para>
/// <para>Each request runs on the thread that owns the session (section 4.0.3 of the M6b design).</para>
/// </remarks>
public sealed partial class MissionEditor
{
    private readonly EditSession session;
    private IMissionWorldSource world;
    private readonly MissionModelCache cache = new();
    private readonly Dictionary<string, long> versions = new(VfsPath.Comparer);

    /// <param name="world">Gives the names of the game data that the checks need.</param>
    public MissionEditor(EditSession session, IMissionWorldSource world)
    {
        this.session = session;
        this.world = world;
        seenRevision = session.Revision;
        References = new MissionReferences(session);
        session.Changed += OnHistory;
    }

    /// <summary>The reference provider for the world edits of M3 and of the Database (section 4.10 of the M6b
    /// design).</summary>
    public MissionReferences References { get; }

    /// <summary>The read queries in the vocabulary of the commands (section 4.12 of the M6b design).</summary>
    public MissionQueries Queries => queries ??= new MissionQueries(session, this);

    private MissionQueries? queries;

    /// <summary>The OpenLancer marks of the checks, when the world source is the source of the session. The editor
    /// gives the marks of the engine. Other sources keep their own marks.</summary>
    public IEngineCoverage Coverage
    {
        get => (world as SessionMissionWorld)?.Coverage ?? world.Inputs().Coverage;
        set
        {
            if (world is SessionMissionWorld source)
                source.Coverage = value;
        }
    }

    /// <summary>Gives the editor another world source, for example a fixed world in a test. The models go.</summary>
    internal void UseWorld(IMissionWorldSource source)
    {
        world = source;
        cache.Use(source.Inputs());
    }

    private (object View, IReadOnlyList<string> Paths)? scanned;

    /// <summary>
    /// The install paths of the mission scripts of W: the scripts of the scan of the edit view (decision D535), and each
    /// script that the session opened, for example a new script. A script that W deletes is not in the list.
    /// </summary>
    public IReadOnlyList<string> ScriptPaths()
    {
        if (scanned is not var (view, paths) || !ReferenceEquals(view, session.EditView))
        {
            var files = session.EditView.CreateFileSystem();
            paths = session.DataPath("MISSIONS") is { } missions ? MissionFiles.Scan(files, missions) : [];
            scanned = (session.EditView, paths);
        }
        // A result gives the path as the session stores it (section 4.0.7 of the M6b design).
        return paths.Concat(session.OpenMissionScripts()).Distinct(VfsPath.Comparer)
            .Select(p => session.OpenDocument(p)).OfType<EditDocument>()
            .Where(d => d is { HoldsScript: true, State.Exists: true }).Select(d => d.Path).ToList();
    }

    /// <summary>The model cache, for the tests.</summary>
    internal MissionModelCache Cache => cache;

    /// <summary>
    /// The current model of a script: W, the index, the problems, the prefab instances, the graph file and the version.
    /// </summary>
    /// <param name="script">The install path of the script, with <c>\</c> or <c>/</c>, in any letter case.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the edit view has no such mission
    /// script.</exception>
    public MissionModel Model(string script)
    {
        var document = Script(script);
        return ModelOf(document, document.State);
    }

    /// <summary>
    /// The mission errors of the save gate (section 6.10 of the M6b design): the errors of each script that the active
    /// mod changes. When the active mod removes or renames a name of the game data, also the errors of each other
    /// script, so a line of any layer that names such a name blocks the save (decision D81). The origin rule of D551
    /// makes each problem of a lower mod or of the base game a warning, so each error here is an error of the active
    /// mod.
    /// </summary>
    internal IEnumerable<Diagnostic> SaveErrors(string activeMod)
    {
        var scripts = ScriptPaths();
        if (scripts.Count == 0)
            return [];
        var removes = world.Inputs().BaseWorld is { } names && names.RemovesNamesOf(activeMod);
        return scripts.Where(p => removes || session.OpenDocument(p) is { } document && document.ChangesBase(document.State))
            .SelectMany(p => Model(p).Problems.Where(d => d.Severity == DiagnosticSeverity.Error)).ToList();
    }

    /// <summary>The model of a state of a script, from the cache or a new analysis.</summary>
    internal MissionModel ModelOf(EditDocument document, DocumentState state)
    {
        var snapshot = world.Inputs();
        cache.Use(snapshot);
        var path = document.Path;
        var analysis = cache.Find(path, state);
        if (analysis == null)
        {
            analysis = Analyze(snapshot, document, state);
            cache.Put(path, state, analysis);
        }
        return new MissionModel(path, Version(path), analysis)
        {
            GraphFile = GraphFileText(path),
            ReadOnlyReason = session.ReadOnlyReason ?? document.ReadOnlyReason,
        };
    }

    /// <summary>
    /// The snapshot of a full run of the mission checks for the worker of the editor: the names of the last snapshot,
    /// a new generation, and the overlay of W of each mission file that the session changed (section 7 of the M6b
    /// design).
    /// </summary>
    public MissionInputs RunInputs() =>
        (world.Inputs() with { Generation = session.NextMissionGeneration() }).WithOverlay(session.MissionOverlay());

    /// <summary>
    /// Takes the analyses of a full run into the model cache. A run result replaces the entries of the states that it
    /// used. A run of another snapshot of the names gives nothing.
    /// </summary>
    /// <param name="inputs">The inputs of the run, from <see cref="RunInputs"/>.</param>
    public void TakeRun(MissionInputs inputs, MissionRunResult result)
    {
        if (cache.Inputs == null || !ReferenceEquals(cache.Inputs.World, inputs.World))
            return;
        foreach (var (path, analysis) in result.Analyses)
        {
            if (inputs.Overlay.GetValueOrDefault(path)?.State is { } state)
                cache.Put(path, state, analysis);
            else if (session.OpenedDocument(path) is { } document && ReferenceEquals(document.State, document.Initial))
                cache.Put(path, document.State, analysis);
        }
    }

    /// <summary>The version of a script (section 4.2 of the M6b design). It starts at 1 and grows when the history
    /// changes W of the script: a commit, an undo, a redo or a restore.</summary>
    public long Version(string script) =>
        versions.GetValueOrDefault(VfsPath.Normalize(script) ?? script, 1);

    /// <summary>The document of a mission script of the edit view.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/> with the detail
    /// <see cref="MissionErrors.ScriptNotFound"/>.</exception>
    internal EditDocument Script(string script)
    {
        var path = InstallPaths.TryParse(script);
        if (path != null && session.OpenDocument(path) is { HoldsScript: true, State.Exists: true } document)
            return document;
        throw new EditException(MissionErrors.ScriptNotFoundError(path ?? script));
    }

    /// <summary>The analysis of a state of a script. A state that the session did not change since the open or the
    /// last save is the file of the edit view, so its raw text keeps the comments of the file.</summary>
    private MissionAnalysis Analyze(MissionInputs snapshot, EditDocument document, DocumentState state)
    {
        var path = document.Path;
        if (ReferenceEquals(state, document.Initial) && state.Exists &&
            MissionRun.Analyze(snapshot, MissionRun.Info(snapshot, path), cache.BaseRun) is { } saved)
            return saved;
        var overlay = new Dictionary<string, MissionOverlayFile>(session.MissionOverlay(), VfsPath.Comparer)
        {
            [path] = session.OverlayOf(document, state),
        };
        return MissionRun.AnalyzeScript(snapshot.WithOverlay(overlay), path, state.Working, document.IsBinary,
            cache.BaseRun);
    }

    /// <summary>The text of the graph file of a script in the active mod, or null.</summary>
    private string? GraphFileText(string path)
    {
        try
        {
            return session.ModFileText($@"graphs\{path}");
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
