using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Media;
using LibreLancer.Sounds;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The sounds and the voice lines of one load of the game data, as the preview looks for them before it gives a name to
/// the engine (section 5.3 of the M8b design). A name that the data does not have gives one warning, and the engine
/// never gets it. Else the engine writes a warning at each start (GameDataManager.cs:239-250). An entry whose file is
/// <c>audio\null.wav</c> is silent without a warning, as in the engine (SoundManager.cs:193-197).
/// </summary>
/// <param name="entry">Null when the audio data has no entry with a name, true for a silent entry, else false.</param>
/// <param name="voiceLine">True when the voice data has a voice with a line, and the voice has its file.</param>
/// <param name="voiceWithLine">The first voice of the voice data that has a line, or null.</param>
/// <param name="log">Writes a line for the Console: a warning, or an information line.</param>
internal sealed class SoundCatalog(Func<string, bool?> entry, Func<string, string, bool> voiceLine,
    Func<string, string?> voiceWithLine, Action<ConsoleSeverity, string> log)
{
    private const string NullFile = "audio/null.wav";

    // The names that a warning or an information line already gave.
    private readonly HashSet<string> warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> told = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The catalog of a load of the game data: its audio entries, and its voices with their lines and
    /// files.</summary>
    public static SoundCatalog Of(GameDataManager data, Action<ConsoleSeverity, string> log)
    {
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in data.AllSounds)
        {
            entries[entry.Nickname] =
                entry.File?.Replace('\\', '/').Equals(NullFile, StringComparison.OrdinalIgnoreCase) == true;
        }

        return new SoundCatalog(name => entries.TryGetValue(name, out var silent) ? silent : null,
            (voice, line) => data.Items.Voices.Get(voice) is { } found &&
                             found.LinesByHash.ContainsKey(FLHash.CreateID(line)) &&
                             data.GetVoicePath(voice) is { } path && data.VFS.FileExists(path),
            line => data.Items.Voices.FirstOrDefault(v => v.LinesByHash.ContainsKey(FLHash.CreateID(line)))?.Nickname,
            log);
    }

    /// <summary>
    /// What a cue plays when the engine can play it, or null: for a name that the data does not have, with one warning
    /// for each name, and for a silent entry. The first play of a voice line tells which voice the preview took.
    /// </summary>
    /// <param name="scene">The voice and the actor of a scene object, or null.</param>
    public SoundSource? Playable(SoundCue cue, Func<string, (string? Voice, string? Actor)?> scene)
    {
        var source = TimelineAudio.SourceOf(cue, scene, voiceWithLine);
        var found = source.Voice == null ? entry(source.Name) : voiceLine(source.Voice, source.Name) ? false : null;
        if (found == null)
        {
            if (warned.Add(source.Text))
            {
                log(ConsoleSeverity.Warning, $"The game data has no sound {source.Text}.");
            }

            return null;
        }

        if (found == true)
        {
            // A silent entry.
            return null;
        }

        if (source.Rule != null && told.Add($"{cue.Sound} {source.Voice}"))
        {
            log(ConsoleSeverity.Info, $"The preview plays {cue.Sound} with the voice {source.Voice}: {source.Rule}.");
        }

        return source;
    }

    /// <summary>The sounds of the cues to decode before the first play: each sound that the data has, that is not
    /// silent, and that is not a voice line. A voice line decodes at its first start.</summary>
    public IEnumerable<string> Preloads(IEnumerable<SoundCue> cues, Func<string, (string? Voice, string? Actor)?> scene) =>
        cues.Select(c => TimelineAudio.SourceOf(c, scene, voiceWithLine))
            .Where(s => s.Voice == null && entry(s.Name) == false)
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The engine output of the sound of the preview (section 5.3 of the M8b design): one <see cref="AudioManager"/> for
/// the editor window, made at the first use, and one <see cref="SoundManager"/> for each load of the game data, as
/// LancerEdit has them. The engine disables sound when the machine has no audio device.
/// </summary>
/// <remarks>A new load of the game data gets a new sound manager. The cache of a manager keeps the decoded sounds and
/// voice files of its data, so a manager of the old data would play the old sound of a changed <c>[Sound]</c> entry
/// (SoundManager.cs:35, :49).</remarks>
internal sealed class CutsceneSound(EditorWindow window) : ISoundOutput, IDisposable
{
    /// <summary>The source of the Console lines of the sound.</summary>
    public const string Source = "Sound";

    private AudioManager? audio;
    private GameDataManager? data;
    private SoundManager? manager;
    private SoundCatalog? catalog;

    // The sound of the Preview row of the Inspector, or null.
    private SoundInstance? single;

    /// <summary>Gives the voice and the actor of a scene object of the visible player, or null.</summary>
    public Func<string, (string? Voice, string? Actor)?> Scene { get; set; } = _ => null;

    /// <summary>
    /// Uses the sounds of a load of the game data. A new load disposes the manager of the old load and makes a new one.
    /// Stop each cue of the old load first (<see cref="TimelineAudio.StopAll"/>). Null disposes the manager.
    /// </summary>
    public void Use(GameDataManager? gameData)
    {
        if (ReferenceEquals(data, gameData))
        {
            return;
        }

        StopSingle();
        manager?.Dispose();
        (manager, catalog, data) = (null, null, gameData);
        if (gameData == null)
        {
            return;
        }

        audio ??= new AudioManager(window);
        manager = new SoundManager(gameData, audio, window);
        catalog = SoundCatalog.Of(gameData, (severity, message) => window.Log.Add(severity, Source, message));
    }

    /// <summary>Starts the decode of each sound of the cues that is not a voice line, so that the first play does not
    /// wait for it (SoundManager.cs:146-159).</summary>
    public void Preload(IEnumerable<SoundCue> cues)
    {
        if (manager == null || catalog == null)
        {
            return;
        }

        foreach (var name in catalog.Preloads(cues, Scene))
        {
            manager.LoadSound(name);
        }
    }

    public object? Start(SoundCue cue, double offset, float pitch)
    {
        if (Instance(cue) is not { } instance ||
            TimelineAudio.OffsetIn(offset, instance.Length, cue.Loop) is not { } at)
        {
            return null;
        }

        instance.SetPitch(pitch);
        instance.Play(cue.Loop, (float)at);
        // The level that the game gives the sound from its first update on (ThnSound.cs:37).
        instance.SetAttenuation(cue.Attenuation);
        return instance;
    }

    public void SetPitch(object handle, float pitch) => ((SoundInstance)handle).SetPitch(pitch);

    public void Stop(object handle) => ((SoundInstance)handle).Stop();

    /// <summary>Plays the sound of a SOUND entity once from its start, with the voice rule, for the Preview row of the
    /// Inspector. A sound that plays from this row stops first.</summary>
    public void PlayOnce(SoundCue cue)
    {
        StopSingle();
        if (Instance(cue) is { } instance)
        {
            instance.Play(false, 0);
            instance.SetAttenuation(cue.Attenuation);
            single = instance;
        }
    }

    /// <summary>Stops the sound of the Preview row.</summary>
    public void StopSingle()
    {
        single?.Stop();
        single = null;
    }

    /// <summary>Disposes the sound manager and the audio of the window.</summary>
    public void Dispose()
    {
        Use(null);
        audio?.Dispose();
        audio = null;
    }

    /// <summary>A new sound instance of a cue, or null.</summary>
    private SoundInstance? Instance(SoundCue cue) =>
        manager != null && catalog?.Playable(cue, Scene) is { } source
            ? manager.GetInstance(source.Voice, source.Name, cue.Attenuation, -1, -1, null)
            : null;
}
