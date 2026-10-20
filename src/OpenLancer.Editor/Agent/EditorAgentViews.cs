using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Agent;

/// <summary>
/// The views of the editor for the view tools of the agent server (section 7.7 of the MCP1 design): the open of a system,
/// a mission or a cutscene, the selection, and the images of the render service. Each member runs on the UI thread, in
/// the session part of a call. The tools act as the user acts in the editor, so a selection follows the rules of the
/// center tabs.
/// </summary>
internal sealed class EditorAgentViews(EditorWindow window) : IAgentViews
{
    public void Open(ViewKind kind, string target)
    {
        switch (kind)
        {
            case ViewKind.System:
                OpenSystem(target);
                window.ShowPanel(SceneView.PanelId);
                break;
            case ViewKind.Mission:
                window.Missions.OpenMission(target, show: true);
                break;
            default:
                OpenCutscene(target);
                break;
        }
    }

    /// <summary>
    /// Selects an item as a click of the user does. An element of a cutscene shows in the Cutscene tab, a trigger or a
    /// line of a mission in the Mission tab, and an item of a system in the Scene. With <paramref name="frame"/>, the
    /// view moves to the item and its tab shows. An item of another system then opens that system first. Without it,
    /// the camera and the tab stay. Each other section shows in the Inspector.
    /// </summary>
    public SelectionView Select(SelectTarget target, bool frame)
    {
        if (target.Element is { } element)
        {
            OpenCutscene(element.Path);
            window.SelectInTab(CenterMode.Cutscene, element);
            var framed = frame && element.Kind == ThnElementKind.Entity;
            if (framed)
                window.FrameInCutscene(element);
            return new SelectionView(target.Ref, SelectionPlace.Cutscene, framed);
        }

        if (target.Cutscene is { } path)
        {
            OpenCutscene(path);
            window.SelectInTab(CenterMode.Cutscene, window.Cutscenes.Root!);
            return new SelectionView(target.Ref, SelectionPlace.Cutscene, false);
        }

        if (target.Script is { } script)
        {
            window.Missions.OpenMission(script, show: true);
            return new SelectionView(target.Ref, SelectionPlace.Mission, false);
        }

        var section = target.Section!;
        // A section of a mission script opens in the Mission tab, which frames it in the graph (section 12.4 of the M6a
        // design).
        if (window.Missions.GoTo(section, target.Row ?? -1))
            return new SelectionView(target.Ref, SelectionPlace.Mission, true);
        if (target.System is { } system && (frame || IsOpen(system)))
        {
            if (frame)
                OpenSystem(system);
            if (window.Scene.ItemOf(section) is { } item and not StarSystem)
            {
                window.SelectInTab(CenterMode.Scene, item);
                if (frame)
                {
                    window.ShowPanel(SceneView.PanelId);
                    window.Scene.Focus(item);
                }

                return new SelectionView(target.Ref, SelectionPlace.Scene, frame);
            }
        }

        window.Selection = section;
        return new SelectionView(target.Ref, SelectionPlace.Inspector, false);
    }

    public Func<CancellationToken, Task<(SceneShotView View, ToolImage Image)>> RenderScene(SceneShotRequest request)
    {
        _ = Data();
        var scene = window.Scene;
        var open = scene.System ?? throw Refused("NoSystem", AgentTexts.Error("render_no_system"));
        if (!open.Nickname.Equals(request.System, StringComparison.OrdinalIgnoreCase))
        {
            throw Refused("SystemNotOpen", AgentTexts.Error("render_system_not_open", ("open", open.Nickname),
                ("system", request.System)));
        }

        var (width, height) = (request.Width, request.Height);
        var camera = request.Camera ?? ShotCamera.Frame(FrameSpheres(scene, request), width / (float)height);
        var image = window.AgentRender.RenderScene(camera, width, height, request.Zones);
        var session = window.Session!.Edits;
        var (items, total) = camera.Visible(scene.ShotItems(request.Zones), width, height, request.MaxVisible);
        var rows = items
            .Select(i => scene.SectionOf(i.Item) is { } section ? VisibleRow.Of(ItemRefs.Canonical(session, section), i.Point) : null)
            .OfType<VisibleRow>()
            .ToList();
        var view = new SceneShotView(ItemRefs.Format(new ItemReference(ItemRefKind.System, open.Nickname)), width, height,
            ShotView.Of(camera), request.Zones, rows, total);
        return async cancel => (view, await image.WaitAsync(cancel).ConfigureAwait(false));
    }

    public Func<CancellationToken, Task<(CutsceneShotView View, ToolImage Image)>> RenderCutscene(
        CutsceneShotRequest request)
    {
        _ = Data();
        CutsceneShotPlan plan;
        try
        {
            plan = window.Cutscenes.ShotPlan(request.Path);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw Refused("NoPreview", AgentTexts.Error("render_no_preview", ("cutscene", request.Path),
                ("reason", ErrorText.Of(e))));
        }

        var job = window.AgentRender.RenderCutscene(plan, request.Time, request.Camera, request.Width, request.Height);
        var reference = CutsceneQueries.Ref(plan.Path);
        var context = plan.Context.Label;
        var withoutSystem = plan.Plan.System != null;
        return async cancel =>
        {
            using var registration = cancel.Register(job.Cancel);
            var done = await job.Result.WaitAsync(cancel).ConfigureAwait(false);
            return (new CutsceneShotView(reference, (float)Math.Round(done.Time, 4), done.Camera, request.Width,
                request.Height, context, withoutSystem), done.Image);
        };
    }

    public Func<CancellationToken, Task<(PanelShotView View, ToolImage Image)>> CapturePanel(AgentPanel panel)
    {
        var id = PanelIdOf(panel);
        if (!window.IsPanelShown(id))
            throw Refused("PanelHidden", AgentTexts.Error("capture_hidden", ("panel", id)));
        var image = window.AgentRender.Capture(id);
        return async cancel =>
        {
            var png = await image.WaitAsync(cancel).ConfigureAwait(false);
            return (new PanelShotView(panel, png.Width, png.Height), png);
        };
    }

    /// <summary>The id of the panel of the layout that a panel of <c>capture_panel</c> names. The names of the tool are
    /// the ids of the layout.</summary>
    internal static string PanelIdOf(AgentPanel panel) => panel switch
    {
        AgentPanel.Scene => SceneView.PanelId,
        AgentPanel.Mission => MissionWorkspace.PanelId,
        AgentPanel.Timeline => TimelinePanel.PanelId,
        AgentPanel.Inspector => InspectorPanel.PanelId,
        AgentPanel.Hierarchy => HierarchyPanel.PanelId,
        _ => ConsolePanel.PanelId,
    };

    /// <summary>
    /// The spheres that an image frames: the sphere of each item of the frame, and each object of the system for a
    /// <c>system:</c> reference or an empty frame.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the Scene does not show an item of the
    /// frame.</exception>
    private static List<(Vector3, float)> FrameSpheres(SceneView scene, SceneShotRequest request)
    {
        var spheres = new List<(Vector3, float)>();
        if (request.Frame.Count == 0 || request.Frame.Any(f => f.Section == null))
            spheres.AddRange(scene.ShotItems(false).Select(i => (i.Center, i.Radius)));
        foreach (var frame in request.Frame.Where(f => f.Section != null))
        {
            var sphere = scene.ItemOf(frame.Section!) is { } item ? scene.SphereOf(item) : null;
            spheres.Add(sphere ?? throw new EditException(new EditError(EditErrorKind.NotFound, "NotInScene",
                AgentTexts.Error("render_frame_none", ("system", request.System), ("ref", frame.Ref)))
            {
                Field = "frame",
                Target = frame.Ref,
            }));
        }

        return spheres;
    }

    /// <summary>Opens a system of the loaded game data in the Scene. The open system stays as it is.</summary>
    private void OpenSystem(string nickname)
    {
        var data = Data();
        if (IsOpen(nickname))
            return;
        var system = data.Items.Systems.Get(nickname) ??
                     throw Refused("NotLoaded", AgentTexts.Error("view_not_loaded", ("system", nickname)));
        window.OpenSystem(system);
    }

    private bool IsOpen(string system) => window.Scene.System?.Nickname.Equals(system, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Opens a cutscene in the Cutscene tab and shows the tab and the Timeline. The open cutscene stays as it is,
    /// so its preview keeps its playhead.</summary>
    private void OpenCutscene(string path)
    {
        if (VfsPath.Comparer.Equals(window.Cutscenes.Path, path))
        {
            window.ShowPanel(Cutscene.CutsceneView.PanelId);
            window.ShowPanel(TimelinePanel.PanelId);
            return;
        }

        if (!window.Cutscenes.OpenAndShow(path))
        {
            throw Refused("NoCutscene", AgentTexts.Error("view_cutscene_failed", ("cutscene", path),
                ("reason", window.Cutscenes.Problem ?? "")));
        }
    }

    /// <summary>The loaded game data of the open profile.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/> while no game data is loaded.</exception>
    private GameDataManager Data() => window.Session?.GameData ??
                                      throw Refused("NoGameData", AgentTexts.Error("render_no_game_data"));

    private static EditException Refused(string detail, string message) =>
        new(new EditError(EditErrorKind.Refused, detail, message));
}
