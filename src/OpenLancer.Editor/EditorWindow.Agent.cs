using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Logs;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor;

/// <summary>
/// The agent server of the editor (milestone MCP1): the seam of the editor host of the agent tools. The members of the
/// seam are explicit, so the public surface of the window does not grow.
/// </summary>
internal sealed partial class EditorWindow : IAgentEditor
{
    private AgentController agentController = null!;
    private AgentMenu agentMenu = null!;

    /// <summary>The agent server and its menu. The window makes them before its first frame.</summary>
    private void MakeAgent()
    {
        agentController = new AgentController(this, this, Activity,
            AgentDiscovery.PathOf(AgentDiscovery.DefaultFolder, System.Environment.ProcessId),
            AppHandler.LogFolder(Product.Name));
        agentMenu = new AgentMenu(this, agentController);
    }

    /// <summary>Starts the agent server at the start of the editor when the setting asks for it and the user saw the
    /// data notice (decisions D704 and D735).</summary>
    private void StartAgentWithEditor()
    {
        if (Settings.Agent is { StartWithEditor: true, NoticeShown: true })
        {
            agentController.Start();
        }
    }

    /// <summary>The text of the agent segment of the status bar, for example "Agent server: on, port 47815".</summary>
    public string AgentStatus => agentController.StatusText;

    /// <summary>True while the agent server runs.</summary>
    public bool AgentServerOn => agentController.State == AgentServerState.On;

    /// <summary>Opens the menu "Agent" as a popup, for a click on the status bar.</summary>
    public void OpenAgentMenu() => agentMenu.OpenPopup();

    /// <summary>Shows the Console tab with a search, for example the source of the agent messages.</summary>
    public void ShowConsoleSearch(string text)
    {
        if (Session == null)
        {
            return;
        }

        console.Search(text);
        layout.Show("console");
    }

    /// <summary>What the editor does now: an agent call waits while the user edits, a dialog is open, the data loads
    /// or an edit of the user runs (section 5.5 of the MCP1 design).</summary>
    private EditorActivity Activity() => new(IsEditing || AssetDrag.IsDragging, DialogOpen, Session?.IsBusy == true,
        Session?.Edits is { } edits && (edits.OpenBatch != null || edits.InTransaction));

    EditSession? IAgentEditor.Edits => Session?.Edits;

    string? IAgentEditor.EditReason => Session?.NoEditReason;

    string IAgentEditor.Name(int ids) => Session?.Name(ids) ?? "";

    string IAgentEditor.InfoText(int ids) => Session?.InfoText(ids) ?? "";

    AgentSettings IAgentEditor.Settings => Settings.Agent;

    ConsoleLog IAgentEditor.Log => Log;

    AgentView IAgentEditor.View => new(
        Scene.System is { } system ? ItemRefs.Format(new ItemReference(ItemRefKind.System, system.Nickname)) : null,
        Missions.OpenPath is { } script ? ItemRefs.Format(new ItemReference(ItemRefKind.Script, "") { Path = script }) : null,
        Cutscenes.Path is { } cutscene ? ItemRefs.Format(new ItemReference(ItemRefKind.Cutscene, "") { Path = cutscene }) : null);
}
