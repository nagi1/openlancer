using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The rule of the mods in the OpenLancer repo (decision D5): the files of such a mod are tracked files, so the editor
/// takes no edit of the mod until the user allows it for the session. The editor host and the headless host of the
/// agent server use the same rule (decision D738 of the MCP1 design). The headless host never allows the edits.
/// </summary>
public static class RepoMods
{
    /// <summary>Why the edits of a mod need the allowance of the user, or null when the mod is not in the OpenLancer
    /// repo. A caller adds where the user allows the edits.</summary>
    public static string? Reason(Mod mod) =>
        RepoCheck.RepoRoot(mod.Folder) == null
            ? null
            : $"The mod '{mod.Id}' is part of the OpenLancer repo, so its files are tracked files.";
}
