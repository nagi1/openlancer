using System;
using System.Collections.Generic;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.World;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Editor.Scene;

/// <summary>
/// Keeps the open scene equal to W: it gives each change of the edit session to a <see cref="SceneApplier"/>, and it is
/// the renderer side of the applier. The scene view runs the applier at two points of each frame (see
/// <see cref="Flush"/>).
/// </summary>
internal sealed class SceneSync : ISceneHost, IDisposable
{
    private readonly EditorWindow window;
    private readonly EditorSession session;
    private readonly SystemRenderer renderer;
    private readonly Action<SceneEffect> indexChanged;
    private readonly SceneApplier applier;

    /// <summary>Starts the sync of a system that the world just loaded, and catches up the unsaved edits of W.</summary>
    /// <param name="items">The game data that <paramref name="system"/> came from.</param>
    /// <param name="system">The clone of the system that the scene shows.</param>
    /// <param name="indexChanged">Builds the index of the scene again, or reads the names again (see
    /// <see cref="ISceneHost.IndexChanged"/>).</param>
    /// <param name="objects">The world object of each object of the system. The applier adds and removes entries for new
    /// and deleted objects, so the views that share the map see them.</param>
    public SceneSync(EditorWindow window, EditorSession session, GameItemDb items, ResourceManager resources,
        StarSystem system, GameWorld world, SystemRenderer renderer, IDictionary<SystemObject, GameObject> objects,
        Action<SceneEffect> indexChanged)
    {
        this.window = window;
        this.session = session;
        this.renderer = renderer;
        this.indexChanged = indexChanged;
        applier = new SceneApplier(session.Edits, items, resources, system, world, objects, this);
        session.Edits.Changed += applier.Note;
        session.Texts.SceneSections = applier.SectionOf;
        CatchUp();
    }

    /// <summary>
    /// Applies the changes of W since the last flush. The scene view calls it at the start of its frame (flush A) and
    /// just before the world update of the renderer (flush B). At both points no worker task of an asteroid field reads
    /// the zones, and flush B shows a change of the frame, for example a step of a drag, in the same frame (section 3 of
    /// the design).
    /// </summary>
    public void Flush() => applier.Run();

    /// <summary>True while W has changes or effects that the scene did not apply yet (see
    /// <see cref="SceneApplier.HasPending"/>).</summary>
    public bool IsBehind => applier.HasPending;

    /// <summary>True when a section of the system waits for the reload of the game data (see
    /// <see cref="SceneApplier.WaitsForReload"/>).</summary>
    public bool WaitsForReload => applier.WaitsForReload;

    /// <summary>The section of W that a scene item shows, or null (see <see cref="SceneApplier.SectionOf"/>).</summary>
    public SectionRef? SectionOf(object item) => applier.SectionOf(item);

    /// <summary>The scene item of a section of W, or null (see <see cref="SceneApplier.ItemOf"/>).</summary>
    public object? ItemOf(SectionRef section) => applier.ItemOf(section);

    /// <summary>Why the scene does not show the W state of a section, for example a missing archetype. Empty when it
    /// shows it.</summary>
    public IReadOnlyList<Diagnostic> Problems(SectionRef section) => applier.Problems(section);

    public bool CanLoadModels => !session.IsBusy;

    public bool EditOpen => window.IsEditing;

    public void LightsChanged(StarSystem system) => renderer.LoadLights(system);

    // A model finds its nebula again only when the zone version changes (ModelRenderer.cs:77-82).
    public void ZonesMoved() => renderer.ZoneVersion++;

    public void VolumesChanged(StarSystem system)
    {
        renderer.LoadZones(system.AsteroidFields, system.Nebulae);
        renderer.ZoneVersion++;
    }

    public void StarspheresChanged(StarSystem system) => renderer.LoadStarspheres(system);

    public void IndexChanged(SceneEffect effects) => indexChanged(effects);

    public void ProblemFound(Diagnostic problem) => window.Log.Add(problem);

    /// <summary>Stops the sync. The names of the scene items then come from the game data again.</summary>
    public void Dispose()
    {
        session.Edits.Changed -= applier.Note;
        session.Texts.SceneSections = null;
    }

    /// <summary>
    /// The catch-up at the open. A model that loads again can log the lines that the Console showed at an earlier open
    /// of the system, so the Console skips the engine lines of the catch-up that it showed (section 13 of the design).
    /// The lines of the system load show as before.
    /// </summary>
    private void CatchUp() =>
        window.Log.SkipKnownEngineLinesOf(() => applier.CatchUp(), window, () => window.Session?.IsReloading == true);
}
