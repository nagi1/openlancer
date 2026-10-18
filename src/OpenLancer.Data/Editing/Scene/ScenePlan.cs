using System.Collections.Generic;
using OpenLancer.Data.Editing.Lists;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>A template of Create in the Scene (decisions D233 and D250 of the M3 design).</summary>
public enum SceneTemplate
{
    Station,
    Planet,
    Sun,
    Satellite,
    Solar,
    Zone,
    Light,
}

/// <summary>The action of a <see cref="ScenePlan"/>.</summary>
public enum SceneAction
{
    Create,
    Duplicate,
    Delete,
    Rename,
}

/// <summary>Why a section goes in a delete.</summary>
public enum DeleteReason
{
    /// <summary>The user deletes it.</summary>
    Target,

    /// <summary>It goes with a target, as a child goes with its parent in Unity.</summary>
    Cascade,
}

/// <summary>A section that a delete removes.</summary>
/// <param name="Section">The section.</param>
/// <param name="Children">The number of child sections that go with it, for example <c>[CollisionGroup]</c>
/// sections.</param>
/// <param name="Why">A target, or a section that goes with a target.</param>
/// <param name="Cause">For a section that goes with a target: the line that names the item that goes, for example
/// <c>parent = Li01_14</c>. Null for a target.</param>
public sealed record PlannedDelete(SectionRef Section, int Children, DeleteReason Why, string? Cause);

/// <summary>A safe fix of a line that names an item that goes (decision D81): the line or the lines of a hole go in the
/// same entry of the edit history.</summary>
/// <param name="Section">The section of the lines.</param>
/// <param name="Key">The key of the first line, for example <c>ring</c> or <c>exclusion</c>.</param>
/// <param name="Text">What the fix does, for example "Bw05_Crow_nebula.ini: the hole of Zone_Bw05_01_exclusion (6
/// lines)".</param>
public sealed record PlannedFix(SectionRef Section, string Key, string Text);

/// <summary>A line that keeps a name of an item that goes, and shows "Missing" after the delete.</summary>
/// <param name="Reference">The line.</param>
/// <param name="Consequence">What the game does with the line, or why the editor cannot fix it.</param>
/// <param name="BlocksSave">True when the rule of the line is closed, so the save stops until a fix.</param>
public sealed record MissingReference(ListReference Reference, string Consequence, bool BlocksSave);

/// <summary>A file that names a nickname as text (see <see cref="StoryMention"/>).</summary>
public sealed record NamedMention(string Nickname, string Path, int Count);

/// <summary>
/// The plan of a Create, a Duplicate, a Delete or a Rename in the Scene (milestone M3): what the action changes, what it
/// fixes, what it refuses and what it warns about, and the command that runs it as one entry of the edit history.
/// </summary>
/// <remarks>A plan reads the edit view when it is made. Its command runs only while the edit view and the text are as
/// the plan read them, so make the plan and run it at once.</remarks>
public sealed class ScenePlan
{
    internal ScenePlan(SceneAction action, string label)
    {
        Action = action;
        Label = label;
    }

    public SceneAction Action { get; }

    /// <summary>The label of the entry of the edit history, for example "Delete Li01_14 and 5 more".</summary>
    public string Label { get; internal set; }

    /// <summary>The sections that a Create or a Duplicate adds, in order.</summary>
    public IReadOnlyList<SectionRef> Adds { get; internal set; } = [];

    /// <summary>The sections that a Delete removes: the targets, then the sections that go with them.</summary>
    public IReadOnlyList<PlannedDelete> Deletes { get; internal set; } = [];

    /// <summary>The safe fixes of a Delete.</summary>
    public IReadOnlyList<PlannedFix> Fixes { get; internal set; } = [];

    /// <summary>The lines that show "Missing" after a Delete.</summary>
    public IReadOnlyList<MissingReference> Missing { get; internal set; } = [];

    /// <summary>Why the action cannot run. A plan with a refusal has no command.</summary>
    public IReadOnlyList<Diagnostic> Refusals { get; internal set; } = [];

    /// <summary>The warnings: the later mods, the bases without an object, the words of a zone nickname, the lines that
    /// a new name adopts, and the lines that the editor cannot change.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; internal set; } = [];

    /// <summary>The files that name the old nickname of a Rename, or a nickname of a Delete, as text.</summary>
    public IReadOnlyList<NamedMention> Mentions { get; internal set; } = [];

    /// <summary>The section to select after the action, or null to clear the selection (decision D249).</summary>
    public SectionRef? SelectAfter { get; internal set; }

    /// <summary>The command, or null when the plan has a refusal.</summary>
    public EditCommand? Command { get; internal set; }

    /// <summary>
    /// True when the editor asks before it runs the command (decisions D238 and D242): a Delete that removes more than
    /// the targets and their child sections, fixes a line, leaves "Missing" or has a warning or a mention, and a Rename
    /// with a warning or a mention. A Create and a Duplicate never ask, as in Unity.
    /// </summary>
    public bool NeedsConfirmation { get; internal set; }
}
