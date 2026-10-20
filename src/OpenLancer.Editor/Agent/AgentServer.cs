using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenLancer.Mcp;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>The result of <see cref="AgentServer.StartAsync"/>: the server, or why it did not start.</summary>
/// <param name="PortInUse">True when another program uses the port. The server never moves to another port.</param>
internal sealed record AgentServerStart(AgentServer? Server, string? Problem, bool PortInUse);

/// <summary>
/// The HTTP host of the agent server of the editor (section 6.1 of the MCP1 design): Kestrel with the Streamable HTTP
/// transport of the SDK, on 127.0.0.1 and one fixed port only. The token of each start is the main guard. The bind, the
/// check of the listen addresses, the <c>Host</c>, <c>Origin</c> and forward header checks are extra guards.
/// </summary>
/// <remarks>
/// <para>The host uses an empty builder: no environment variable, no settings file and no command line can add an
/// endpoint (decision D707). The only endpoint comes from code. After the start, the host reads the addresses of all
/// listeners and stops at once when one is not 127.0.0.1.</para>
/// <para>The SDK serves the tools without a protocol session (the stateless mode), so each request carries what it
/// needs, and GET gets no stream.</para>
/// </remarks>
internal sealed class AgentServer : IAsyncDisposable
{
    /// <summary>The path of the one endpoint.</summary>
    public const string Endpoint = "/mcp";

    /// <summary>The headers that a proxy or a tunnel adds. A request with one of them gets 403.</summary>
    private static readonly string[] ForwardHeaders = ["Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Real-IP"];

    private readonly WebApplication app;

    private AgentServer(WebApplication app, int port, string token)
    {
        this.app = app;
        Port = port;
        Token = token;
    }

    public int Port { get; }

    /// <summary>The token of this start: 256 random bits as hexadecimal text (decision D706).</summary>
    public string Token { get; }

    /// <summary>Starts the server on 127.0.0.1 and the port. It does not take another port when the port is in
    /// use.</summary>
    /// <param name="queue">The queue of the editor host. Each tool call waits in it for the UI thread.</param>
    public static async Task<AgentServerStart> StartAsync(int port, ToolQueue queue, AgentLimits limits)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(AgentServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = BodyLimit(limits);
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddMcpServer(options => McpBinding.Configure(options, queue))
            .WithHttpTransport(http => http.Stateless = true);
        var app = builder.Build();
        app.Use(new RequestChecks(port, token, BodyLimit(limits)).Invoke);
        app.UseRouting();
        app.MapMcp(Endpoint);
        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (IOException e) when (e.InnerException is AddressInUseException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            return new AgentServerStart(null, $"Port {port} is in use.", PortInUse: true);
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            return new AgentServerStart(null, e.Message, PortInUse: false);
        }

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                        ?? (ICollection<string>)[];
        if (addresses.Count == 0 || addresses.Any(a => !IsLoopbackAddress(a)))
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            return new AgentServerStart(null, $"The agent server listened on {string.Join(", ", addresses)}, which is not " +
                                              "this computer only. The editor stopped the server.", PortInUse: false);
        }
        return new AgentServerStart(new AgentServer(app, port, token), null, PortInUse: false);
    }

    /// <summary>The listen addresses of the running server.</summary>
    public IReadOnlyList<string> Addresses =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];

    /// <summary>Stops the server. A request that runs now ends.</summary>
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>True for an address of 127.0.0.1 with any port, as Kestrel gives it, for example
    /// <c>http://127.0.0.1:47815</c>.</summary>
    internal static bool IsLoopbackAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host == "127.0.0.1";

    private static long BodyLimit(AgentLimits limits) => limits.RequestMegabytes * 1024L * 1024L;

    /// <summary>
    /// The checks of each request, before the SDK and before the routing, on each path (section 6.1, item 8 of the MCP1
    /// design). The server sends no CORS header.
    /// </summary>
    private sealed class RequestChecks(int port, string token, long bodyLimit)
    {
        private readonly byte[] expected = Encoding.UTF8.GetBytes("Bearer " + token);

        public Task Invoke(HttpContext context, RequestDelegate next)
        {
            var request = context.Request;
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            {
                context.Abort();
                return Task.CompletedTask;
            }
            if (!(request.Host.Port == port &&
                  (request.Host.Host == "127.0.0.1" || request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))))
                return Status(context, StatusCodes.Status403Forbidden);
            if (request.Headers.ContainsKey("Origin"))
                return Status(context, StatusCodes.Status403Forbidden);
            if (ForwardHeaders.Any(request.Headers.ContainsKey) ||
                request.Headers.Keys.Any(k => k.StartsWith("Tailscale-", StringComparison.OrdinalIgnoreCase)))
                return Status(context, StatusCodes.Status403Forbidden);
            if (!Authorized(request))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Status(context, StatusCodes.Status401Unauthorized);
            }
            if (!request.Path.Equals(Endpoint, StringComparison.Ordinal))
                return Status(context, StatusCodes.Status404NotFound);
            if (!HttpMethods.IsPost(request.Method))
            {
                context.Response.Headers.Allow = "POST";
                return Status(context, StatusCodes.Status405MethodNotAllowed);
            }
            if (request.ContentLength > bodyLimit)
                return Status(context, StatusCodes.Status413PayloadTooLarge);
            return next(context);
        }

        /// <summary>True when the request has the token, by a comparison in constant time.</summary>
        private bool Authorized(HttpRequest request) =>
            request.Headers.Authorization is { Count: 1 } header &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[0] ?? ""), expected);

        private static Task Status(HttpContext context, int status)
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }
    }
}
