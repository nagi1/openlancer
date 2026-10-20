using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>A mission script (section 4.2 of the M6b design). JSON writes it as <c>msn:&lt;install path&gt;</c>.</summary>
/// <param name="Path">The install path in normal form.</param>
[JsonConverter(typeof(MissionRefConverter<ScriptRef>))]
public sealed record ScriptRef(string Path) : IMissionRef
{
    public bool Equals(ScriptRef? other) => other is not null && VfsPath.Comparer.Equals(Path, other.Path);

    public override int GetHashCode() => VfsPath.Comparer.GetHashCode(Path);

    public override string ToString() => ItemRefs.Format(new ItemReference(ItemRefKind.Script, "") { Path = Path });

    /// <summary>The reference of a text, for example <c>msn:DATA\MISSIONS\M02\m02.ini</c>.</summary>
    /// <exception cref="EditException">The text is not a script reference.</exception>
    public static ScriptRef Parse(string text) =>
        ItemRefs.Parse(text) is { Kind: ItemRefKind.Script, Path: { } path }
            ? new ScriptRef(path)
            : throw MissionRefs.Bad(text, "a mission script, for example msn:DATA\\MISSIONS\\M02\\m02.ini");

    /// <summary>The reference of an install path.</summary>
    /// <exception cref="EditException">The path is not an install path.</exception>
    public static ScriptRef Of(string path) => new(InstallPaths.Parse(path));
}

/// <summary>A trigger of a script by its nickname (section 4.2 of the M6b design). A repeated nickname names the first
/// trigger (TR4). JSON writes it as <c>trg:&lt;install path&gt;#&lt;trigger&gt;</c>.</summary>
[JsonConverter(typeof(MissionRefConverter<TriggerRef>))]
public sealed record TriggerRef(string Script, string Name) : IMissionRef
{
    public bool Equals(TriggerRef? other) =>
        other is not null && VfsPath.Comparer.Equals(Script, other.Script) &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(VfsPath.Comparer.GetHashCode(Script), StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    public override string ToString() => ItemRefs.Format(new ItemReference(ItemRefKind.Trigger, Name) { Path = Script });

    /// <exception cref="EditException">The text is not a trigger reference.</exception>
    public static TriggerRef Parse(string text) =>
        ItemRefs.Parse(text) is { Kind: ItemRefKind.Trigger, Path: { } path } reference && !MissionRefs.IsPrefab(path)
            ? new TriggerRef(path, reference.Name)
            : throw MissionRefs.Bad(text, "a trigger, for example trg:DATA\\MISSIONS\\M02\\m02.ini#ol_trigger");

    /// <summary>The trigger as an item.</summary>
    public ItemRef Item => new(Script, new SectionIdentity("Trigger", Name));
}

/// <summary>
/// A section of a mission file (section 4.2 of the M6b design): a ship, a formation, a solar, a loot, an order list,
/// an objective, a conversation, an NPC, a random encounter, a ship build, a trigger or another section. The ordinal
/// tells apart two sections with one nickname. JSON writes it as <c>sec:&lt;install path&gt;#&lt;section&gt;</c>, and
/// reads also a <c>trg:</c> reference.
/// </summary>
[JsonConverter(typeof(MissionRefConverter<ItemRef>))]
public sealed record ItemRef(string Script, SectionIdentity Section) : IMissionRef
{
    public bool Equals(ItemRef? other) =>
        other is not null && VfsPath.Comparer.Equals(Script, other.Script) && Section.Equals(other.Section);

    public override int GetHashCode() => HashCode.Combine(VfsPath.Comparer.GetHashCode(Script), Section);

    public override string ToString() => ItemRefs.Of(new SectionRef(Script, Section));

    /// <summary>The section of the item.</summary>
    public SectionRef Ref => new(Script, Section);

    /// <summary>The item of a section with a key.</summary>
    public static ItemRef Of(string script, string type, string? key, int ordinal = 0) =>
        new(script, new SectionIdentity(type, key, null, ordinal));

    /// <exception cref="EditException">The text is not a reference of a section or a trigger.</exception>
    public static ItemRef Parse(string text) => ItemRefs.Parse(text) switch
    {
        { Kind: ItemRefKind.Section, Path: { } path, Section: { } section } => new ItemRef(path, section),
        { Kind: ItemRefKind.Trigger, Path: { } path } trigger when !MissionRefs.IsPrefab(path) =>
            new ItemRef(path, new SectionIdentity("Trigger", trigger.Name)),
        _ => throw MissionRefs.Bad(text, "a section, for example sec:DATA\\MISSIONS\\M02\\m02.ini#MsnShip/ol_ship"),
    };
}

/// <summary>
/// A line of a mission section by its entry index from 0 (section 4.2 of the M6b design). The expected key and values
/// are optional guards: when they do not match, the command refuses with <c>RowChanged</c>. JSON writes the line as an
/// object with the <c>line:</c> reference in <c>ref</c>.
/// </summary>
/// <param name="Item">The section of the line.</param>
/// <param name="Row">The entry index of the line in its section, from 0.</param>
[JsonConverter(typeof(RowRefConverter))]
public sealed record RowRef(ItemRef Item, int Row)
{
    /// <summary>The key that the line must have, or null.</summary>
    public string? ExpectKey { get; init; }

    /// <summary>The value texts that the line must have, or null.</summary>
    public IReadOnlyList<string>? ExpectValues { get; init; }

    /// <summary>The <c>line:</c> reference.</summary>
    public string Reference => ItemRefs.Format(new ItemReference(ItemRefKind.Line, "")
    {
        Path = Item.Script, Section = Item.Section, Row = Row,
    });

    public override string ToString() => Reference;

    /// <summary>A line of a trigger.</summary>
    public static RowRef Of(TriggerRef trigger, int row) => new(trigger.Item, row);

    /// <exception cref="EditException">The text is not a line reference.</exception>
    public static RowRef Parse(string text) =>
        ItemRefs.Parse(text) is { Kind: ItemRefKind.Line, Path: { } path, Section: { } section, Row: { } row } &&
        !MissionRefs.IsPrefab(path)
            ? new RowRef(new ItemRef(path, section), row)
            : throw MissionRefs.Bad(text, "a line, for example line:DATA\\MISSIONS\\M02\\m02.ini#Trigger/ol_trigger/2");
}

/// <summary>A label of a mission script (section 4.2 of the M6b design). JSON writes it as
/// <c>label:&lt;install path&gt;#&lt;label&gt;</c>.</summary>
[JsonConverter(typeof(MissionRefConverter<LabelRef>))]
public sealed record LabelRef(string Script, string Label) : IMissionRef
{
    public bool Equals(LabelRef? other) =>
        other is not null && VfsPath.Comparer.Equals(Script, other.Script) &&
        string.Equals(Label, other.Label, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(VfsPath.Comparer.GetHashCode(Script), StringComparer.OrdinalIgnoreCase.GetHashCode(Label));

    public override string ToString() => ItemRefs.Format(new ItemReference(ItemRefKind.Label, Label) { Path = Script });

    /// <exception cref="EditException">The text is not a label reference.</exception>
    public static LabelRef Parse(string text) =>
        ItemRefs.Parse(text) is { Kind: ItemRefKind.Label, Path: { } path } reference
            ? new LabelRef(path, reference.Name)
            : throw MissionRefs.Bad(text, "a label, for example label:DATA\\MISSIONS\\M02\\m02.ini#ol_wing");
}

/// <summary>A reference of the mission commands that JSON writes as one string.</summary>
public interface IMissionRef;

/// <summary>The helpers of the mission references.</summary>
internal static class MissionRefs
{
    public static bool IsPrefab(string path) => path.StartsWith("prefab:", StringComparison.Ordinal);

    public static EditException Bad(string text, string what) =>
        new(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference,
            $"'{text}' is not a reference of {what}.") { Target = text });

    /// <summary>An example of the reference text of a type of the mission commands.</summary>
    public static string Example(Type type) =>
        type == typeof(ScriptRef) ? @"msn:DATA\MISSIONS\M02\m02.ini"
        : type == typeof(TriggerRef) ? @"trg:DATA\MISSIONS\M02\m02.ini#ol_trigger"
        : type == typeof(LabelRef) ? @"label:DATA\MISSIONS\M02\m02.ini#ol_wing"
        : @"sec:DATA\MISSIONS\M02\m02.ini#MsnShip/ol_ship";

    /// <summary>The reference of a text as a type of the mission commands.</summary>
    public static object Parse(Type type, string text) =>
        type == typeof(ScriptRef) ? ScriptRef.Parse(text)
        : type == typeof(TriggerRef) ? TriggerRef.Parse(text)
        : type == typeof(ItemRef) ? ItemRef.Parse(text)
        : type == typeof(LabelRef) ? LabelRef.Parse(text)
        : throw new NotSupportedException(type.Name);
}

/// <summary>Reads and writes a mission reference as its string. A text that is not such a reference gives a
/// <see cref="JsonException"/> with the message of the refusal.</summary>
public sealed class MissionRefConverter<T> : JsonConverter<T> where T : class, IMissionRef
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new MissionJsonException(MissionErrors.BadJsonError(
                $"An item of a mission command is a reference text, for example {MissionRefs.Example(typeof(T))}."));
        var text = reader.GetString()!;
        try
        {
            return (T)MissionRefs.Parse(typeof(T), text);
        }
        catch (EditException e)
        {
            throw new MissionJsonException(e.Error);
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Reads and writes a <see cref="RowRef"/>: an object with <c>ref</c>, <c>expect_key</c> and
/// <c>expect_values</c>.</summary>
public sealed class RowRefConverter : JsonConverter<RowRef>
{
    public override RowRef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
            return Parse(root.GetString()!);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ref", out var reference) ||
            reference.ValueKind != JsonValueKind.String)
            throw new MissionJsonException(MissionErrors.BadJsonError("A row is an object with the line reference in 'ref'."));
        var row = Parse(reference.GetString()!);
        if (root.TryGetProperty("expect_key", out var key) && key.ValueKind == JsonValueKind.String)
            row = row with { ExpectKey = key.GetString() };
        if (root.TryGetProperty("expect_values", out var values) && values.ValueKind == JsonValueKind.Array)
            row = row with { ExpectValues = values.EnumerateArray().Select(Text).ToList() };
        return row;
    }

    public override void Write(Utf8JsonWriter writer, RowRef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("ref", value.Reference);
        if (value.ExpectKey != null)
            writer.WriteString("expect_key", value.ExpectKey);
        if (value.ExpectValues != null)
        {
            writer.WriteStartArray("expect_values");
            foreach (var text in value.ExpectValues)
                writer.WriteStringValue(text);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static RowRef Parse(string text)
    {
        try
        {
            return RowRef.Parse(text);
        }
        catch (EditException e)
        {
            throw new MissionJsonException(e.Error);
        }
    }

    private static string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
}

/// <summary>A JSON value of a mission request that the command layer refuses, with the error of the refusal.</summary>
public sealed class MissionJsonException(EditError error) : JsonException(error.Message)
{
    public EditError Error { get; } = error;
}
