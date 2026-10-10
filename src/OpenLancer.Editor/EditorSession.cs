using System;
using System.Collections.Generic;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Infocards;
using LibreLancer.Resources;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using OpenLancer.Editor.Editing;

namespace OpenLancer.Editor;

/// <summary>
/// One open profile: its edit session, its resources, and the engine data that loads from the edit view: the base
/// data with the mods up to the active mod (decision D2).
/// </summary>
internal sealed class EditorSession : IDisposable
{
    private readonly Dictionary<int, string> infoTexts = [];

    /// <param name="repoRoot">The OpenLancer repo that holds the active mod, or null (see <see cref="RepoCheck"/>).</param>
    public EditorSession(EditSession edits, GameResourceManager resources, string? repoRoot)
    {
        Edits = edits;
        Resources = resources;
        RepoRoot = repoRoot;
        edits.Changed += _ => Touch();
    }

    public EditSession Edits { get; }

    public ModProfile Profile => Edits.Profile;

    public GameResourceManager Resources { get; }

    /// <summary>The engine data. It is null until the background load ends, and stays null when it fails.</summary>
    public GameDataManager? GameData { get; private set; }

    /// <summary>
    /// True while the engine data loads in the background. The load uses <see cref="Resources"/>, so the session must
    /// not be disposed before the load ends.
    /// </summary>
    public bool IsLoading { get; private set; } = true;

    /// <summary>True while the engine data loads again after a save.</summary>
    public bool IsReloading { get; private set; }

    /// <summary>True while a load or a reload runs. The profile cannot close and no model loads until it ends.</summary>
    public bool IsBusy => IsLoading || IsReloading;

    /// <summary>A number that changes each time the edit view or the text of the active mod changes.</summary>
    public int Version { get; private set; }

    /// <summary>The number of saves of this session that changed files.</summary>
    public int Saves { get; private set; }

    /// <summary>The OpenLancer repo that holds the active mod, or null.</summary>
    public string? RepoRoot { get; }

    /// <summary>True after the user allowed the edits of a mod in the OpenLancer repo for this session (decision
    /// D5).</summary>
    public bool RepoEditsAllowed { get; set; }

    /// <summary>True when the active mod is in the OpenLancer repo and the user did not allow its edits for this
    /// session yet.</summary>
    public bool NeedsRepoConfirmation => RepoRoot != null && !RepoEditsAllowed;

    /// <summary>True when the recovery file is older than the edits (decision D8).</summary>
    public bool RecoveryOutdated { get; private set; }

    /// <summary>The time of the last edit, undo or redo.</summary>
    public DateTime LastChange { get; private set; }

    /// <summary>Why the editor takes no edits now, or null.</summary>
    public string? NoEditReason =>
        Edits.ReadOnlyReason ?? (NeedsRepoConfirmation
            ? $"The mod '{Edits.ActiveMod!.Id}' is part of the OpenLancer repo, so its files are tracked files. Allow " +
              "its edits for this session first."
            : null);

    public bool CanEdit => NoEditReason == null;

    /// <summary>Notes a change of the edit view or the text.</summary>
    public void Touch()
    {
        Version++;
        RecoveryOutdated = true;
        LastChange = DateTime.UtcNow;
    }

    /// <summary>Notes that the recovery file now has the edits, or that no recovery file is needed.</summary>
    public void RecoveryWritten() => RecoveryOutdated = false;

    /// <summary>
    /// Notes a save. The save makes W again from the saved files, so the views of the edit view change. Returns the
    /// number of the save, or 0 when it changed no file.
    /// </summary>
    public int Saved(bool changedFiles)
    {
        Version++;
        RecoveryOutdated = false;
        return changedFiles ? ++Saves : 0;
    }

    /// <summary>Ends the load. Call it on the UI thread, with the data, or with null when the load failed.</summary>
    public void LoadEnded(GameDataManager? data)
    {
        GameData = data;
        IsLoading = false;
    }

    public void ReloadStarted() => IsReloading = true;

    /// <summary>Ends a reload. Call it on the UI thread. A null result keeps the old data.</summary>
    public void ReloadEnded(GameDataManager? data)
    {
        if (data != null)
        {
            GameData = data;
            infoTexts.Clear();
        }

        IsReloading = false;
    }

    /// <summary>
    /// The text of a string resource, or an empty string when the id is not above 0 or is unknown. A text with only
    /// white space counts as no text, so that the item shows its nickname.
    /// </summary>
    public string Name(int ids)
    {
        var text = ids <= 0 || GameData == null ? null : GameData.Items.Ini.Infocards.GetStringResource(ids);
        return string.IsNullOrWhiteSpace(text) ? "" : text;
    }

    /// <summary>
    /// The plain text of an infocard, or an empty string when the id is not above 0 or is unknown. The infocard XML is
    /// parsed once for each id.
    /// </summary>
    public string InfoText(int ids)
    {
        if (ids <= 0 || GameData == null)
        {
            return "";
        }

        if (!infoTexts.TryGetValue(ids, out var text))
        {
            var xml = GameData.Items.Ini.Infocards.GetXmlResource(ids);
            text = xml == null ? "" : PlainText(xml);
            infoTexts[ids] = text;
        }

        return text;
    }

    /// <summary>The plain text of an infocard in RDL XML.</summary>
    public static string PlainText(string xml) => RDLParse.Parse(xml).ExtractText().TrimEnd();

    /// <summary>The display name of an item, or an empty string when it has no name.</summary>
    public string ItemName(object? item) => item is SectionRef section ? SectionName(section) : Name(NameIds(item));

    /// <summary>
    /// The name of a section of the edit view: the text of its <c>ids_name</c>, from the text of the active mod or from
    /// the game data. An empty string when it has no name.
    /// </summary>
    public string SectionName(SectionRef section) => Edits.Get(section) is { } view ? NameOf(view) : "";

    /// <summary>The name of a section: the text of its <c>ids_name</c>, or an empty string.</summary>
    public string NameOf(SectionView view)
    {
        if (view.Lines("ids_name") is not [{ Values: [var value, ..] }, ..])
        {
            return "";
        }

        if (StringPlaceholder.TryParse(FieldEdit.FormatValue(value), out var placeholder))
        {
            return Edits.Text(placeholder) ?? "";
        }

        return value.TryToInt32(out var id) ? Name(id) : "";
    }

    /// <summary>The string resource id of the name of an item, or 0 when the item has no name.</summary>
    public static int NameIds(object? item) => item switch
    {
        SystemObject obj => obj.IdsName,
        Zone zone => zone.IdsName,
        NamedItem named => named.IdsName,
        _ => 0,
    };

    public void Dispose() => Resources.Dispose();
}
