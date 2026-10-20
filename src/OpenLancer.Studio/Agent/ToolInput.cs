using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// One argument of a tool: its JSON Schema and its check. The help text of the schema comes from
/// <c>agent-tools.json</c>. The check runs before the handler, so a handler reads only values that fit the schema
/// (section 4, item 5 of the MCP1 design: servers must check the inputs).
/// </summary>
internal abstract class ToolParam(string name, bool required)
{
    public string Name { get; } = name;

    public bool Required { get; } = required;

    /// <summary>The schema of the value, without the help text.</summary>
    public abstract JsonObject Schema(string tool);

    /// <summary>What the value must be, for the error text, for example "a whole number from 1".</summary>
    public abstract string Expected { get; }

    /// <summary>True when the value fits the argument.</summary>
    public abstract bool Fits(JsonNode value);
}

/// <summary>A text, optionally one of a fixed set of words.</summary>
internal sealed class TextParam(string name, bool required, IReadOnlyList<string>? choices, string? fallback)
    : ToolParam(name, required)
{
    public IReadOnlyList<string>? Choices { get; } = choices;

    /// <summary>The value when the call does not give the argument, or null.</summary>
    public string? Default { get; } = fallback;

    public override JsonObject Schema(string tool)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (Choices != null)
            schema["enum"] = new JsonArray(Choices.Select(c => (JsonNode)c).ToArray());
        else
            schema["minLength"] = 1;
        if (Default != null)
            schema["default"] = Default;
        return schema;
    }

    public override string Expected => Choices == null ? "a text" : "one of: " + string.Join(", ", Choices);

    public override bool Fits(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text &&
        (Choices == null || Choices.Contains(text, StringComparer.Ordinal));
}

/// <summary>A whole number from a minimum.</summary>
internal sealed class WholeParam(string name, int minimum) : ToolParam(name, false)
{
    public int Minimum { get; } = minimum;

    public override JsonObject Schema(string tool) => new() { ["type"] = "integer", ["minimum"] = Minimum };

    public override string Expected => $"a whole number from {Minimum}";

    public override bool Fits(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.Number && value.AsValue().TryGetValue(out long number) && number >= Minimum;
}

/// <summary>A number from a minimum.</summary>
internal sealed class NumberParam(string name, bool required, double minimum) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool) => new() { ["type"] = "number", ["minimum"] = minimum };

    public override string Expected => $"a number from {minimum.ToString(CultureInfo.InvariantCulture)}";

    public override bool Fits(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.Number && value.GetValue<double>() >= minimum;
}

/// <summary>True or false.</summary>
internal sealed class FlagParam(string name) : ToolParam(name, false)
{
    public override JsonObject Schema(string tool) => new() { ["type"] = "boolean", ["default"] = false };

    public override string Expected => "true or false";

    public override bool Fits(JsonNode value) => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
}

/// <summary>A list of words from a fixed set, each word once.</summary>
internal sealed class ChoicesParam(string name, IReadOnlyList<string> choices) : ToolParam(name, false)
{
    public IReadOnlyList<string> Choices { get; } = choices;

    public override JsonObject Schema(string tool) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(Choices.Select(c => (JsonNode)c).ToArray()),
        },
        ["uniqueItems"] = true,
    };

    public override string Expected => "a list of: " + string.Join(", ", Choices);

    public override bool Fits(JsonNode value)
    {
        if (value is not JsonArray array)
            return false;
        var words = new HashSet<string>(StringComparer.Ordinal);
        return array.All(item => item?.GetValueKind() == JsonValueKind.String &&
                                 Choices.Contains(item.GetValue<string>(), StringComparer.Ordinal) &&
                                 words.Add(item.GetValue<string>()));
    }
}

/// <summary>A point: three numbers, X, Y and Z in meters.</summary>
internal sealed class PointParam(string name, bool required) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "number" },
        ["minItems"] = 3,
        ["maxItems"] = 3,
    };

    public override string Expected => "three numbers";

    public override bool Fits(JsonNode value) =>
        value is JsonArray { Count: 3 } array && array.All(n => n?.GetValueKind() == JsonValueKind.Number);
}

/// <summary>An object with arguments of its own.</summary>
internal sealed class GroupParam(string name, IReadOnlyList<ToolParam> members) : ToolParam(name, false)
{
    public IReadOnlyList<ToolParam> Members { get; } = members;

    public override JsonObject Schema(string tool) => ToolInput.ObjectSchema(tool, Members);

    public override string Expected => "an object with: " + string.Join(", ", Members.Select(m => m.Name));

    public override bool Fits(JsonNode value) => value is JsonObject;
}

/// <summary>
/// The arguments of a tool: the schema that <c>tools/list</c> gives, and the check of the arguments of a call. The
/// check gives <c>invalid_argument</c> with the detail <see cref="BadArgument"/> and the reason.
/// </summary>
internal sealed class ToolInput(params ToolParam[] parameters)
{
    /// <summary>The detail code of an argument that does not fit the schema.</summary>
    public const string BadArgument = "BadArgument";

    public IReadOnlyList<ToolParam> Parameters { get; } = parameters;

    /// <summary>A tool without arguments.</summary>
    public static readonly ToolInput None = new();

    /// <summary>The schema of the arguments, with the help of each argument.</summary>
    public JsonObject Schema(string tool) => ObjectSchema(tool, Parameters);

    internal static JsonObject ObjectSchema(string tool, IReadOnlyList<ToolParam> parameters)
    {
        var properties = new JsonObject();
        foreach (var parameter in parameters)
        {
            var schema = parameter.Schema(tool);
            schema["description"] = AgentTexts.Field(tool, parameter.Name);
            properties[parameter.Name] = schema;
        }
        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (parameters.Any(p => p.Required))
            result["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray());
        return result;
    }

    /// <summary>Checks the arguments of a call. A null value counts as a missing argument.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> with the detail
    /// <see cref="BadArgument"/>.</exception>
    public ToolArgs Read(string tool, JsonObject? arguments) => Read(tool, Parameters, arguments, null);

    private static ToolArgs Read(string tool, IReadOnlyList<ToolParam> parameters, JsonObject? arguments, string? parent)
    {
        string Path(string name) => parent == null ? name : $"{parent}.{name}";
        var values = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var groups = new Dictionary<string, ToolArgs>(StringComparer.Ordinal);
        foreach (var (name, value) in arguments ?? [])
        {
            var parameter = parameters.FirstOrDefault(p => p.Name == name);
            if (parameter == null)
            {
                throw Bad(Path(name), parameters.Count == 0
                    ? AgentTexts.Error("argument_none", ("tool", tool), ("name", Path(name)))
                    : AgentTexts.Error("argument_unknown", ("tool", tool), ("name", Path(name)),
                        ("names", string.Join(", ", parameters.Select(p => p.Name)))));
            }
            if (value == null)
                continue;
            if (!parameter.Fits(value))
                throw Bad(Path(name), AgentTexts.Error("argument_type", ("name", Path(name)), ("expected", parameter.Expected)));
            values[name] = value;
            if (parameter is GroupParam group)
                groups[name] = Read(tool, group.Members, (JsonObject)value, Path(name));
        }
        foreach (var parameter in parameters.Where(p => p.Required && !values.ContainsKey(p.Name)))
            throw Bad(Path(parameter.Name), AgentTexts.Error("argument_missing", ("name", Path(parameter.Name))));
        return new ToolArgs(parameters, values, groups);
    }

    private static EditException Bad(string field, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, BadArgument, message) { Field = field });
}

/// <summary>The checked arguments of a call. Each accessor gives the default of a missing argument.</summary>
internal sealed class ToolArgs(IReadOnlyList<ToolParam> parameters, IReadOnlyDictionary<string, JsonNode> values,
    IReadOnlyDictionary<string, ToolArgs> groups)
{
    /// <summary>True when the call gives the argument.</summary>
    public bool Has(string name) => values.ContainsKey(name);

    /// <summary>A text, the default of its argument, or null.</summary>
    public string? Text(string name) =>
        values.TryGetValue(name, out var value) ? value.GetValue<string>()
        : parameters.OfType<TextParam>().FirstOrDefault(p => p.Name == name)?.Default;

    /// <summary>A whole number, or null.</summary>
    public long? Whole(string name) => values.TryGetValue(name, out var value) ? value.GetValue<long>() : null;

    /// <summary>A number, or null.</summary>
    public double? Number(string name) => values.TryGetValue(name, out var value) ? value.GetValue<double>() : null;

    /// <summary>True or false. A missing argument is false.</summary>
    public bool Flag(string name) => values.TryGetValue(name, out var value) && value.GetValue<bool>();

    /// <summary>The words of a list, or null when the call does not give the list.</summary>
    public IReadOnlyList<string>? Choices(string name) =>
        values.TryGetValue(name, out var value) ? value.AsArray().Select(v => v!.GetValue<string>()).ToList() : null;

    /// <summary>A point, or null.</summary>
    public Vector3? Point(string name) => values.TryGetValue(name, out var value)
        ? new Vector3(value[0]!.GetValue<float>(), value[1]!.GetValue<float>(), value[2]!.GetValue<float>())
        : null;

    /// <summary>The arguments of an object argument, or null.</summary>
    public ToolArgs? Group(string name) => groups.GetValueOrDefault(name);
}
