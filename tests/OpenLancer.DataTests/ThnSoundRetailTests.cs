using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Resources;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The sound of the preview on the retail data (retail test 2 of the M8b design): the cues of s002xe and the names that
/// the retail data has, the count of the START_SOUND events without a length, and the steps of the voice rule for the
/// voice lines of the GCS scripts in the context Script only.
/// </summary>
public sealed class ThnSoundRetailTests(ITestOutputHelper output)
{
    private const string VoicePrefix = "VoiceProfile_";

    static ThnSoundRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheRetailDataHasTheSoundsOfS002xeAndTheVoiceRuleFindsMostGcsLines()
    {
        var fs = FileSystem.FromPath(GameDataPaths.RequireBase());
        var resources = new ServerResourceManager(null, fs);
        // The resource manager is not disposable, but its physics shape pool is.
        using var shapes = resources.ConvexCollection;
        var data = new GameDataManager(new GameItemDb(fs), resources);
        data.LoadData(null);
        var lines = new List<(ConsoleSeverity Severity, string Message)>();
        var catalog = SoundCatalog.Of(data, (severity, message) => lines.Add((severity, message)));

        // s002xe: the premixed dialogue and two silent entries.
        var (s002xe, s002xeW) = Model(fs, ThnPatchRetailTests.S002xe);
        var cues = TimelineAudio.Cues(s002xe, s002xeW);
        foreach (var cue in cues)
            output.WriteLine($"s002xe cue: {cue.Sound} at {cue.Start:0.###} s to {cue.End:0.###} s, offset {cue.Offset:0.###} s, " +
                             $"loop {cue.Loop}, attenuation {cue.Attenuation}.");
        var playable = cues.Where(c => catalog.Playable(c, _ => null) != null).Select(c => c.Sound).Distinct().ToList();
        output.WriteLine($"s002xe: {cues.Count} cues of {cues.Select(c => c.Sound).Distinct().Count()} sounds. " +
                         $"Playable: {string.Join(", ", playable)}. Missing: {lines.Count(l => l.Severity == ConsoleSeverity.Warning)}.");
        Assert.Contains(cues, c => c.Sound.Equals("mix_s002xe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("mix_s002xe", playable, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(lines, l => l.Severity == ConsoleSeverity.Warning);
        // backgroundambient and backgroundmusic are silent null.wav entries: known, not playable, no warning.
        Assert.DoesNotContain("backgroundmusic", playable, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["mix_s002xe"], catalog.Preloads(cues, _ => null).Select(n => n.ToLowerInvariant()));

        // The START_SOUND events without a length, which play nothing, in all retail files.
        var (files, sounds, withoutLength) = (0, 0, 0);
        var voiceCues = new List<(string File, SoundCue Cue, ThnScriptModel W)>();
        foreach (var file in VfsFiles.Find(fs, ".thn"))
        {
            files++;
            var (model, working) = Model(fs, file);
            var starts = model.Events.Where(e => e.Kind == LibreLancer.Thn.EventTypes.StartSound).ToList();
            sounds += starts.Count;
            withoutLength += starts.Count(e => !e.IsBar);
            if (file.Contains(@"\GCS\", StringComparison.OrdinalIgnoreCase))
            {
                voiceCues.AddRange(TimelineAudio.Cues(model, working)
                    .Where(c => c.Speaker != null && c.Sound.StartsWith(VoicePrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(c => (file, c, working)));
            }
        }

        output.WriteLine($"{files} files: {sounds} START_SOUND events, {withoutLength} without a length.");

        // The voice rule in the context Script only: the scene object of a speaker is the entity of the script, with
        // its Actor and without a voice.
        var steps = voiceCues.GroupBy(v => TimelineAudio.SourceOf(v.Cue, name => Speaker(v.W, name),
            line => data.Items.Voices.FirstOrDefault(x => x.LinesByHash.ContainsKey(FLHash.CreateID(line)))?.Nickname).Rule)
            .ToDictionary(g => g.Key ?? "none", g => g.Count());
        var withFallback = voiceCues.Count(v => data.Items.Voices.Get("rvp146")?.LinesByHash
            .ContainsKey(FLHash.CreateID(v.Cue.Sound[VoicePrefix.Length..])) == true);
        var gcsFiles = voiceCues.Select(v => v.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        output.WriteLine($"GCS: {voiceCues.Count} VoiceProfile_ cues with a speaker in {gcsFiles} files. rvp146 has " +
                         $"{withFallback} of the lines.");
        foreach (var (rule, count) in steps)
            output.WriteLine($"  {count} cues: {rule}");
        lines.Clear();
        var playableLines = voiceCues.Count(v => catalog.Playable(v.Cue, name => Speaker(v.W, name)) != null);
        output.WriteLine($"  {playableLines} cues are playable. {lines.Count(l => l.Severity == ConsoleSeverity.Warning)} " +
                         "names are missing.");

        Assert.Equal(1506, files);
        Assert.True(voiceCues.Count > 0);
        // The step of the voice data finds more lines than the fallback of the engine.
        Assert.True(steps.GetValueOrDefault("the first voice of the voice data that has the line") > withFallback);
    }

    private static (TimelineModel Model, ThnScriptModel W) Model(FileSystem fs, string file)
    {
        var working = ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(fs.ReadAllBytes(file), file),
            ThnElementId.Counter());
        return (TimelineModel.Build(file, working, ThornEvaluator.Instance.Symbols, ScriptPlay.Normal), working);
    }

    /// <summary>The scene object of a speaker in the context Script only: the entity of the script with its Actor, and
    /// no voice. Null when the script has no entity with the name.</summary>
    private static (string? Voice, string? Actor)? Speaker(ThnScriptModel working, string name) =>
        working.EntityNamed(name) is { } entity
            ? (null, entity.Table["userprops"] is ThnTable { IsRecord: true } props
                ? (props.Record.LastOrDefault(f => f.Key.Equals("Actor", StringComparison.OrdinalIgnoreCase)).Value as ThnText)?.Value
                : null)
            : null;
}
