using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using LibreLancer.Thn;
using LibreLancer.Thorn;
using OpenLancer.Data.Thn;

namespace OpenLancer.Runtime.Thn;

/// <summary>
/// Runs cutscene scripts with the Thorn VM and the THN names of <see cref="ThnScript.ThnEnv"/>, as the engine runs
/// them (ThnScript.cs:110-114). It converts the globals into a <see cref="ThnValue"/> tree.
/// </summary>
/// <remarks>
/// <para>The VM runs bytecode, and it compiles text with the native compiler first (ThornRuntime.cs:49-59). The
/// compiler needs <c>x64\thorncompiler.dll</c> and <c>AppHandler.ConsoleInit</c>. It is not re-entrant, so
/// <see cref="ThornCompiler"/> lets one thread at a time compile.</para>
/// <para>A script cannot include another file. No retail script does (<c>dofile</c>), and the tree has no form for an
/// include.</para>
/// </remarks>
public sealed class ThornEvaluator : IThnEvaluator
{
    /// <summary>The name of each enum value of the environment.</summary>
    private readonly FrozenDictionary<Enum, string> names;

    private ThornEvaluator()
    {
        var reverse = new Dictionary<Enum, string>();
        var symbols = new Dictionary<string, ThnSymbolInfo>(StringComparer.Ordinal);
        foreach (var (name, value) in ThnScript.ThnEnv)
        {
            // Y, N, y and n are booleans.
            if (value is not Enum symbol)
                continue;
            if (!reverse.TryAdd(symbol, name))
                throw new InvalidOperationException(
                    $"The THN names {reverse[symbol]} and {name} have the same value, so the value has no single name.");
            symbols.Add(name, new ThnSymbolInfo(symbol.GetType().Name, Convert.ToInt64(symbol, CultureInfo.InvariantCulture),
                Type.GetTypeCode(Enum.GetUnderlyingType(symbol.GetType()))));
        }
        names = reverse.ToFrozenDictionary();
        Symbols = symbols.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static ThornEvaluator Instance { get; } = new();

    public IReadOnlyDictionary<string, ThnSymbolInfo> Symbols { get; }

    public ThnTable Evaluate(ReadOnlySpan<byte> script, string name)
    {
        Dictionary<string, object> globals;
        try
        {
            var runner = new ThornRunner(ThnScript.ThnEnv, include => throw new ThnEvaluationException(name,
                $"The script includes the file '{include}'. A THN script cannot include another file."));
            globals = runner.DoBytes(script.ToArray(), name);
        }
        catch (ThornCompileException e)
        {
            throw new ThnEvaluationException(name, $"The script does not compile: {e.Message}", e);
        }
        catch (KeyNotFoundException e)
        {
            // The VM reads an unknown global name from its globals dictionary (ThornRuntime.cs:304-315).
            throw new ThnEvaluationException(name, $"The script uses a name that is not defined. {e.Message}", e);
        }
        catch (Exception e) when (e is not (ThnEvaluationException or DllNotFoundException or EntryPointNotFoundException
                                       or BadImageFormatException))
        {
            // The VM throws many types for a script that does not run, for example InvalidCastException for
            // arithmetic on a text. A missing or wrong native compiler is not a problem of the script.
            throw new ThnEvaluationException(name, $"The script does not run: {e.Message}", e);
        }
        return new Reader(names, name).Globals(globals);
    }

    /// <summary>Converts the VM output of one script, and names the path of a value that the writer cannot
    /// write.</summary>
    private sealed class Reader(FrozenDictionary<Enum, string> names, string script)
    {
        /// <summary>The VM reads a text as ASCII (Undump.cs:69), so U+0000 is the only character of a text or a key that
        /// the writer refuses.</summary>
        private const string NulProblem = "with the character U+0000. A THN text cannot hold it.";

        /// <summary>The path from the globals to the value that the reader converts now: a record key, or a list index
        /// from 1.</summary>
        private readonly List<(string? Key, int Index)> path = [];

        public ThnTable Globals(Dictionary<string, object> globals)
        {
            // The VM keeps the globals in the order of their first assignment.
            var fields = ImmutableArray.CreateBuilder<ThnField>(globals.Count);
            foreach (var (key, value) in globals)
            {
                // A global that holds nil is not set.
                if (value == null)
                    continue;
                // Compiled text sets only Lua names. Other bytecode can set any name.
                if (!ThnWriter.IsName(key))
                    throw new ThnEvaluationException(script, $"The global name '{key}' is not a Lua name.");
                fields.Add(new ThnField(key, Child(key, 0, value)));
            }
            return new ThnTable([], fields.ToImmutable());
        }

        private ThnValue Child(string? key, int index, object? value)
        {
            path.Add((key, index));
            var result = Value(value);
            path.RemoveAt(path.Count - 1);
            return result;
        }

        private ThnValue Value(object? value)
        {
            switch (value)
            {
                case null:
                    // The length of a VM table counts a nil item inside the list, for example in { 1, nil, 3 }
                    // (ThornTable.cs:163-173).
                    throw Error("is nil. A THN list cannot have a gap.");
                case float number:
                    if (!float.IsFinite(number))
                        throw Error($"is {number.ToString(CultureInfo.InvariantCulture)}. A THN number must be finite.");
                    return new ThnNumber(number);
                case string text:
                    if (text.Contains('\0'))
                        throw Error($"is a text {NulProblem}");
                    return new ThnText(text);
                case bool flag:
                    return new ThnBool(flag);
                case Enum symbol when names.TryGetValue(symbol, out var name):
                    return new ThnSymbol(name);
                case ThornTable table:
                    return Table(table);
                default:
                    throw Error($"is a {value.GetType().Name}. A THN value is a number, a text, Y or N, a THN name or a table.");
            }
        }

        private ThnTable Table(ThornTable table)
        {
            // Pairs gives the list part (int keys) first and then the record part in insertion order
            // (ThornTable.cs:352-363). The list part is the keys 1 to Length.
            var length = table.Length;
            var list = ImmutableArray.CreateBuilder<ThnValue>(length);
            for (var i = 1; i <= length; i++)
                list.Add(Child(null, i, table[i]));
            var record = ImmutableArray.CreateBuilder<ThnField>();
            foreach (var pair in table.Pairs)
            {
                if (pair.Key is int index && index >= 1 && index <= length)
                    continue;
                // A key that holds nil is not set.
                if (pair.Value == null)
                    continue;
                if (pair.Key is not string key)
                    throw Error($"has the key {Convert.ToString(pair.Key, CultureInfo.InvariantCulture)}. A THN table has the list items 1 to n and text keys only.");
                if (key.Contains('\0'))
                    throw Error($"has a key {NulProblem}");
                record.Add(new ThnField(key, Child(key, 0, pair.Value)));
            }
            return new ThnTable(list.ToImmutable(), record.ToImmutable());
        }

        private ThnEvaluationException Error(string problem)
        {
            var text = "";
            foreach (var (key, index) in path)
                text = key != null ? ThnValues.FieldPath(text, key) : ThnValues.ItemPath(text, index);
            return new ThnEvaluationException(script, $"The value at {text} {problem}");
        }
    }
}
