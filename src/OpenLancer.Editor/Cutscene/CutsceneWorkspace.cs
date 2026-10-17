using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Thn;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>The open cutscene as a selection: the Inspector then shows the cutscene page (section 7.5 of the M8
/// design).</summary>
/// <param name="Path">The install path of the cutscene, in normal form.</param>
internal sealed record CutsceneRoot(string Path);

/// <summary>
/// The open cutscene of the editor: its path, its preview context and plan, its timeline, and the clock of the
/// preview. One cutscene is open at a time (decision D67). A build reads the open script from W, so an unsaved edit
/// shows at once (section 8.1 of the M8 design).
/// </summary>
/// <remarks>The Hierarchy and the Inspector follow the shown tab of the center group (section 7.1): the Scene shows the
/// system and its selection, and the Cutscene tab shows the open cutscene and its selection. So each of the two keeps
/// its selection while the other one shows (see <see cref="Swap"/>).</remarks>
internal sealed partial class CutsceneWorkspace(EditorWindow window) : IDisposable
{
    /// <summary>The time budget of the catch-up in a frame while the user drags or types, and at other times (section
    /// 8.4).</summary>
    private static readonly TimeSpan GestureBudget = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan IdleBudget = TimeSpan.FromMilliseconds(40);

    /// <summary>The first viewport of a build, before the view gives its size.</summary>
    private static readonly Rectangle DefaultViewport = new(0, 0, 1280, 720);

    private EditorSession? session;
    private CutsceneClock? clock;

    // The scan of the INI files of the game data that the references came from.
    private (GameDataManager? Data, ThnReferences? References) scan;

    // A change of a gesture invalidated the clock, and the end of the gesture must build.
    private bool gesture;

    // The player that showed in the last frame, and the catch-up that shows the progress.
    private ICutscenePlayer? shown;
    private double catchUpWall;
    private double catchUpStart;

    // The timeline of W, made again when W or the context changes, and at the end of a gesture, which pinned the
    // sub-rows of the bars.
    private (ThnScriptModel? Working, ScriptPlay Play, TimelineModel? Model, bool Pinned) timeline;

    // The selection of the tab of the center group that does not show, and the tab that showed in the last frame.
    private object? parkedScene;
    private object? parkedCutscene;
    private bool cutsceneShown;

    // The sound of the preview (section 5.3 of the M8b design): the engine output, the scheduler, and the timeline
    // whose cues the scheduler has. A null timeline makes the next frame set the cues and start their decode.
    private readonly CutsceneSound sound = new(window);
    private TimelineAudio? audio;
    private TimelineModel? cueModel;

    /// <summary>The install path of the open cutscene, in normal form, or null.</summary>
    public string? Path { get; private set; }

    /// <summary>The preview context, or null when no cutscene is open.</summary>
    public PreviewContext? Context { get; private set; }

    /// <summary>The contexts that fit the open cutscene, the suggested context first.</summary>
    public IReadOnlyList<PreviewContext> Choices { get; private set; } = [];

    /// <summary>The plan of the context, or null when the plan failed.</summary>
    public PreviewPlan? Plan { get; private set; }

    /// <summary>Why the open cutscene has no preview, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>The Preview controls, or null without a preview.</summary>
    public PreviewTransport? Transport { get; private set; }

    /// <summary>The clock, or null without a preview.</summary>
    public CutsceneClock? Clock => clock;

    /// <summary>The player that the view draws, or null.</summary>
    public CutscenePlayer? Visible => clock?.Visible as CutscenePlayer;

    /// <summary>The steps of the players of the open cutscene.</summary>
    public PreviewMeter Meter { get; private set; } = new();

    /// <summary>The time of the last build that ended.</summary>
    public TimeSpan LastBuild { get; private set; }

    /// <summary>The builds of the open context.</summary>
    public int Builds { get; private set; }

    /// <summary>The THN camera right after the last build, before a step, or null when no scene object is the
    /// camera.</summary>
    public string? CameraAfterBuild { get; private set; }

    /// <summary>The time of the last scan of the references.</summary>
    public TimeSpan LastScan { get; private set; }

    /// <summary>The wall time that the catch-up still needs, from the rate of the catch-up so far, or null.</summary>
    public TimeSpan? CatchUpLeft { get; private set; }

    /// <summary>
    /// True when the shown frame comes from a catch-up and no play ran since. The particle effects then show their
    /// first state at the playhead (section 8.6).
    /// </summary>
    public bool EffectsStartAtPlayhead { get; private set; }

    /// <summary>The viewport of the last draw. The next build starts with it.</summary>
    public Rectangle Viewport { get; set; } = DefaultViewport;

    /// <summary>The duration of W, or 0 when the session cannot read the cutscene.</summary>
    public double Duration => Path != null && session?.Edits.Cutscene(Path) is { } working ? working.Duration : 0;

    /// <summary>The timeline of W in the context, or null when no cutscene is open or the session cannot read
    /// it. The sub-rows of the bars stay where they can, and they stay pinned while a gesture is open (decision
    /// D131).</summary>
    public TimelineModel? Timeline
    {
        get
        {
            if (Path == null || session?.Edits.Cutscene(Path) is not { } working)
            {
                return null;
            }

            var play = Plan?.PlayOf(Path) ?? ScriptPlay.Normal;
            var pin = window.IsEditing;
            if (!ReferenceEquals(timeline.Working, working) || timeline.Play != play || timeline.Model == null ||
                timeline.Pinned && !pin)
            {
                timeline = (working, play, TimelineModel.Build(Path, working, ThornEvaluator.Instance.Symbols, play,
                    timeline.Model, pin), pin);
            }

            return timeline.Model;
        }
    }

    /// <summary>The selected element of the open cutscene, or null.</summary>
    public ThnRef? Selected => window.Selection is ThnRef selected && VfsPath.Comparer.Equals(selected.Path, Path)
        ? selected
        : null;

    /// <summary>The selection of the open cutscene when nothing in it is selected, or null without a cutscene.</summary>
    public CutsceneRoot? Root => Path != null ? new CutsceneRoot(Path) : null;

    /// <summary>The wait of a catch-up, from the measured step rate (section 7.7).</summary>
    public string UpdatingText => CatchUpLeft is { } left
        ? $"Updating: about {Math.Ceiling(left.TotalSeconds):0} s left"
        : "Updating";

    /// <summary>
    /// Opens a cutscene of the edit view with the suggested context. Returns false when the session cannot read it.
    /// <see cref="Problem"/> then tells why.
    /// </summary>
    public bool Open(string path)
    {
        Close();
        session = window.Session;
        Path = VfsPath.Normalize(path) ?? path;
        if (session?.GameData is not { } data)
        {
            Problem = "The game data is not loaded.";
            return false;
        }

        if (session.Edits.Cutscene(Path) == null)
        {
            Problem = session.Edits.CutsceneReadOnlyReason(Path) ?? $"The edit view has no cutscene {Path}.";
            return false;
        }

        session.Edits.Changed += Changed;
        UseSound(data);
        Choices = MakeChoices(data);
        SetContext(Choices[0]);
        SelectRoot();
        // The line goes to the Console and to the log file, so that the bench records it too.
        var line = OpenedLine(Path, Duration, Context!.Label, Timeline?.Summary);
        window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource, line);
        FLLog.Info(EditorWindow.EditorSource, line);
        return true;
    }

    /// <summary>The line of an open: the path, the duration, the context and the counts of the Timeline. The numbers
    /// do not depend on the culture of the user.</summary>
    internal static string OpenedLine(string path, double duration, string context, string? timeline) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Opened cutscene {path} ({duration:0.###} s). Context: {context}. {timeline}").TrimEnd();

    /// <summary>
    /// Follows the shown tab of the center group: each of the Scene and the Cutscene tab gets back its own selection.
    /// A selection of the other kind that the user made in the meantime, for example a section in the Database while
    /// the Cutscene tab shows, stays. Call it once in each frame, before the panels draw.
    /// </summary>
    /// <param name="shown">True when the Cutscene tab is the shown tab of the center group.</param>
    public void FollowCenterTab(bool shown)
    {
        if (window.Selection is { } current && IsCutsceneItem(current) && !Equals(Valid(current), current))
        {
            // An undo removed the element or some events of a set, or another cutscene opened.
            window.Selection = Valid(current) ?? (shown ? Root : null);
        }

        if (shown == cutsceneShown)
        {
            return;
        }

        cutsceneShown = shown;
        (window.Selection, parkedScene, parkedCutscene) =
            Swap(shown, window.Selection, parkedScene, Valid(parkedCutscene) ?? Root);
    }

    /// <summary>
    /// The selections after the center group shows the other tab: the selection to show, and the selections of the
    /// Scene and of the Cutscene tab to keep.
    /// </summary>
    /// <param name="toCutscene">True when the Cutscene tab shows now.</param>
    /// <param name="current">The selection before the change.</param>
    /// <param name="scene">The kept selection of the Scene.</param>
    /// <param name="cutscene">The kept selection of the Cutscene tab, valid for the open cutscene, or null.</param>
    internal static (object? Selection, object? Scene, object? Cutscene) Swap(bool toCutscene, object? current,
        object? scene, object? cutscene)
    {
        if (toCutscene)
        {
            return IsCutsceneItem(current) ? (current, scene, cutscene) : (cutscene, current, cutscene);
        }

        return IsCutsceneItem(current) ? (scene, scene, current) : (current, scene, cutscene);
    }

    /// <summary>True for a selection that the Cutscene tab shows: an element, events or the root of a
    /// cutscene.</summary>
    internal static bool IsCutsceneItem(object? item) => item is ThnRef or ThnEventSet or CutsceneRoot;

    /// <summary>
    /// A cutscene selection when it is an element or the root of the open cutscene, else null. Of a set of events, the
    /// events that W still has: a set, one event, or null.
    /// </summary>
    public object? Valid(object? item) => item switch
    {
        CutsceneRoot root when VfsPath.Comparer.Equals(root.Path, Path) => root,
        ThnEventSet set when VfsPath.Comparer.Equals(set.Path, Path) && Timeline is { } model => Kept(set, model),
        ThnRef element when VfsPath.Comparer.Equals(element.Path, Path) && Timeline is { } model &&
                            (element.Kind == ThnElementKind.Event
                                ? model.Event(element.Id) != null
                                : model.KindOf(element.Id) != null) => element,
        _ => null,
    };

    /// <summary>The events of a set that W still has: the set itself, a smaller set, one event, or null.</summary>
    private static object? Kept(ThnEventSet set, TimelineModel model)
    {
        var kept = set.Events.Where(e => model.Event(e.Id) != null).Select(e => e.Id).ToList();
        return kept.Count == set.Events.Length ? set : ThnEventSet.Of(set.Path, kept);
    }

    /// <summary>The kind of a cutscene selection, or null for another item.</summary>
    public ObjectKind? KindOf(object? item) => item switch
    {
        CutsceneRoot => ObjectKinds.Thn.Script,
        ThnRef { Kind: ThnElementKind.Event } or ThnEventSet => ObjectKinds.Thn.Event,
        ThnRef element when Valid(element) != null => Timeline!.KindOf(element.Id),
        _ => null,
    };

    /// <summary>
    /// The title and the identifier of a cutscene selection for the header of the Inspector: the file name and the
    /// install path of a cutscene, the name of an entity, or the type and the time of an event and its first target.
    /// Null for another item.
    /// </summary>
    public (string Title, string Identifier)? Describe(object? item)
    {
        switch (item)
        {
            case CutsceneRoot root:
                return (System.IO.Path.GetFileNameWithoutExtension(root.Path), root.Path);
            case ThnEventSet set:
                return ($"{set.Events.Length} events", System.IO.Path.GetFileNameWithoutExtension(set.Path));
            case ThnRef { Kind: ThnElementKind.Event } element when Timeline?.Event(element.Id) is { } ev:
                return ($"{ev.Type} at {ev.Time.ToString("0.000", CultureInfo.InvariantCulture)} s",
                    ev.Targets.Count > 0 ? ev.Targets[0] : TimelineModel.NoTarget);
            case ThnRef { Kind: ThnElementKind.Entity } element when Element(element) is { } entity:
                var name = TimelineModel.EntityName(entity.Table);
                return (name, name);
            default:
                return null;
        }
    }

    /// <summary>The section of the edit view of the session that holds an INI line that names a cutscene, or
    /// null (see <see cref="SectionOf(EditSession, ThnReference)"/>).</summary>
    public SectionRef? SectionOf(ThnReference reference) =>
        window.Session?.Edits is { } edits ? SectionOf(edits, reference) : null;

    /// <summary>
    /// The section of W that holds an INI line that names a cutscene: the section at the place of the line when it has
    /// the type, the key and the value of the line, or else the first section that has them. So a click opens the
    /// right one of two sections with the same line, and a place that an edit of the file made stale still finds the
    /// line. Null when no section has them, for example after an edit that removed the line.
    /// </summary>
    internal static SectionRef? SectionOf(EditSession edits, ThnReference reference)
    {
        var sections = edits.Sections(reference.File);
        bool Holds(SectionRef section) =>
            section.Id.Type.Equals(reference.Section, StringComparison.OrdinalIgnoreCase) &&
            edits.Get(section) is { } view && view.Lines(reference.Key)
                .Any(l => l.Values.Any(v => string.Equals(v.ToString(), reference.Value, StringComparison.Ordinal)));
        return reference.SectionIndex < sections.Count && Holds(sections[reference.SectionIndex])
            ? sections[reference.SectionIndex]
            : sections.FirstOrDefault(Holds);
    }

    /// <summary>The element of W that a selection names, or null.</summary>
    public ThnElement? Element(ThnRef element) =>
        Valid(element) != null && session?.Edits.Cutscene(Path!) is { } working
            ? working.Elements(element.Kind).Find(e => e.Id == element.Id)
            : null;

    /// <summary>
    /// The keys of the preview (section 7.7) while the Timeline or the Cutscene panel has the focus: Space, Home, End,
    /// and Left and Right with or without Shift.
    /// </summary>
    public void Keys(bool focused)
    {
        if (Transport is not { } transport)
        {
            return;
        }

        var io = ImGui.GetIO();
        switch (PreviewKeys.Read(focused, io.WantTextInput, k => ImGui.IsKeyPressed(k), io.KeyShift))
        {
            case PreviewKey.PlayPause:
                transport.TogglePlay();
                break;
            case PreviewKey.Start:
                transport.Seek(0);
                break;
            case PreviewKey.End:
                transport.Seek(transport.End);
                break;
            case PreviewKey.FrameBack:
                transport.StepFrames(-1);
                break;
            case PreviewKey.FrameForward:
                transport.StepFrames(1);
                break;
            case PreviewKey.SecondBack:
                transport.StepSeconds(-1);
                break;
            case PreviewKey.SecondForward:
                transport.StepSeconds(1);
                break;
        }
    }

    /// <summary>Selects the open cutscene in the Cutscene tab: at once when it shows, else when it shows next.</summary>
    private void SelectRoot() => Select(Root!);

    /// <summary>Selects an item of the open cutscene in the Cutscene tab: at once when it shows, else when it shows
    /// next. The Scene keeps its selection.</summary>
    private void Select(object item)
    {
        if (cutsceneShown || IsCutsceneItem(window.Selection))
        {
            window.Selection = item;
        }

        parkedCutscene = item;
    }

    /// <summary>Opens a cutscene, and shows it in the Cutscene tab and in the Timeline. When it does not open, the
    /// Console tells why.</summary>
    public bool OpenAndShow(string path)
    {
        if (!Open(path))
        {
            window.Log.Add(ConsoleSeverity.Warning, EditorWindow.EditorSource, $"The cutscene {path} did not open: {Problem}");
            return false;
        }

        ShowPanels();
        return true;
    }

    /// <summary>
    /// Shows the place of a problem (section 9 of the M8 design): opens the cutscene when it is not the open one, shows
    /// it, and selects the element (see <see cref="SelectionAt"/>).
    /// </summary>
    public void Show(ThnLocation place)
    {
        if (ToOpen(Path, place) is { } path)
        {
            if (!OpenAndShow(path))
            {
                return;
            }
        }
        else
        {
            ShowPanels();
        }

        Select(SelectionAt(Path!, session?.Edits.Cutscene(Path!), place));
    }

    /// <summary>The cutscene that a click on a problem opens, in normal form, or null when the problem is about the
    /// open cutscene.</summary>
    /// <param name="open">The install path of the open cutscene, in normal form, or null.</param>
    internal static string? ToOpen(string? open, ThnLocation place)
    {
        var path = VfsPath.Normalize(place.Path) ?? place.Path;
        return VfsPath.Comparer.Equals(path, open) ? null : path;
    }

    /// <summary>
    /// The selection that shows the place of a problem in the open cutscene: the element of W that the place names (see
    /// <see cref="ThnLocation.ElementIn"/>), or else the cutscene. An edit after the save can delete the element, or
    /// make its key name no element or more than one element.
    /// </summary>
    /// <param name="path">The install path of the open cutscene, in normal form.</param>
    /// <param name="working">W of the open cutscene, or null when the session cannot read it.</param>
    internal static object SelectionAt(string path, ThnScriptModel? working, ThnLocation place) =>
        working != null && place.Kind is { } kind && place.ElementIn(working) is { } element
            ? new ThnRef(path, kind, element.Id)
            : new CutsceneRoot(path);

    private void ShowPanels()
    {
        window.ShowPanel(CutsceneView.PanelId);
        window.ShowPanel(TimelinePanel.PanelId);
    }

    /// <summary>Previews the open cutscene in another context. The playhead and the Preview controls stay.</summary>
    public void SetContext(PreviewContext context)
    {
        if (Path == null || session?.GameData is not { } data)
        {
            return;
        }

        var previous = Transport;
        DropPreview();
        // The context decides which events run, so the cues change.
        audio?.StopAll();
        cueModel = null;
        Context = context;
        try
        {
            Plan = context.Plan(Path, data.Items, References(), session.Edits.Cutscene(Path));
        }
        catch (ArgumentException e)
        {
            Problem = ErrorText.Of(e);
            return;
        }

        clock = new CutsceneClock(Build);
        Transport = new PreviewTransport(clock, () => Duration);
        if (previous != null)
        {
            (Transport.Speed, Transport.Loop, Transport.PreRoll, Transport.LoopRange) =
                (previous.Speed, previous.Loop, previous.PreRoll, previous.LoopRange);
            Transport.Seek(previous.Time);
        }
    }

    /// <summary>
    /// Runs the clock for one editor frame, then the sound of the preview. While the game data loads again, nothing
    /// builds or steps, because a build loads models with the resources of the session, and no sound plays.
    /// </summary>
    public void Frame(double elapsed)
    {
        if (Transport == null || clock == null || session is not { GameData: not null, IsBusy: false })
        {
            // Else the sound plays on while the picture waits for the reload after a save.
            audio?.StopAll();
            return;
        }

        if (gesture && !window.IsEditing)
        {
            gesture = false;
            clock.EndGesture();
        }

        if (Transport.Playing)
        {
            EffectsStartAtPlayhead = false;
        }

        Transport.Frame(elapsed, window.IsEditing ? GestureBudget : IdleBudget);
        if (!ReferenceEquals(shown, clock.Visible))
        {
            shown = clock.Visible;
            EffectsStartAtPlayhead = shown is { Time: > 0 } && !Transport.Playing;
        }

        Progress(elapsed);
        Sound();
    }

    /// <summary>
    /// A frame in which the preview does not run, because neither the Cutscene tab nor the Timeline shows. The clock
    /// waits, so the sound stops too. Else it plays on without the picture. The next frame that runs starts the active
    /// sounds again at the playhead.
    /// </summary>
    public void Wait() => audio?.StopAll();

    /// <summary>
    /// The sound of one frame (decision D132): the cues of the timeline, and a play that goes on or starts again. The
    /// sound plays only while the preview plays forward: not in the pre-roll, not during a catch-up and not during a
    /// drag of the playhead, and only while the Sound toggle is on.
    /// </summary>
    private void Sound()
    {
        audio ??= new TimelineAudio(sound);
        var model = Timeline;
        if (!ReferenceEquals(model, cueModel))
        {
            var first = cueModel == null;
            cueModel = model;
            var cues = model != null && session?.Edits.Cutscene(Path!) is { } working
                ? TimelineAudio.Cues(model, working)
                : [];
            audio.SetCues(cues);
            if (first)
            {
                sound.Preload(cues);
            }
        }

        audio.Frame(Transport!.Time, Transport.Jumps, Transport.PlaysSound && window.Settings.PreviewSound,
            Transport.Speed);
    }

    /// <summary>Plays the sound of a SOUND entity of the open cutscene once from its start, for the Preview row of the
    /// Inspector. Returns false when the entity has no sound to play.</summary>
    public bool PlaySound(ThnRef entity)
    {
        if (Element(entity) is not { } element || TimelineAudio.CueOf(element.Table, element.Id) is not { } cue)
        {
            return false;
        }

        sound.PlayOnce(cue);
        return true;
    }

    /// <summary>Stops the sound of the Preview row of the Inspector.</summary>
    public void StopSound() => sound.StopSingle();

    /// <summary>The sound uses a load of the game data. A new load gets a new sound manager, so the next play reads the
    /// sounds of the new data.</summary>
    private void UseSound(GameDataManager data)
    {
        audio?.StopAll();
        sound.Use(data);
        sound.Scene = name => Visible?.VoiceOf(name);
        cueModel = null;
    }

    /// <summary>
    /// The game data loaded again after a save. The sound gets a new sound manager, the contexts that fit the cutscene
    /// come again from the INI files, the plan reads the new data, and the clock builds again. So the room of a room
    /// line that the save wrote is a choice now. The chosen context stays when it is still a choice.
    /// </summary>
    public void DataReplaced()
    {
        if (Path == null || window.Session?.GameData is not { } data)
        {
            return;
        }

        UseSound(data);
        if (Context != null)
        {
            Choices = MakeChoices(data);
            SetContext(ContextAfterReload(Choices, Context));
        }
    }

    /// <summary>The context after a reload of the game data: the chosen context when the new choices still have it,
    /// else the first choice.</summary>
    internal static PreviewContext ContextAfterReload(IReadOnlyList<PreviewContext> choices, PreviewContext chosen) =>
        choices.FirstOrDefault(c => PreviewContext.Same(c, chosen)) ?? choices[0];

    /// <summary>The contexts that fit the open cutscene in the game data, with the ship package of Play.</summary>
    private List<PreviewContext> MakeChoices(GameDataManager data)
    {
        var package = window.Settings.PlayPackageOf(session!.Profile.FilePath);
        return PreviewContext.Choices(Path!, References(), data.Items).Select(c => c with { ShipPackage = package })
            .ToList();
    }

    /// <summary>
    /// The references of the INI files of the loaded game data. The scan runs once for each load of the data, because
    /// the INI files change only with a save, and a save loads the data again.
    /// </summary>
    public ThnReferences References()
    {
        var data = window.Session?.GameData ?? throw new InvalidOperationException("The game data is not loaded.");
        if (!ReferenceEquals(scan.Data, data) || scan.References == null)
        {
            var start = Stopwatch.GetTimestamp();
            var references = ThnReferences.Scan(data.Items.VFS);
            LastScan = Stopwatch.GetElapsedTime(start);
            foreach (var problem in references.Problems)
            {
                window.Log.Add(problem);
            }

            scan = (data, references);
        }

        return scan.References;
    }

    /// <summary>Closes the open cutscene.</summary>
    public void Close()
    {
        if (session != null)
        {
            session.Edits.Changed -= Changed;
        }

        DropPreview();
        audio?.StopAll();
        sound.StopSingle();
        cueModel = null;
        Context = null;
        Path = null;
        session = null;
        Choices = [];
        timeline = default;
    }

    /// <summary>Closes the cutscene and forgets the references, the kept selections and the sound manager of the game
    /// data, for example when the profile closes.</summary>
    public void Reset()
    {
        Close();
        sound.Use(null);
        scan = default;
        (parkedScene, parkedCutscene, cutsceneShown) = (null, null, false);
    }

    /// <summary>Closes the cutscene and frees the audio of the window.</summary>
    public void Dispose()
    {
        Reset();
        sound.Dispose();
    }

    /// <summary>Disposes the clock and forgets the preview of the context.</summary>
    private void DropPreview()
    {
        clock?.Dispose();
        clock = null;
        Transport = null;
        Plan = null;
        Problem = null;
        Meter = new PreviewMeter();
        LastBuild = default;
        Builds = 0;
        CameraAfterBuild = null;
        shown = null;
        CatchUpLeft = null;
        catchUpWall = 0;
        EffectsStartAtPlayhead = false;
        gesture = false;
    }

    /// <summary>
    /// Builds a player at script time 0 from the current W, context and game data. The clock calls it.
    /// </summary>
    private ICutscenePlayer Build()
    {
        var data = session!.GameData!;
        var start = Stopwatch.GetTimestamp();
        var player = CutscenePlayer.Build(Plan!, Script, data, session.Resources, window, Viewport, Meter);
        LastBuild = Stopwatch.GetElapsedTime(start);
        CameraAfterBuild = player.CameraName;
        Builds++;
        return player;
    }

    /// <summary>
    /// The script of an install path: the text that the writer makes from W for the open cutscene and for each other
    /// open cutscene of the session, else the bytes of the edit view. So no build reads the open file on disk.
    /// </summary>
    private ThnScript Script(string path)
    {
        var edits = session!.Edits;
        var items = session.GameData!.Items;
        byte[] bytes;
        if (VfsPath.Comparer.Equals(path, Path) || edits.OpenCutscenes.Contains(path, VfsPath.Comparer))
        {
            var working = edits.Cutscene(path) ?? throw new InvalidOperationException(
                edits.CutsceneReadOnlyReason(path) ?? $"No cutscene {path}.");
            bytes = Encoding.ASCII.GetBytes(ThnWriter.Write(working.ToGlobals()));
        }
        else
        {
            bytes = items.VFS.ReadAllBytes(path);
        }

        return new ThnScript(bytes, items.ThornReadCallback, path);
    }

    /// <summary>
    /// An edit, an undo or a redo changed W. A change of a script of the plan builds the scene again. A change while a
    /// field edit or a drag is open is a step of a gesture, which builds once at its end (section 8.4).
    /// </summary>
    private void Changed(EditChange change)
    {
        // An undo of New cutscene takes the open cutscene away, so the clock cannot build it.
        if (Path != null && change.Cutscenes.Contains(Path, VfsPath.Comparer) && session?.Edits.Cutscene(Path) == null)
        {
            window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource,
                $"Closed cutscene {Path}, because it does not exist now.");
            Close();
            return;
        }

        if (clock == null || Plan == null || !change.Cutscenes.Any(UsedByPlan))
        {
            return;
        }

        gesture = window.IsEditing;
        clock.Invalidate(gesture);
    }

    private bool UsedByPlan(string path) =>
        VfsPath.Comparer.Equals(path, Path) || VfsPath.Comparer.Equals(path, Plan!.SetScript) ||
        VfsPath.Comparer.Equals(path, Plan.Fidget) || Plan.Scripts.Contains(path, VfsPath.Comparer);

    /// <summary>The wall time that the catch-up still needs, from its rate since it started.</summary>
    private void Progress(double elapsed)
    {
        if (clock is not { CatchingUp: true })
        {
            CatchUpLeft = null;
            catchUpWall = 0;
            return;
        }

        if (catchUpWall == 0)
        {
            catchUpStart = clock.CatchUpTime;
        }

        catchUpWall += elapsed;
        var rate = (clock.CatchUpTime - catchUpStart) / catchUpWall;
        CatchUpLeft = rate > 0 ? TimeSpan.FromSeconds(Math.Max(0, clock.Target - clock.CatchUpTime) / rate) : null;
    }
}
