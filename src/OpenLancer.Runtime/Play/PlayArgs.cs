using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// What the editor gives the play process (section 6.3 of the M4 design):
/// <code>
/// OpenLancer.Play.exe --profile &lt;profile.json&gt; [--mod &lt;active mod id&gt;] [--save &lt;n&gt;]
///                     (--space &lt;system&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;qx&gt; &lt;qy&gt; &lt;qz&gt; &lt;qw&gt; | --base &lt;base nickname&gt;
///                      | --start-save &lt;file&gt; [--test-layer &lt;folder&gt;])
///                     [--package &lt;ship package nickname&gt;]
/// </code>
/// A mission play (section 5.6 of the M6c design) gives <c>--start-save</c> and, for its setup triggers,
/// <c>--test-layer</c>.
/// Numbers use the invariant culture. Each float has the round-trip format <c>R</c>, so a write and a read give the
/// same record.
/// </summary>
/// <param name="Profile">The full path of the profile file.</param>
/// <param name="ActiveMod">The id of the active mod, or null for all mods of the profile.</param>
/// <param name="Save">The number of the editor save whose files the game loads. Only the status lines use it.</param>
/// <param name="Package">The nickname of the ship package, or null for the package of a new multiplayer character
/// (decision D15).</param>
public sealed record PlayArgs(string Profile, string? ActiveMod, int Save, PlayStart Start, string? Package)
{
    private const string ProfileOption = "--profile";
    private const string ModOption = "--mod";
    private const string SaveOption = "--save";
    private const string SpaceOption = "--space";
    private const string BaseOption = "--base";
    private const string PackageOption = "--package";
    private const string StartSaveOption = "--start-save";
    private const string TestLayerOption = "--test-layer";

    /// <summary>The numbers after the system of <c>--space</c>: the position, then the quaternion.</summary>
    private const int SpaceNumbers = 7;

    /// <summary>A quaternion whose length differs more from 1 is not an orientation.</summary>
    private const float UnitTolerance = 1e-3f;

    /// <summary>The folder of the test layer of a mission play, or null. The start, each reload and each restart of the
    /// play load it after the mods of the play (section 5.4 of the M6c design).</summary>
    public string? TestLayer => (Start as PlayStart.FromSave)?.TestLayer;

    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string> { ProfileOption, Profile };
        if (ActiveMod != null)
            args.AddRange([ModOption, ActiveMod]);
        args.AddRange([SaveOption, Save.ToString(CultureInfo.InvariantCulture)]);
        switch (Start)
        {
            case PlayStart.InSpace space:
                args.AddRange([SpaceOption, space.System]);
                var (p, q) = (space.Position, space.Orientation);
                foreach (var number in (float[])[p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W])
                    args.Add(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case PlayStart.AtBase atBase:
                args.AddRange([BaseOption, atBase.Base]);
                break;
            case PlayStart.FromSave fromSave:
                args.AddRange([StartSaveOption, fromSave.Save]);
                if (fromSave.TestLayer != null)
                    args.AddRange([TestLayerOption, fromSave.TestLayer]);
                break;
        }
        if (Package != null)
            args.AddRange([PackageOption, Package]);
        return args;
    }

    /// <exception cref="FormatException">The list is not valid. The message gives the reason.</exception>
    public static PlayArgs Parse(IReadOnlyList<string> args)
    {
        string? profile = null, mod = null, package = null, layer = null;
        int? save = null;
        PlayStart? start = null;
        for (var i = 0; i < args.Count;)
        {
            var option = args[i++];
            switch (option)
            {
                case ProfileOption:
                    profile = Once(profile, option, Values(args, ref i, option, 1)[0]);
                    break;
                case ModOption:
                    mod = Once(mod, option, Values(args, ref i, option, 1)[0]);
                    break;
                case SaveOption:
                    var text = Values(args, ref i, option, 1)[0];
                    save = Once(save, option, int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                        ? n
                        : throw new FormatException($"The value '{text}' of {option} is not a save number."));
                    break;
                case SpaceOption:
                    var space = Values(args, ref i, option, 1 + SpaceNumbers);
                    start = OneStart(start, Space(space));
                    break;
                case BaseOption:
                    start = OneStart(start, new PlayStart.AtBase(Values(args, ref i, option, 1)[0]));
                    break;
                case PackageOption:
                    package = Once(package, option, Values(args, ref i, option, 1)[0]);
                    break;
                case StartSaveOption:
                    start = OneStart(start, new PlayStart.FromSave(Values(args, ref i, option, 1)[0], null));
                    break;
                case TestLayerOption:
                    layer = Once(layer, option, Values(args, ref i, option, 1)[0]);
                    break;
                default:
                    throw new FormatException($"The argument '{option}' is not known.");
            }
        }
        if (layer != null)
        {
            start = start is PlayStart.FromSave fromSave
                ? fromSave with { TestLayer = layer }
                : throw new FormatException($"{TestLayerOption} needs {StartSaveOption}.");
        }

        return new PlayArgs(
            profile ?? throw new FormatException($"The arguments have no {ProfileOption}."),
            mod,
            save ?? 0,
            start ?? throw new FormatException(
                $"The arguments have no start. Give {SpaceOption}, {BaseOption} or {StartSaveOption}."),
            package);
    }

    /// <summary>The values that follow an option. A value cannot be empty or start with <c>--</c>.</summary>
    private static string[] Values(IReadOnlyList<string> args, ref int i, string option, int count)
    {
        var values = new string[count];
        for (var k = 0; k < count; k++, i++)
        {
            if (i >= args.Count || args[i].Length == 0 || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new FormatException(count == 1 ? $"{option} needs a value." : $"{option} needs {count} values.");
            values[k] = args[i];
        }
        return values;
    }

    private static PlayStart.InSpace Space(string[] values)
    {
        var numbers = new float[SpaceNumbers];
        for (var k = 0; k < SpaceNumbers; k++)
        {
            var text = values[1 + k];
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[k]) ||
                !float.IsFinite(numbers[k]))
                throw new FormatException($"The value '{text}' of {SpaceOption} is not a finite number.");
        }
        var orientation = new Quaternion(numbers[3], numbers[4], numbers[5], numbers[6]);
        if (MathF.Abs(orientation.Length() - 1) > UnitTolerance)
            throw new FormatException($"The orientation of {SpaceOption} is not a unit quaternion.");
        return new PlayStart.InSpace(values[0], new Vector3(numbers[0], numbers[1], numbers[2]), orientation);
    }

    private static T Once<T>(T? current, string option, T value) =>
        current == null ? value : throw new FormatException($"The arguments give {option} two times.");

    private static PlayStart OneStart(PlayStart? current, PlayStart start) =>
        current == null
            ? start
            : throw new FormatException(
                $"The arguments give two starts. Give one {SpaceOption}, one {BaseOption} or one {StartSaveOption}.");
}
