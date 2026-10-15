using System;

namespace OpenLancer.Runtime.Thn;

/// <summary>
/// A cutscene that <see cref="CutsceneClock"/> steps. The editor's player wraps the engine <c>Cutscene</c>. A test
/// player wraps one <c>ThnScriptInstance</c> without a renderer.
/// </summary>
public interface ICutscenePlayer : IDisposable
{
    /// <summary>The script time of the scene, in seconds.</summary>
    double Time { get; }

    /// <summary>
    /// Advances the scene by one step: the events and the processors, then the scene objects and the world, as
    /// <c>Cutscene._Update</c> does (Cutscene.cs:398-432). A step draws nothing.
    /// </summary>
    void Step(double seconds);
}
