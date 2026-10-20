using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using OpenLancer.Editor;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Logs;
using OpenLancer.Mcp;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The HTTP host of the agent server of the editor (section 13.2, items 2 to 4 of the MCP1 design): the bind on
/// 127.0.0.1 also with the variables of ASP.NET Core in the environment, the token, the request checks, the fixed port,
/// the discovery file and the Console messages. The host runs in the test process. A session thread of a test host
/// takes the calls, as the UI thread of the editor does.
/// </summary>
[Collection(nameof(AgentServerTests))]
public sealed class AgentServerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A port that no program uses now.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A running server on a free port, its session thread, and an HTTP client.</summary>
    private sealed class Running : IAsyncDisposable
    {
        public Running(AgentLimits? limits = null)
        {
            Thread = SessionThread.Start(() => new TestAgentHost(null, AgentHostKind.Editor));
            var start = AgentServer.StartAsync(FreePort(), Thread.Queue, limits ?? new AgentLimits()).GetAwaiter().GetResult();
            Assert.Null(start.Problem);
            Server = start.Server!;
        }

        public SessionThread Thread { get; }

        public AgentServer Server { get; }

        public HttpClient Http { get; } = new();

        public string Url(string path = AgentServer.Endpoint) => $"http://127.0.0.1:{Server.Port}{path}";

        /// <summary>A request with the token, unless the test leaves it out.</summary>
        public HttpRequestMessage Request(HttpMethod method, string? path = null, string? token = "", string body = "{}")
        {
            var request = new HttpRequestMessage(method, Url(path ?? AgentServer.Endpoint));
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (token != null)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (token.Length == 0 ? Server.Token : token));
            return request;
        }

        public async Task<HttpStatusCode> Send(HttpRequestMessage request)
        {
            using var response = await Http.SendAsync(request, Token);
            Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Server.DisposeAsync();
            Thread.Dispose();
        }
    }

    [Fact]
    public async Task TheServerListensOnLoopbackOnlyAlsoWithTheVariablesOfAspNetCore()
    {
        var names = new[] { "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "Kestrel__Endpoints__X__Url", "URLS" };
        var old = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "0");
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__X__Url", "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable("URLS", "http://0.0.0.0:0");
            await using var running = new Running();

            Assert.Equal([$"http://127.0.0.1:{running.Server.Port}"], running.Server.Addresses);
            Assert.Equal(64, running.Server.Token.Length);
        }
        finally
        {
            foreach (var (name, value) in old)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task EachRequestNeedsTheTokenAndPassesTheChecks()
    {
        await using var running = new Running(new AgentLimits { RequestMegabytes = 1 });

        HttpRequestMessage With(string header, string value)
        {
            var request = running.Request(HttpMethod.Post);
            request.Headers.TryAddWithoutValidation(header, value);
            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await running.Send(running.Request(HttpMethod.Post, token: null)));
        Assert.Equal(HttpStatusCode.Unauthorized, await running.Send(running.Request(HttpMethod.Post, token: new string('0', 64))));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("Origin", "http://example.com")));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("X-Forwarded-For", "10.0.0.1")));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("Forwarded", "for=10.0.0.1")));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("X-Real-IP", "10.0.0.1")));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("Tailscale-User-Login", "someone")));
        var host = running.Request(HttpMethod.Post);
        host.Headers.Host = $"example.com:{running.Server.Port}";
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(host));
        var port = running.Request(HttpMethod.Post);
        port.Headers.Host = "127.0.0.1:1";
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(port));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await running.Send(running.Request(HttpMethod.Get)));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await running.Send(running.Request(HttpMethod.Delete)));
        Assert.Equal(HttpStatusCode.NotFound, await running.Send(running.Request(HttpMethod.Post, "/other")));
        Assert.Equal(HttpStatusCode.NotFound, await running.Send(running.Request(HttpMethod.Get, "/")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            await running.Send(running.Request(HttpMethod.Post, body: new string(' ', 1024 * 1024 + 1))));
        var localhost = running.Request(HttpMethod.Post, body:
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        localhost.Headers.Host = $"localhost:{running.Server.Port}";
        localhost.Headers.Accept.ParseAdd("application/json");
        localhost.Headers.Accept.ParseAdd("text/event-stream");
        Assert.Equal(HttpStatusCode.OK, await running.Send(localhost));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task AClientWithTheTokenCallsTheToolsOverHttp(string? protocol)
    {
        await using var running = new Running();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(running.Url()),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + running.Server.Token },
        });
        var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocol },
            cancellationToken: Token);
        await using (client)
        {
            var tools = await client.ListToolsAsync(cancellationToken: Token);
            var status = await client.CallToolAsync("studio_status", cancellationToken: Token);

            Assert.Equal(AgentTools.All.Count, tools.Count);
            Assert.Equal("editor", status.StructuredContent!.Value.GetProperty("host").GetString());
            Assert.StartsWith("Call studio_status first.", client.ServerInstructions);
        }
    }

    [Fact]
    public async Task ASecondServerOnAPortInUseDoesNotStartAndTakesNoOtherPort()
    {
        await using var first = new Running();
        using var thread = SessionThread.Start(() => new TestAgentHost(null, AgentHostKind.Editor));

        var second = await AgentServer.StartAsync(first.Server.Port, thread.Queue, new AgentLimits());

        Assert.Null(second.Server);
        Assert.True(second.PortInUse);
        Assert.Equal($"Port {first.Server.Port} is in use.", second.Problem);
    }

    [Fact]
    public void OnlyAnAddressOf127001IsALoopbackAddress()
    {
        Assert.True(AgentServer.IsLoopbackAddress("http://127.0.0.1:47815"));
        Assert.False(AgentServer.IsLoopbackAddress("http://0.0.0.0:47815"));
        Assert.False(AgentServer.IsLoopbackAddress("http://[::]:47815"));
        Assert.False(AgentServer.IsLoopbackAddress("http://localhost:47815"));
    }
}

/// <summary>The editor side of the agent server: the controller with the discovery file and the Console, the waits of
/// the dispatcher, the token helper, the Connect text and the settings.</summary>
[Collection(nameof(AgentServerTests))]
public sealed class AgentControllerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));

    private sealed class FakeEditor : IAgentEditor
    {
        public EditorSession? Session => null;

        public AgentSettings Settings { get; } = new();

        public ConsoleLog Log { get; } = new();

        public AgentView View => new("system:Li01", null, null);
    }

    private string Discovery => Path.Combine(folder, "agent", "editor.json");

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }

    [Fact]
    public void StartWritesTheDiscoveryFileAndStopDeletesIt()
    {
        var editor = new FakeEditor();
        editor.Settings.Port = FreePort();
        var ui = new QueuedUIThread();
        using var controller = new AgentController(editor, ui, () => default, Discovery, folder);

        controller.Start();
        ui.RunUntil(() => controller.State == AgentServerState.On, "the start of the server");
        var info = AgentDiscovery.Read(Discovery)!;
        var headers = AgentDiscovery.Headers(Discovery, $"http://127.0.0.1:{editor.Settings.Port}/mcp");
        controller.Stop();

        Assert.Equal(editor.Settings.Port, info.Port);
        Assert.Equal(Environment.ProcessId, info.ProcessId);
        Assert.Equal(64, info.Token.Length);
        Assert.Equal($$"""{"Authorization":"Bearer {{info.Token}}"}""", headers.Headers);
        Assert.False(File.Exists(Discovery));
        Assert.Equal(AgentServerState.Off, controller.State);
        Assert.Equal("Agent server: off", controller.StatusText);
        Assert.Equal([$"The agent server listens on port {editor.Settings.Port}.", "The agent server stopped."],
            editor.Log.Entries.Where(e => e.Source == AgentController.Source).Select(e => e.Message));
        Assert.Single(Directory.GetFiles(folder, "agent-*.jsonl"));
    }

    [Fact]
    public void APortInUseStopsTheStartAndTellsTheUser()
    {
        var editor = new FakeEditor();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            editor.Settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ui = new QueuedUIThread();
            using var controller = new AgentController(editor, ui, () => default, Discovery, folder);

            controller.Start();
            ui.RunUntil(() => controller.State != AgentServerState.Starting, "the end of the start");

            Assert.Equal(AgentServerState.PortInUse, controller.State);
            Assert.Equal($"Agent server: port {editor.Settings.Port} is in use", controller.StatusText);
            var entry = editor.Log.Entries.Single(e => e.Source == AgentController.Source);
            Assert.Equal(ConsoleSeverity.Error, entry.Severity);
            Assert.StartsWith($"Port {editor.Settings.Port} is in use, so the agent server did not start.", entry.Message);
            Assert.False(File.Exists(Discovery));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void TheTokenHelperGivesTheTokenOnlyToTheRunningServer()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        AgentDiscovery.Write(Discovery, new AgentDiscoveryInfo(47815, "abc", 42, now, "test", "act"));
        DateTime? Running(int id) => id == 42 ? now.AddMilliseconds(300) : null;

        Assert.Equal("""{"Authorization":"Bearer abc"}""", AgentDiscovery.Headers(Discovery, null, Running).Headers);
        Assert.NotNull(AgentDiscovery.Headers(Discovery, "http://localhost:47815/mcp", Running).Headers);
        Assert.Null(AgentDiscovery.Headers(Discovery, "http://127.0.0.1:47816/mcp", Running).Headers);
        Assert.Null(AgentDiscovery.Headers(Discovery, "http://example.com:47815/mcp", Running).Headers);
        Assert.Null(AgentDiscovery.Headers(Discovery, "https://127.0.0.1:47815/mcp", Running).Headers);
        Assert.Null(AgentDiscovery.Headers(Discovery, null, _ => null).Headers);
        Assert.Null(AgentDiscovery.Headers(Discovery, null, _ => now.AddMinutes(5)).Headers);
        Assert.Equal("No agent server of the editor runs. Start it in the Agent menu of the editor.",
            AgentDiscovery.Headers(Path.Combine(folder, "none.json"), null, Running).Problem);

        AgentDiscovery.Delete(Discovery, "other");
        Assert.True(File.Exists(Discovery));
        AgentDiscovery.Delete(Discovery, "abc");
        Assert.False(File.Exists(Discovery));
    }

    [Fact]
    public async Task ACallWaitsWhileTheUserEditsAndGivesBusyAfterTheLimit()
    {
        var clock = new FakeClock();
        var editing = true;
        using var dispatcher = new AgentDispatcher(new EditorAgentHost(new FakeEditor()), () => new EditorActivity(editing, false,
            false, false), null, clock);

        var call = dispatcher.Queue.CallAsync("studio_status", null, null, TestContext.Current.CancellationToken);
        Assert.False(dispatcher.Frame());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(dispatcher.Frame());
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var next = dispatcher.Queue.CallAsync("studio_status", null, null, TestContext.Current.CancellationToken);
        editing = false;
        Assert.True(dispatcher.Frame());

        Assert.Equal("busy", (string?)result.Content["code"]);
        Assert.StartsWith("The user edits a field or drags an item now.", (string?)result.Content["message"]);
        var status = await next.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("system:Li01", (string?)status.Content["view"]!["system"]);
        Assert.Equal("No profile is open. Open a profile in the editor, then try again.", (string?)status.Content["reason"]);
    }

    [Fact]
    public void EachActivityHasItsWaitReason()
    {
        Assert.Null(AgentDispatcher.WaitReason(default));
        Assert.Equal("A dialog of the editor is open.", AgentDispatcher.WaitReason(new EditorActivity(false, true, false, false)));
        Assert.Equal("The editor loads the game data now.", AgentDispatcher.WaitReason(new EditorActivity(false, false, true, false)));
        Assert.Equal("An edit of the user runs now.", AgentDispatcher.WaitReason(new EditorActivity(false, false, false, true)));
    }

    [Fact]
    public void TheEditorHostGivesTheConsoleWithItsFilters()
    {
        var editor = new FakeEditor();
        editor.Log.Add(ConsoleSeverity.Info, "Editor", "Opened.");
        editor.Log.Add(ConsoleSeverity.Warning, AgentController.Source, "A warning.");
        editor.Log.Add(ConsoleSeverity.Error, "Mods", "An error.");
        var host = new EditorAgentHost(editor);

        var all = host.Console(new ConsoleFilter(null, "info", false, null, 50));
        var warnings = host.Console(new ConsoleFilter(null, "warning", false, null, 50));
        var agent = host.Console(new ConsoleFilter("agent", "info", false, null, 50));
        var since = host.Console(new ConsoleFilter(null, "info", false, all[1].Sequence, 50));

        Assert.Equal(["Opened.", "A warning.", "An error."], all.Select(e => e.Message));
        Assert.Equal(["warning", "error"], warnings.Select(e => e.Severity));
        Assert.Equal(["A warning."], agent.Select(e => e.Message));
        Assert.Equal(["An error."], since.Select(e => e.Message));
        Assert.Equal(OpenLancer.Data.OriginKind.OpenLancer, all[0].Origin);
    }

    [Fact]
    public void TheConnectTextHasThePortAndTheTokenHelper()
    {
        var text = System.Text.Json.Nodes.JsonNode.Parse(AgentMenu.McpJson(47816, @"D:\Some Folder\oltool.exe"))!;
        var server = text["mcpServers"]!["openlancer"]!;

        Assert.Equal("http", (string?)server["type"]);
        Assert.Equal("http://127.0.0.1:47816/mcp", (string?)server["url"]);
        Assert.Equal("\"D:\\Some Folder\\oltool.exe\" mcp token", (string?)server["headersHelper"]);
    }

    [Fact]
    public void TheAgentSettingsKeepTheirValuesInRange()
    {
        var settings = EditorSettings.Parse("""
            { "Agent": { "Port": 0, "StartWithEditor": true, "HiddenGroups": ["Missions", "Studio", "Unknown"],
                         "Limits": { "MaxListRows": 99999, "CallsPerSecond": 0 } } }
            """, DateTime.UtcNow);

        Assert.Equal(AgentSettings.DefaultPort, settings.Agent.Port);
        Assert.True(settings.Agent.StartWithEditor);
        Assert.Equal([ToolGroup.Missions], settings.Agent.HiddenToolGroups);
        Assert.Equal(1000, settings.Agent.Limits.MaxListRows);
        Assert.Equal(1, settings.Agent.Limits.CallsPerSecond);
        settings.Agent.SetHidden(ToolGroup.Missions, false);
        settings.Agent.SetHidden(ToolGroup.Cutscenes, true);
        Assert.Equal([ToolGroup.Cutscenes], settings.Agent.HiddenToolGroups);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
