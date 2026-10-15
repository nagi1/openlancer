using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Runtime.Validation;

/// <summary>
/// The error checks of the cutscenes that the mods add or change (section 9 of the M8 design). The check reads each
/// listed cutscene in the lower view and in the modded view, and reports the errors that only the modded view has.
/// </summary>
/// <remarks>
/// <para>A script that does not run is one error, for example a text that does not compile, or a number that is not
/// finite. Else <see cref="ThnChecks"/> checks the tree with the game data of the view. A script text with a
/// character outside ASCII is an error too, because THN text is ASCII (decision D60). The engine runs such a text,
/// but the VM reads the texts of the script as ASCII (Undump.cs:69).</para>
/// <para>The rooms of a base fill in the <c>$terrain_*</c> templates (ThnRoomHandler.cs:23-51). So, for such a
/// template, the check reads the INI lines that name the cutscene (<see cref="ThnReferences"/>), and checks the
/// template of each base that plays it. The INI lines also tell when the game plays the cutscene only in space, where
/// it makes no COMPOUND. The check reads the INI lines only for such a template, or for a COMPOUND with a
/// problem.</para>
/// <para>The check reads only the listed paths. A check of every cutscene of both views adds about 2.5 s to each
/// validation.</para>
/// </remarks>
public static class CutsceneCheck
{
    private static readonly IReadOnlyDictionary<string, string> NoSubstitutions = new Dictionary<string, string>();

    /// <summary>The errors of the listed cutscenes that the lower view does not have.</summary>
    /// <param name="paths">The install paths of the cutscenes to check, for example the <c>.thn</c> files of the
    /// merged view of the mods.</param>
    /// <param name="lowerData">The game data of the lower view, or null when it did not load. The checks of templates
    /// and costumes then do not run.</param>
    /// <param name="moddedData">The game data of the modded view, or null when it did not load.</param>
    public static IReadOnlyList<Diagnostic> NewProblems(FileSystem lower, FileSystem modded, IEnumerable<string> paths,
        GameItemDb? lowerData, GameItemDb? moddedData)
    {
        var lowerView = new View(lower, lowerData);
        var moddedView = new View(modded, moddedData);
        var problems = new List<Diagnostic>();
        foreach (var path in paths.Distinct(VfsPath.Comparer).Order(StringComparer.OrdinalIgnoreCase))
            problems.AddRange(Diagnostic.NewIn(lowerView.Errors(path), moddedView.Errors(path)));
        return problems;
    }

    /// <summary>The errors of one cutscene of a view, or none when the view has no file at the path.</summary>
    /// <param name="data">The game data of the view, or null to skip the checks of templates and costumes.</param>
    public static IReadOnlyList<Diagnostic> Errors(FileSystem fs, string path, GameItemDb? data) =>
        new View(fs, data).Errors(path);

    /// <summary>The cutscenes, the INI lines and the game data of one view.</summary>
    private sealed class View(FileSystem fs, GameItemDb? data)
    {
        private ThnReferences? references;

        /// <summary>The game data of the view, or null when it did not load.</summary>
        public GameItemDb? Data => data;

        /// <summary>The INI lines of the view that name cutscenes. The scan runs once, when a check first needs
        /// it.</summary>
        public ThnReferences References => references ??= ThnReferences.Scan(fs);

        public IReadOnlyList<Diagnostic> Errors(string path)
        {
            if (!fs.FileExists(path))
                return [];
            var bytes = fs.ReadAllBytes(path);
            var place = new ThnLocation(path);
            var errors = new List<Diagnostic>();
            if (!IsBytecode(bytes) && Array.FindIndex(bytes, b => b > 0x7F) is var at and >= 0)
            {
                var line = bytes.AsSpan(0, at).Count((byte)'\n') + 1;
                errors.Add(Diagnostic.Error(path, $"Line {line} has a character outside ASCII. The text of a THN script is " +
                                                  "ASCII.") with { Cutscene = place });
            }
            ThnTable globals;
            try
            {
                globals = ThornEvaluator.Instance.Evaluate(bytes, path);
            }
            catch (ThnEvaluationException e)
            {
                // The message starts with the path.
                errors.Add(Diagnostic.Error(path, e.Message) with { Cutscene = place });
                return errors;
            }
            errors.AddRange(ThnChecks.Errors(path, globals, ThornEvaluator.Instance, new Script(this, path)));
            return errors;
        }

        /// <summary>True for Lua bytecode. The VM compiles each other file as Lua text (ThornRuntime.cs:49-59,
        /// Undump.cs).</summary>
        private static bool IsBytecode(byte[] bytes) => bytes is [0x1B, (byte)'L', (byte)'u', (byte)'a', ..];
    }

    /// <summary>The game data around one cutscene of a view: the templates and the costumes of the loaded data, and the
    /// INI lines that name the cutscene.</summary>
    private sealed class Script(View view, string path) : IThnGameData
    {
        private IReadOnlyList<IReadOnlyDictionary<string, string>>? bases;

        public bool MakesCompounds => !view.References.OnlyInSpace(path);

        public ThnItemState? Item(ThnItemKind kind, string template)
        {
            if (view.Data is not { } db)
                return null;
            if (!template.StartsWith('$'))
                return State(db, kind, template);
            // Only a template that starts with $ has a substitution.
            bases ??= Substitutions(db);
            return bases.Min(b => State(db, kind, b.TryGetValue(template, out var filled) ? filled : template));
        }

        /// <summary>The <c>$terrain_*</c> templates of each base whose rooms play the cutscene, or no templates when no
        /// room plays it.</summary>
        private IReadOnlyList<IReadOnlyDictionary<string, string>> Substitutions(GameItemDb db)
        {
            var substitutions = view.References.Rooms(path).Select(r => r.Base).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(b => db.Bases.Get(b)).OfType<Base>().Select(PreviewContext.Substitutions).ToList();
            return substitutions.Count > 0 ? substitutions : [NoSubstitutions];
        }

        /// <summary>The state of an item, as the engine finds it (ThnScriptInstance.cs:120-155, :260-264).</summary>
        private static ThnItemState State(GameItemDb db, ThnItemKind kind, string nickname) => kind switch
        {
            ThnItemKind.Archetype => Model(db.Archetypes.Get(nickname), a => a.ModelFile),
            ThnItemKind.Ship => Model(db.Ships.Get(nickname), s => s.ModelFile),
            ThnItemKind.Equipment => Model(db.Equipment.Get(nickname), e => e.ModelFile),
            ThnItemKind.Asteroid => Model(db.Asteroids.Get(nickname), a => a.ModelFile),
            ThnItemKind.Costume => Model(db.Costumes.Get(nickname), c => c.Body),
            ThnItemKind.Room => db.Ini.PetalDb.Rooms.ContainsKey(nickname) ? ThnItemState.Ready : ThnItemState.Missing,
            ThnItemKind.Cart => db.Ini.PetalDb.Carts.ContainsKey(nickname) ? ThnItemState.Ready : ThnItemState.Missing,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        private static ThnItemState Model<T>(T? item, Func<T, object?> model) where T : class =>
            item == null ? ThnItemState.Missing : model(item) == null ? ThnItemState.NoModel : ThnItemState.Ready;
    }
}
