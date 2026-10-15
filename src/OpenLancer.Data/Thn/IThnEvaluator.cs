using System;
using System.Collections.Generic;

namespace OpenLancer.Data.Thn;

/// <summary>The group and the number of a THN name, for example <c>CAMERA</c> is <c>EntityTypes</c> 3.</summary>
/// <param name="Storage">The integer type of the values of the group. The engine converts a value of one group to
/// another group only when the two groups have the same integer type (ThnTypes.cs:59, :77, ThnScript.cs:301), and it
/// converts a number only when the integer type can hold it.</param>
public sealed record ThnSymbolInfo(string Group, long Value, TypeCode Storage = TypeCode.Int32);

/// <summary>
/// Runs cutscene scripts and gives their globals as a <see cref="ThnValue"/> tree. OpenLancer.Runtime implements this
/// with the Thorn VM of the engine, because OpenLancer.Data does not reference the engine.
/// </summary>
public interface IThnEvaluator
{
    /// <summary>Runs Lua 3.2 bytecode or text in the THN environment. Gives the globals in order, as a tree that
    /// <see cref="ThnWriter"/> can write.</summary>
    /// <param name="script">The bytes of the script, as a file holds them.</param>
    /// <param name="name">The name of the script, for messages.</param>
    /// <exception cref="ThnEvaluationException">The script does not compile or does not run, or it gives a value, a
    /// key or a global name that the writer cannot write, for example a number that is not finite. The message names
    /// the path of the value.</exception>
    ThnTable Evaluate(ReadOnlySpan<byte> script, string name);

    /// <summary>The names of the THN environment, with their group and number. <c>Y</c> and <c>N</c> are not in it,
    /// because they give <see cref="ThnBool"/> values.</summary>
    IReadOnlyDictionary<string, ThnSymbolInfo> Symbols { get; }
}

/// <summary>A cutscene script does not compile or does not run, or it gives a value that <see cref="ThnWriter"/>
/// cannot write.</summary>
public sealed class ThnEvaluationException(string script, string message, Exception? inner = null)
    : Exception($"{script}: {message}", inner);
