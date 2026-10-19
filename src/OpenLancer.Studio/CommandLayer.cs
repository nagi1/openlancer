// The shared command layer (section 4.0 of the M6b design, decisions D686 and D701).
//
// The rule of the layer:
//
// 1. The editor UI and the agent tools call commands, queries and play requests. They never change the edit session
//    in another way.
// 2. The commands call OpenLancer.Data (DA) and OpenLancer.Runtime (RT).
// 3. DA and this project (ST) make no ImGui call and have no reference to the MCP SDK. DA has no reference to ST.
// 4. A command lives in the lowest project that has what it needs. The mission commands need only DA, so they live in
//    DA (OpenLancer.Data.Missions.Editing). The scene, data and cutscene commands live here, because some of them need
//    RT.
// 5. The contract of the layer is in DA: EditOrigin, EditBatch, IBatchCheck and EditSession.Revision in
//    OpenLancer.Data.Editing, and EditPlan, EditResult, EditError, ItemRefs and InstallPaths in
//    OpenLancer.Data.Editing.Commands.
// 6. Each request runs on the thread that owns the session. One gesture of the user, or one tool call of an agent, is
//    one batch and one entry of the edit history.
//
// The namespace OpenLancer.Studio.Commands holds the commands and the edit helpers that have no ImGui call. Milestone
// MCP1-0 moved the helpers here from the editor. The editor keeps the dialogs, the selection and the drawing.
