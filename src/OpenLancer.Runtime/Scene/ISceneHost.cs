using LibreLancer.Data.GameData.World;
using OpenLancer.Data;

namespace OpenLancer.Runtime.Scene;

/// <summary>
/// The renderer side of the effects of <see cref="SceneApplier"/>: the parts of the scene that need the GPU or the
/// editor. A headless test uses a fake.
/// </summary>
public interface ISceneHost
{
    /// <summary>False while the game data loads again after a save. A model load then is not allowed, because the load
    /// makes the material map again (review finding F5 of M1).</summary>
    bool CanLoadModels { get; }

    /// <summary>True while a field edit of the Inspector or a drag of the gizmo is open.</summary>
    bool EditOpen { get; }

    /// <summary>A light of the system changed. The renderer holds a copy of each light, so it loads the lights
    /// again.</summary>
    void LightsChanged(StarSystem system);

    /// <summary>A zone moved, or changed its rotation, shape or size. The renderer lets each model find its nebula
    /// again (its zone version).</summary>
    void ZonesMoved();

    /// <summary>The zone of an asteroid field or a nebula changed. The renderer builds the fields and the nebulae again,
    /// then lets each model find its nebula again.</summary>
    void VolumesChanged(StarSystem system);

    /// <summary>The starsphere models of the system changed. The renderer loads them again.</summary>
    void StarspheresChanged(StarSystem system);

    /// <summary>
    /// The data that the index of the scene reads changed. <see cref="SceneEffect.Label"/>: a name changed, so the
    /// labels and the Hierarchy read the names again. <see cref="SceneEffect.Roles"/> or <see cref="SceneEffect.Lanes"/>:
    /// a zone role, a lane chain or a dock kind changed, so the editor builds the index of the system again.
    /// </summary>
    void IndexChanged(SceneEffect effects);

    /// <summary>A new problem of a section, for example a missing archetype. The editor shows it in the Console. The
    /// applier tells each problem once, when no edit is open, so a field edit gives no line for each
    /// keystroke.</summary>
    void ProblemFound(Diagnostic problem);
}
