using System;
using System.Linq;
using System.Text;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>The engine evaluator: the THN names, the order of globals and keys, the number rules, and errors that name
/// the path of the value.</summary>
public sealed class ThnEvaluatorTests
{
    /// <summary>The VM loads numbers as floats (Undump.cs:125), so the text 1e39 gives infinity. A hand-written patch
    /// with such a number must fail with a clear error.</summary>
    [Fact]
    public void ANumberThatIsNotFiniteFailsWithThePathOfTheValue()
    {
        var top = Assert.Throws<ThnEvaluationException>(() => Evaluate("x = 1e39"));
        Assert.Equal("test.thn: The value at x is Infinity. A THN number must be finite.", top.Message);

        var deep = Assert.Throws<ThnEvaluationException>(() =>
            Evaluate("entities = { { entity_name = \"a\" }, { spatialprops = { pos = { 0, -1e39, 0 } } } }"));
        Assert.Contains("The value at entities[2].spatialprops.pos[2] is -Infinity.", deep.Message);

        var nan = Assert.Throws<ThnEvaluationException>(() => Evaluate("events = { { 0, 0 / 0 } }"));
        Assert.Contains("The value at events[1][2] is NaN.", nan.Message);
    }

    /// <summary>NaN is not a name of the THN environment, so the VM does not find it (ThornRuntime.cs:304-315).</summary>
    [Fact]
    public void TheNameNaNFailsWithAClearMessage()
    {
        var e = Assert.Throws<ThnEvaluationException>(() => Evaluate("x = NaN"));

        Assert.StartsWith("test.thn: The script uses a name that is not defined.", e.Message);
        Assert.Contains("'NaN'", e.Message);
    }

    /// <summary>A -0 from the VM becomes +0. The compiler folds -1e-46 to a double -1e-46, which the VM loads as the
    /// float -0, and 0 * -1 is -0 in float arithmetic.</summary>
    [Fact]
    public void ANegativeZeroFromTheVmBecomesPositiveZero()
    {
        var globals = Evaluate("a = -1e-46\nb = 0 * -1\nc = -0");

        foreach (var field in globals.Record)
            Assert.Equal(0, BitConverter.SingleToInt32Bits(((ThnNumber)field.Value).Value));
    }

    /// <summary>A name stays a name and a number stays a number. Arithmetic on names gives a number
    /// (ThornRuntime.cs:538-544). Y, N, y and n give booleans.</summary>
    [Fact]
    public void NamesNumbersAndBooleansKeepTheirKind()
    {
        var globals = Evaluate("a = CAMERA\nb = 3\nc = LIT_DYNAMIC + LIT_AMBIENT\nd = { Y, N, y, n }\ne = \"CAMERA\"");

        Assert.Equal(S("CAMERA"), globals["a"]);
        Assert.Equal(N(3), globals["b"]);
        Assert.Equal(N(6), globals["c"]);
        Assert.Equal(List(new ThnBool(true), new ThnBool(false), new ThnBool(true), new ThnBool(false)), globals["d"]);
        Assert.Equal(T("CAMERA"), globals["e"]);
    }

    /// <summary>The globals keep the order of their first assignment. A record keeps the order of its constructor.
    /// A global or a key that holds nil is not set.</summary>
    [Fact]
    public void GlobalsAndKeysKeepTheirOrder()
    {
        var globals = Evaluate("zeta = 1\nalpha = { z = 1, a = 2, m = 3, gone = nil }\nzeta = 2\nnone = nil");

        Assert.Equal(["zeta", "alpha"], globals.Record.Select(f => f.Key));
        Assert.Equal(N(2), globals["zeta"]);
        Assert.Equal(["z", "a", "m"], ((ThnTable)globals["alpha"]!).Record.Select(f => f.Key));
    }

    [Fact]
    public void BytecodeAndTextGiveTheSameTree()
    {
        const string text = "duration = 2.5\nentities = { { entity_name = \"Cam\", type = CAMERA, flags = 6 } }\nevents = {}";

        var fromBytes = Evaluator.Evaluate(Compile(text), "test.thn");

        Assert.Equal(Evaluate(text), fromBytes);
    }

    /// <summary>Each name of the environment evaluates to itself, so each value has one name.</summary>
    [Fact]
    public void SymbolsGiveTheGroupAndTheNumberOfEachName()
    {
        Assert.Equal(new ThnSymbolInfo("EntityTypes", 3), Evaluator.Symbols["CAMERA"]);
        Assert.Equal(new ThnSymbolInfo("EventTypes", 2), Evaluator.Symbols["SET_CAMERA"]);
        Assert.Equal(new ThnSymbolInfo("ThnObjectFlags", 2), Evaluator.Symbols["LIT_DYNAMIC"]);
        Assert.Equal(new ThnSymbolInfo("ThnObjectFlags", 1 << 20), Evaluator.Symbols["SPATIAL"]);
        // FogModes is the only group with a byte for each value (FogModes.cs:8).
        Assert.Equal(new ThnSymbolInfo("FogModes", 3, System.TypeCode.Byte), Evaluator.Symbols["F_LINEAR"]);
        Assert.False(Evaluator.Symbols.ContainsKey("Y"));

        var names = Evaluator.Symbols.Keys.Order(StringComparer.Ordinal).ToArray();
        var list = (ThnTable)Evaluate($"x = {{ {string.Join(", ", names)} }}")["x"]!;
        Assert.Equal(names, list.List.Select(v => ((ThnSymbol)v).Name));
    }

    [Theory]
    [InlineData("x = {", "The script does not compile")]
    [InlineData("x = \"a\" + 1", "The script does not run")]
    [InlineData("dofile(\"other.thn\")", "The script includes the file 'other.thn'. A THN script cannot include another file.")]
    [InlineData("x = { a = 1, a = 2 }", "The script does not run")]
    [InlineData("function f() end", "The value at f is a ")]
    [InlineData("x = { [5] = 1 }", "The script does not run")]
    [InlineData("x = {}\nx[3] = 1", "The value at x[1] is nil.")]
    [InlineData("x = { 1, nil, 3 }", "The value at x[2] is nil.")]
    [InlineData("x = { 1; a = 2 }\nx[7.5] = 3", "The value at x has the key 7.5.")]
    [InlineData("x = { a = \"a\\000b\" }", "The value at x.a is a text with the character U+0000.")]
    [InlineData("x = { [\"a\\000b\"] = 1 }", "The value at x has a key with the character U+0000.")]
    public void AScriptThatTheTreeCannotHoldFailsWithAClearMessage(string text, string message)
    {
        var e = Assert.Throws<ThnEvaluationException>(() => Evaluate(text));

        Assert.StartsWith("test.thn: ", e.Message);
        Assert.Contains(message, e.Message);
    }

    /// <summary>Compiled text sets only Lua names as globals. Other bytecode can set any name, which the writer cannot
    /// write. The VM drops U+0000 at the end of a text (Undump.cs:69), so a short name ends with U+0000 in the
    /// bytecode.</summary>
    [Theory]
    [InlineData("z qz")]
    [InlineData("end")]
    public void BytecodeWithAGlobalNameThatIsNotALuaNameFails(string name)
    {
        var bytes = Compile("zqzq = 1");
        var at = bytes.AsSpan().IndexOf("zqzq"u8);
        Assert.True(at >= 0);
        Encoding.ASCII.GetBytes(name.PadRight(4, '\0')).CopyTo(bytes, at);

        var e = Assert.Throws<ThnEvaluationException>(() => Evaluator.Evaluate(bytes, "test.thn"));

        Assert.Equal($"test.thn: The global name '{name}' is not a Lua name.", e.Message);
    }
}
