using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// Two or more events of one cutscene, selected together: with Shift and a click, or with a box on the Timeline. The
/// Inspector shows "N events selected" with a time shift and Delete (section 7.5 of the M8 design).
/// </summary>
/// <remarks>Two sets are equal when they have the same cutscene and the same events, in any order.</remarks>
internal sealed class ThnEventSet : IEquatable<ThnEventSet>
{
    private readonly ImmutableHashSet<ThnElementId> ids;

    /// <param name="path">The install path of the cutscene, in normal form.</param>
    /// <param name="events">The events, in the order of W.</param>
    public ThnEventSet(string path, IEnumerable<ThnElementId> events)
    {
        Path = path;
        Events = [.. events.Distinct().Select(id => new ThnRef(path, ThnElementKind.Event, id))];
        ids = [.. Events.Select(e => e.Id)];
    }

    public string Path { get; }

    /// <summary>The events, each one once.</summary>
    public ImmutableArray<ThnRef> Events { get; }

    /// <summary>True when the set has an event.</summary>
    public bool Contains(ThnElementId id) => ids.Contains(id);

    /// <summary>
    /// The selection of some events of a cutscene: null for no event, the event itself for one event, and a set for
    /// two or more.
    /// </summary>
    public static object? Of(string path, IEnumerable<ThnElementId> events)
    {
        var list = events.Distinct().ToList();
        return list.Count switch
        {
            0 => null,
            1 => new ThnRef(path, ThnElementKind.Event, list[0]),
            _ => new ThnEventSet(path, list),
        };
    }

    /// <summary>The events of a selection: the events of a set, one event, or none.</summary>
    public static IReadOnlyList<ThnRef> EventsOf(object? selection) => selection switch
    {
        ThnEventSet set => set.Events,
        ThnRef { Kind: ThnElementKind.Event } ev => [ev],
        _ => [],
    };

    /// <summary>The selection with an event added, or removed when the selection has it (Shift and a click).</summary>
    public static object? Toggle(object? selection, ThnRef ev)
    {
        var events = EventsOf(selection).Where(e => VfsPath.Comparer.Equals(e.Path, ev.Path)).Select(e => e.Id).ToList();
        if (!events.Remove(ev.Id))
            events.Add(ev.Id);
        return Of(ev.Path, events);
    }

    public bool Equals(ThnEventSet? other) =>
        other != null && VfsPath.Comparer.Equals(Path, other.Path) && ids.SetEquals(other.ids);

    public override bool Equals(object? obj) => Equals(obj as ThnEventSet);

    public override int GetHashCode() => HashCode.Combine(VfsPath.Comparer.GetHashCode(Path), ids.Count);
}
