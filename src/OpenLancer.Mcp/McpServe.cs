using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Mcp;

/// <summary>
/// The agent server without a window (<c>oltool mcp serve</c>, section 6.2 of the MCP1 design): the tools of a session
/// thread on stdio. The SDK writes the protocol to the standard output, so the server writes its own messages to the
/// standard error only.
/// </summary>
public static class McpServe
{
    /// <summary>Serves the tools on the standard input and output of the process until the input ends.</summary>
    public static Task ServeStdioAsync(SessionThread thread, CancellationToken stop)
    {
        var options = Options(thread);
        return RunAsync(new StdioServerTransport(options), options, stop);
    }

    /// <summary>Serves the tools on two streams until the input ends. The tests run a server in their process with
    /// it.</summary>
    public static Task ServeAsync(SessionThread thread, Stream input, Stream output, CancellationToken stop)
    {
        var options = Options(thread);
        return RunAsync(new StreamServerTransport(input, output, McpBinding.ServerName), options, stop);
    }

    private static McpServerOptions Options(SessionThread thread)
    {
        var options = new McpServerOptions();
        McpBinding.Configure(options, thread.Queue);
        return options;
    }

    private static async Task RunAsync(ITransport transport, McpServerOptions options, CancellationToken stop)
    {
        await using (transport.ConfigureAwait(false))
        {
            var server = McpServer.Create(transport, options);
            await using (server.ConfigureAwait(false))
                await server.RunAsync(stop).ConfigureAwait(false);
        }
    }
}
