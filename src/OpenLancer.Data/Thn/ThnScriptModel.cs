using System;
using System.Collections.Immutable;
using System.Linq;

namespace OpenLancer.Data.Thn;

/// <summary>The two kinds of element of a script.</summary>
public enum ThnElementKind
{
    /// <summary>An item of the <c>entities</c> list: a record with <c>entity_name</c>, <c>type</c> and the props of
    /// its type.</summary>
    Entity,

    /// <summary>An item of the <c>events</c> list: the list <c>{ time, type, { targets }, { props } }</c>. The props
    /// table is optional.</summary>
    Event,
}

/// <summary>The identity of an element. It stays the same in each model that an edit or a patch makes from the model
/// of the element, so a selection survives each edit.</summary>
public readonly record struct ThnElementId(long Value)
{
    /// <summary>Gives the ids 0, 1, 2 and so on. Use one source for a model and the models that come from it.</summary>
    public static Func<ThnElementId> Counter()
    {
        var next = 0L;
        return () => new ThnElementId(next++);
    }
}

/// <summary>An entity or an event of a script.</summary>
/// <param name="Origin">The index of the element in the lower script (B), or -1 for a new element.</param>
/// <param name="Table">The record of an entity, or the list of an event.</param>
public sealed record ThnElement(ThnElementId Id, int Origin, ThnTable Table);

/// <summary>
/// A script as elements: the duration, the entities, the events and the other globals. The engine reads these three
/// globals (ThnScript.cs:122-141).
/// </summary>
/// <remarks>
/// <para>The model is immutable. An edit replaces only the elements that it changes, so all other tables stay the same
/// objects.</para>
/// <para>Equality compares the other globals, the duration and the elements in order, with their ids and origins.
/// The tables compare exactly (see <see cref="ThnValue"/>).</para>
/// </remarks>
public sealed record ThnScriptModel
{
    public const string DurationGlobal = "duration";
    public const string EntitiesGlobal = "entities";
    public const string EventsGlobal = "events";

    /// <summary>The key of the name of an entity.</summary>
    public const string EntityNameKey = "entity_name";

    /// <summary>The globals that the model holds as its own values, in the order that <see cref="ToGlobals"/> adds them.</summary>
    private static readonly string[] ModelGlobals = [DurationGlobal, EntitiesGlobal, EventsGlobal];

    private readonly ThnTable otherGlobals = ThnTable.Empty;
    private readonly float duration;
    private readonly ImmutableList<ThnElement> entities = [];
    private readonly ImmutableList<ThnElement> events = [];

    /// <exception cref="ArgumentException">The other globals have a list part.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The duration is NaN or infinite.</exception>
    public ThnScriptModel(ThnTable otherGlobals, float duration, ImmutableList<ThnElement> entities,
        ImmutableList<ThnElement> events)
    {
        OtherGlobals = otherGlobals;
        Duration = duration;
        Entities = entities;
        Events = events;
    }

    /// <summary>
    /// The globals of the script in order. The fields <c>duration</c>, <c>entities</c> and <c>events</c> only keep
    /// their places: they hold empty tables, and <see cref="ToGlobals"/> puts the values of the model there. A table
    /// without these fields is valid, for example <see cref="ThnTable.Empty"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The table has a list part.</exception>
    public ThnTable OtherGlobals
    {
        get => otherGlobals;
        init => otherGlobals = Places(value);
    }

    /// <summary>The length of the script in seconds. -0 becomes +0, as in <see cref="ThnNumber"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or infinite.</exception>
    public float Duration
    {
        get => duration;
        init => duration = new ThnNumber(value).Value;
    }

    public ImmutableList<ThnElement> Entities
    {
        get => entities;
        init => entities = value ?? throw new ArgumentNullException(nameof(value));
    }

    public ImmutableList<ThnElement> Events
    {
        get => events;
        init => events = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The elements of one kind.</summary>
    public ImmutableList<ThnElement> Elements(ThnElementKind kind) => kind == ThnElementKind.Entity ? Entities : Events;

    /// <summary>The element with an id, or null.</summary>
    public ThnElement? Element(ThnElementKind kind, ThnElementId id) => Elements(kind).Find(e => e.Id == id);

    /// <summary>The entity that the engine uses for a name: the last entity with the name, without letter case
    /// (ThnScript.cs:107, :130-137). Null when no entity has the name.</summary>
    public ThnElement? EntityNamed(string name) =>
        Entities.LastOrDefault(e => NameOf(e.Table)?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>The name of an entity, or null when it has no text name.</summary>
    public static string? NameOf(ThnTable entity) => (entity[EntityNameKey] as ThnText)?.Value;

    /// <summary>The globals of the script, in the order of <see cref="OtherGlobals"/>. A model global that
    /// <see cref="OtherGlobals"/> does not have goes at the end, in the order duration, entities, events.</summary>
    public ThnTable ToGlobals()
    {
        var fields = ImmutableArray.CreateBuilder<ThnField>(otherGlobals.Record.Length + 3);
        foreach (var field in otherGlobals.Record)
            fields.Add(ModelValue(field.Key) is { } value ? new ThnField(field.Key, value) : field);
        foreach (var name in ModelGlobals)
        {
            if (otherGlobals[name] == null)
                fields.Add(new ThnField(name, ModelValue(name)!));
        }
        return new ThnTable([], fields.ToImmutable());
    }

    /// <summary>
    /// Makes a model of the globals of a script. Each element gets a new id, and its index in its list as its origin.
    /// </summary>
    /// <param name="ids">Gives a new id for each element.</param>
    /// <exception cref="ArgumentException">The globals have a list part.</exception>
    /// <exception cref="FormatException"><c>duration</c> is not a number, or <c>entities</c> or <c>events</c> is not a
    /// list of tables. The engine cannot read such a script (ThnScript.cs:122-141).</exception>
    public static ThnScriptModel From(ThnTable globals, Func<ThnElementId> ids)
    {
        if (!globals.IsRecord)
            throw new ArgumentException("The globals of a script must be a record.", nameof(globals));
        if (globals[DurationGlobal] is not ThnNumber number)
            throw new FormatException($"The global '{DurationGlobal}' must be a number.");
        return new ThnScriptModel(globals, number.Value, Read(globals, EntitiesGlobal, ids), Read(globals, EventsGlobal, ids));
    }

    public bool Equals(ThnScriptModel? other) =>
        ReferenceEquals(this, other) ||
        other is not null && duration.Equals(other.duration) && otherGlobals.Equals(other.otherGlobals) &&
        entities.SequenceEqual(other.entities) && events.SequenceEqual(other.events);

    public override int GetHashCode() => HashCode.Combine(duration, otherGlobals, entities.Count, events.Count);

    private ThnValue? ModelValue(string name) => name switch
    {
        DurationGlobal => new ThnNumber(duration),
        EntitiesGlobal => List(entities),
        EventsGlobal => List(events),
        _ => null,
    };

    private static ThnTable List(ImmutableList<ThnElement> elements) =>
        new([.. elements.Select(e => (ThnValue)e.Table)], []);

    /// <summary>The globals with an empty table in the place of each model global.</summary>
    private static ThnTable Places(ThnTable globals)
    {
        ArgumentNullException.ThrowIfNull(globals);
        if (!globals.IsRecord)
            throw new ArgumentException("The globals of a script must be a record.", nameof(globals));
        if (globals.Record.All(f => !ModelGlobals.Contains(f.Key) || f.Value.Equals(ThnTable.Empty)))
            return globals;
        return new ThnTable([], [.. globals.Record.Select(f =>
            ModelGlobals.Contains(f.Key) ? new ThnField(f.Key, ThnTable.Empty) : f)]);
    }

    private static ImmutableList<ThnElement> Read(ThnTable globals, string name, Func<ThnElementId> ids)
    {
        if (globals[name] is not ThnTable { Record.IsEmpty: true } list)
            throw new FormatException($"The global '{name}' must be a list of tables.");
        var elements = ImmutableList.CreateBuilder<ThnElement>();
        for (var i = 0; i < list.List.Length; i++)
        {
            if (list.List[i] is not ThnTable table)
                throw new FormatException($"The value at {ThnValues.ItemPath(name, i + 1)} must be a table.");
            elements.Add(new ThnElement(ids(), i, table));
        }
        return elements.ToImmutable();
    }
}
