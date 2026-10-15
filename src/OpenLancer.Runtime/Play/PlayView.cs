using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using OpenLancer.Data;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// The data that a play loads (decision D38): the base data with the mods through the active mod. This is the edit
/// view of the editor, and the view that <c>oltool validate --active</c> checks.
/// </summary>
/// <remarks>
/// The editor can save a mod while the play reads it (decision D51). A save changes the files of a mod only while its
/// journal exists, from its commit point to its end (see <see cref="Mod.HasPendingSave"/>). A save completes in
/// milliseconds. So the build waits until no mod of the view has a journal. Then it loads the mods again, because
/// <see cref="ModProfile.Load"/> read their file lists and their text before that wait. Then it builds and checks
/// again. When the check after the build found a journal, the build can have parts of that save, so it loads and
/// builds again. A journal that stays for <see cref="SaveWait"/> is an error. A save that starts and ends between two
/// checks leaves no journal. The editor knows its own saves and covers that case.
/// </remarks>
public static class PlayView
{
    /// <summary>The longest time that a journal of a mod can stay before the build stops.</summary>
    public static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    /// <param name="activeModId">The active mod, or null for all mods of the profile.</param>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    /// <exception cref="ModLoadException">The journal of a save of a mod of the view stays for
    /// <see cref="SaveWait"/>, or a mod that loads again is not valid.</exception>
    public static ModOverlay Build(ModProfile profile, string? activeModId) =>
        Build(profile, activeModId, DllStringResourceWriter.Instance);

    /// <param name="stringWriter">Writes the text DLL. The tests give a writer that saves a mod during the
    /// build.</param>
    internal static ModOverlay Build(ModProfile profile, string? activeModId, IStringResourceWriter stringWriter)
    {
        var mods = profile.ModsThrough(activeModId);
        WaitForSaves(mods);
        while (true)
        {
            mods = mods.Select(m => Mod.Load(m.Folder)).ToList();
            var overlay = ModOverlay.Build(profile.BaseFolder, mods, stringWriter, ThornEvaluator.Instance);
            if (!WaitForSaves(mods))
                return overlay;
        }
    }

    /// <summary>
    /// The reason why a play cannot use the view: the merge errors of its mods, or null when it has none. The start and
    /// the reload of a play refuse such a view.
    /// </summary>
    public static string? MergeErrors(ModOverlay view) => view.HasErrors
        ? "The mods of the play have merge errors:" + string.Concat(view.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"\n{d}"))
        : null;

    /// <summary>Waits until no mod has a pending save.</summary>
    /// <returns>True when a mod had a pending save.</returns>
    /// <exception cref="ModLoadException">A pending save stayed for <see cref="SaveWait"/>.</exception>
    private static bool WaitForSaves(IReadOnlyList<Mod> mods)
    {
        var clock = Stopwatch.StartNew();
        var found = false;
        while (mods.FirstOrDefault(m => m.HasPendingSave) is { } pending)
        {
            if (clock.Elapsed >= SaveWait)
                throw new ModLoadException(pending.Folder, $"A save of the editor in mod '{pending.Id}' is not " +
                                                           "complete. Open the profile in the editor to complete the save.");
            found = true;
            Thread.Sleep(Poll);
        }
        return found;
    }
}
