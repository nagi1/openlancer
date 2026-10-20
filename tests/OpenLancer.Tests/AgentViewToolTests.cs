using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Agent;
using OpenLancer.Mcp;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The view tools of section 7.7 of the MCP1 design with the views of a test editor: the check of the arguments, the
/// references that resolve on the session, the limits, the results with their images, and the hosts that have the
/// tools. The render bench of the editor renders the images (section 13.4).
/// </summary>
public sealed class AgentViewToolTests
{
    private static (TestWorkspace Ws, TestAgentHost Host, FakeAgentViews Views) Editor(AgentView? view = null)
    {
        var ws = SceneWorkspace.Create();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(ws.Session("act"), AgentHostKind.Editor)
        {
            TestViews = views,
            TestView = view ?? new AgentView("system:Li01", null, null),
        };
        return (ws, host, views);
    }

    private static readonly string[] ViewTools =
        ["open_view", "select_items", "render_scene", "render_cutscene", "capture_panel"];

    /// <summary>The server without a window has no views, so each view tool tells the agent to use the editor
    /// (decision D721).</summary>
    [Fact]
    public void TheServerWithoutAWindowGivesNotAvailable()
    {
        using var ws = SceneWorkspace.Create();
        var host = new TestAgentHost(ws.Session("act"));

        foreach (var tool in ViewTools)
        {
            var error = Error(host, tool);
            Assert.Equal(("not_available", "EditorOnly"), (error.Code, error.Detail));
            Assert.Equal($"Only the editor host has the tool {tool}. Start the agent server in the editor.", error.Message);
        }
    }

    [Fact]
    public void OpenViewOpensOneSystemMissionOrCutscene()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var system = Ok(host, "open_view", """{ "system": "li01" }""");
        var byRef = Ok(host, "open_view", """{ "system": "system:Li02" }""");
        var none = Error(host, "open_view");
        var two = Error(host, "open_view", """{ "system": "Li01", "cutscene": "thn:DATA\\x.thn" }""");
        var missing = Error(host, "open_view", """{ "system": "Li0x" }""");

        Assert.Equal(("system", "system:Li01"), ((string?)system["kind"], (string?)system["ref"]));
        Assert.NotNull(system["rev"]);
        Assert.Equal("system:Li02", (string?)byRef["ref"]);
        Assert.Equal([(ViewKind.System, "Li01"), (ViewKind.System, "Li02")], views.Opened);
        Assert.Equal(("invalid_argument", "BadArgument"), (none.Code, none.Detail));
        Assert.Equal("Give exactly one of the arguments system, mission and cutscene.", none.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (two.Code, two.Detail));
        Assert.Equal(("not_found", ItemRefs.NotFound), (missing.Code, missing.Detail));
    }

    [Fact]
    public void OpenViewResolvesTheMissionAndTheCutscene()
    {
        using var m = MissionEdits.Create();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(m.Session, AgentHostKind.Editor) { TestViews = views };
        using var thn = ThnSessions.Workspace();
        var cutsceneViews = new FakeAgentViews();
        var cutsceneHost = new TestAgentHost(thn.Session("act"), AgentHostKind.Editor) { TestViews = cutsceneViews };

        var mission = Ok(host, "open_view", $$"""{ "mission": {{Json("msn:" + MissionEdits.Script.ToLowerInvariant())}} }""");
        var noScript = Error(host, "open_view", """{ "mission": "msn:DATA\\MISSIONS\\none.ini" }""");
        var cutscene = Ok(cutsceneHost, "open_view",
            $$"""{ "cutscene": {{Json($"thn:{ThnSessions.ScriptPath}#entity/Cam_A")}} }""");

        Assert.Equal("mission", (string?)mission["kind"]);
        Assert.Equal(ViewKind.Mission, views.Opened.Single().Kind);
        Assert.Equal(MissionEdits.Script, views.Opened.Single().Target, ignoreCase: true);
        Assert.Equal("not_found", noScript.Code);
        Assert.Equal("cutscene", (string?)cutscene["kind"]);
        Assert.Equal($"thn:{ThnSessions.ScriptPath}", (string?)cutscene["ref"], ignoreCase: true);
        Assert.Equal(ViewKind.Cutscene, cutsceneViews.Opened.Single().Kind);
    }

    /// <summary>The editor has one selection until milestone M3b. Each reference becomes the item that the editor
    /// selects, with its section, its system and the row of a line.</summary>
    [Fact]
    public void SelectItemsSelectsOneItem()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var station = Ok(host, "select_items", """{ "refs": ["obj:li01_01"] }""");
        var unframed = Ok(host, "select_items", """{ "refs": ["zone:Zone_ring"], "frame": false }""");
        var two = Error(host, "select_items", """{ "refs": ["obj:Li01_01", "zone:Zone_ring"] }""");
        var group = Error(host, "select_items", """{ "refs": ["group:my_group"] }""");
        var empty = Error(host, "select_items", """{ "refs": [] }""");

        Assert.Equal("obj:Li01_01", (string?)station["ref"]);
        Assert.Equal("inspector", (string?)station["shown"]);
        Assert.True((bool)station["framed"]!);
        Assert.False((bool)unframed["framed"]!);
        var (target, frame) = views.Selected[0];
        Assert.Equal("Li01", target.System, ignoreCase: true);
        Assert.Equal("Li01_01", target.Section!.Id.Key);
        Assert.True(frame);
        Assert.False(views.Selected[1].Frame);
        Assert.Equal(("refused", "OneItem"), (two.Code, two.Detail));
        Assert.Equal("The editor selects one item at a time. Give one reference in refs.", two.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (group.Code, group.Detail));
        Assert.Equal(("invalid_argument", "BadArgument"), (empty.Code, empty.Detail));
        Assert.Equal(2, views.Selected.Count);
    }

    [Fact]
    public void SelectItemsResolvesMissionAndCutsceneReferences()
    {
        using var m = MissionEdits.Create();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(m.Session, AgentHostKind.Editor) { TestViews = views };
        using var thn = ThnSessions.Workspace();
        var session = thn.Session("act");
        var cutsceneViews = new FakeAgentViews();
        var cutsceneHost = new TestAgentHost(session, AgentHostKind.Editor) { TestViews = cutsceneViews };

        Ok(host, "select_items", $$"""{ "refs": [{{Json($"trg:{MissionEdits.Script}#ol_wave")}}] }""");
        Ok(host, "select_items", $$"""{ "refs": [{{Json("msn:" + MissionEdits.Script)}}] }""");
        Ok(cutsceneHost, "select_items", $$"""{ "refs": [{{Json($"thn:{ThnSessions.ScriptPath}#entity/Cam_A")}}] }""");
        Ok(cutsceneHost, "select_items", $$"""{ "refs": [{{Json($"thn:{ThnSessions.ScriptPath}")}}] }""");

        Assert.Equal("ol_wave", views.Selected[0].Target.Section!.Id.Key);
        Assert.Null(views.Selected[0].Target.System);
        Assert.Equal(MissionEdits.Script, views.Selected[1].Target.Script, ignoreCase: true);
        Assert.Equal(ThnSessions.Entity(session, "Cam_A"), cutsceneViews.Selected[0].Target.Element);
        Assert.Equal(ThnSessions.ScriptPath, cutsceneViews.Selected[1].Target.Cutscene, ignoreCase: true);
    }

    /// <summary>An image of the open system has the size 1024 by 640 and the zones by default. Without a camera, the image
    /// frames the whole system. The result has the image and the structured content of its schema.</summary>
    [Fact]
    public void RenderSceneRendersTheOpenSystemWithTheDefaults()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var result = Call(host, "render_scene");

        Assert.False(result.IsError, result.Text);
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find("render_scene")!.OutputSchema, result.Content));
        Assert.Equal([FakeAgentViews.Image], result.Images);
        var request = views.Scene!;
        Assert.Equal("Li01", request.System);
        Assert.Null(request.Camera);
        Assert.Empty(request.Frame);
        Assert.Equal((1024, 640, true), (request.Width, request.Height, request.Zones));
        Assert.Equal(200, request.MaxVisible);
        Assert.Equal("obj:Li01_01", (string?)result.Content["visible"]![0]!["ref"]);
        Assert.NotNull(result.Content["rev"]);
    }

    [Fact]
    public void RenderSceneTakesACameraOrItemsToFrame()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        Ok(host, "render_scene", """{ "camera": { "position": [0, 50000, 0], "look_at": [0, 0, 0] }, "width": 800, "height": 600, "zones": false }""");
        var first = views.Scene!;
        Ok(host, "render_scene", """{ "system": "Li01", "camera": { "position": [10, 20, 30], "look_at": [0, 0, 0], "fov": 40 } }""");
        var fov = views.Scene!.Camera!;
        Ok(host, "render_scene", """{ "frame": ["obj:Li01_01", "zone:Zone_ring", "system:Li01"] }""");
        var frame = views.Scene!.Frame;

        Assert.Equal(new Vector3(0, 50000, 0), first.Camera!.Position);
        Assert.Equal(ShotCamera.SceneFov(800f / 600), first.Camera.Fov);
        Assert.Equal((800, 600, false), (first.Width, first.Height, first.Zones));
        Assert.Equal(40, fov.Fov);
        Assert.Equal(["Li01_01", "Zone_ring", null], frame.Select(f => f.Section?.Id.Key));
        Assert.Equal("obj:Li01_01", frame[0].Ref);
    }

    [Fact]
    public void RenderSceneRefusesWrongArguments()
    {
        var (ws, host, _) = Editor();
        using var __ = ws;

        var both = Error(host, "render_scene", """{ "camera": { "position": [1, 2, 3], "look_at": [0, 0, 0] }, "frame": ["obj:Li01_01"] }""");
        var self = Error(host, "render_scene", """{ "camera": { "position": [1, 2, 3], "look_at": [1, 2, 3] } }""");
        var wide = Error(host, "render_scene", """{ "width": 2049 }""");
        var small = Error(host, "render_scene", """{ "height": 15 }""");
        var fov = Error(host, "render_scene", """{ "camera": { "position": [1, 2, 3], "look_at": [0, 0, 0], "fov": 170 } }""");
        var kind = Error(host, "render_scene", $$"""{ "frame": [{{Json("msn:" + MissionEdits.Script)}}] }""");
        var missing = Error(host, "render_scene", """{ "frame": ["obj:none"] }""");
        var noView = Error(new TestAgentHost(host.Session, AgentHostKind.Editor) { TestViews = new FakeAgentViews() },
            "render_scene");

        Assert.Equal(("invalid_argument", "BadArgument", "Give camera or frame, not both."), both);
        Assert.Equal(("invalid_argument", "BadArgument"), (self.Code, self.Detail));
        Assert.Equal(("limit", "RenderSize"), (wide.Code, wide.Detail));
        Assert.Equal("One image can have at most 2048 pixels on each side. Give a smaller width or height.", wide.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (small.Code, small.Detail));
        Assert.Equal("The argument 'camera.fov' needs a number from 5 to 150.", fov.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (kind.Code, kind.Detail));
        Assert.Equal(("not_found", ItemRefs.NotFound), (missing.Code, missing.Detail));
        Assert.Equal(("refused", "NoSystem"), (noView.Code, noView.Detail));
    }

    /// <summary>The render limit is a setting (decision D724). A hidden group gives not_available (decision
    /// D725).</summary>
    [Fact]
    public void TheRenderLimitAndTheHiddenGroupsApply()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;
        host.TestLimits = new AgentLimits { RenderPixels = 512 };

        var limited = Error(host, "render_scene", """{ "width": 513 }""");
        host.TestLimits = new AgentLimits();
        host.Hidden.Add(ToolGroup.Scene);
        var hidden = Error(host, "render_scene");

        Assert.Equal(("limit", "RenderSize"), (limited.Code, limited.Detail));
        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.Null(views.Scene);
        Assert.Equal(256, new AgentLimits { RenderPixels = 1 }.Clamped().RenderPixels);
        Assert.Equal(4096, new AgentLimits { RenderPixels = 100_000 }.Clamped().RenderPixels);
    }

    [Fact]
    public void RenderCutsceneTakesATimeOfTheScriptAndACamera()
    {
        using var thn = ThnSessions.Workspace();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(thn.Session("act"), AgentHostKind.Editor) { TestViews = views };
        var cutscene = Json($"thn:{ThnSessions.ScriptPath}");

        var start = Call(host, "render_cutscene", $$"""{ "cutscene": {{cutscene}} }""");
        var request = views.Cutscene!;
        var later = Ok(host, "render_cutscene", $$"""{ "cutscene": {{cutscene}}, "time": 6.5, "camera": "Cam_A", "width": 400, "height": 300 }""");
        var after = Error(host, "render_cutscene", $$"""{ "cutscene": {{cutscene}}, "time": 10.5 }""");
        var none = Error(host, "render_cutscene", """{ "cutscene": "thn:DATA\\none.thn" }""");

        Assert.False(start.IsError, start.Text);
        Assert.Equal([FakeAgentViews.Image], start.Images);
        Assert.Equal((0d, null, 1024, 640), (request.Time, request.Camera, request.Width, request.Height));
        Assert.Equal(ThnSessions.ScriptPath, request.Path, ignoreCase: true);
        Assert.Equal((6.5, "Cam_A", 400, 300), (views.Cutscene!.Time, views.Cutscene.Camera, views.Cutscene.Width,
            views.Cutscene.Height));
        Assert.Equal("Cam_A", (string?)later["camera"]);
        Assert.Equal(("invalid_argument", "BadArgument"), (after.Code, after.Detail));
        Assert.Equal("The cutscene ends at 10 s. Give a time from 0 to 10.", after.Message);
        Assert.Equal("not_found", none.Code);
    }

    [Fact]
    public void CapturePanelTakesOneOfTheSixPanels()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var console = Call(host, "capture_panel", """{ "panel": "console" }""");
        var other = Error(host, "capture_panel", """{ "panel": "assets" }""");

        Assert.False(console.IsError, console.Text);
        Assert.Equal(AgentPanel.Console, views.Panel);
        Assert.Equal("console", (string?)console.Content["panel"]);
        Assert.Equal([FakeAgentViews.Image], console.Images);
        Assert.Equal(("invalid_argument", "BadArgument"), (other.Code, other.Detail));
    }

    /// <summary>The name of each panel of the tool is the id of its panel in the layout, so the error of a hidden panel
    /// names the panel as the agent named it.</summary>
    [Fact]
    public void EachPanelOfTheToolIsAPanelOfTheLayout()
    {
        foreach (var panel in Enum.GetValues<AgentPanel>())
            Assert.Equal(AgentTools.Snake(panel.ToString()), EditorAgentViews.PanelIdOf(panel));
    }

    /// <summary>The binding gives the JSON as a text block and each image as an image block after it (section 5.7 and
    /// 7.7 of the MCP1 design). The images do not count for the limit of the text, and the revision keeps them.</summary>
    [Fact]
    public void TheImageOfAResultFollowsItsText()
    {
        var image = new ToolImage([1, 2, 3, 4], 2, 1);
        var result = ToolResult.Of(new PanelShotView(AgentPanel.Scene, 2, 1), image).WithRevision(7);

        var protocol = McpBinding.Protocol(result);
        var limited = result.Limited(new AgentLimits { ResultKilobytes = 16 });

        Assert.Equal(2, protocol.Content.Count);
        Assert.Equal(result.Text, Assert.IsType<TextContentBlock>(protocol.Content[0]).Text);
        var block = Assert.IsType<ImageContentBlock>(protocol.Content[1]);
        Assert.Equal("image/png", block.MimeType);
        Assert.Equal(image.Png, block.DecodedData.ToArray());
        Assert.Equal("7", (string?)result.Content["rev"]);
        Assert.Same(result, limited);
        Assert.Equal([image], result.Images);
        Assert.Single(McpBinding.Protocol(ToolResult.Of(new PanelShotView(AgentPanel.Scene, 2, 1))).Content);
    }
}
