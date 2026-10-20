using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol;
using OpenLancer.Data.Mods;
using OpenLancer.Mcp;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Tools;

/// <summary>
/// The commands <c>oltool mcp</c> of the agent server (section 6.2 of the MCP1 design): <c>serve</c>, <c>replay</c>,
/// <c>token</c> and <c>tools</c>. In this build, the server without a window only reads, so <c>serve</c> needs
/// <c>--read-only</c>, and <c>replay</c> opens the profile only to read.
/// </summary>
internal static class McpCommands
{
    public const string Usage = """
          mcp serve <profile.json> --mod <id> --read-only
                                            Serve the agent tools on stdin and stdout (the MCP stdio transport). The
                                            server opens the profile with the mod as the active mod. --read-only: the
                                            server only reads and writes no file. This build has only the query tools,
                                            so --read-only is necessary. The server writes its own messages to stderr.
          mcp replay <profile.json> --mod <id> <calls.jsonl>
                                            Run each call of an audit log or of a file of calls through the agent
                                            tools, and write one result line for each call. Exit 1 at the first result
                                            that differs from the recorded result, or at an error of a call without a
                                            recorded result. The profile opens only to read.
          mcp token                         Print the header with the token of the agent server of the editor, as JSON,
                                            for the headersHelper of Claude Code. Exit 2 and print nothing when no
                                            editor server runs, or when CLAUDE_CODE_MCP_SERVER_URL names no running
                                            server on 127.0.0.1. The helper gives no token for localhost.
          mcp tools [--json]                Print the agent tools: names, titles and descriptions. --json: print the
                                            tool list of the MCP protocol with the schemas and the hints.
        """;

    /// <param name="output">The standard output of oltool. Only <c>tools</c> and <c>replay</c> write to it.</param>
    /// <param name="error">The standard error of oltool.</param>
    /// <returns>The exit code, or null for arguments that are not an <c>mcp</c> command.</returns>
    public static int? Run(string[] args, TextWriter output, TextWriter error) => args switch
    {
        ["serve", var profile, .. var options] => Serve(profile, options, error),
        ["replay", var profile, .. var options] => Replay(profile, options, output, error),
        ["token"] => Token(output, error),
        ["tools"] => Tools(output, json: false),
        ["tools", "--json"] => Tools(output, json: true),
        _ => null,
    };

    private static int Serve(string profile, string[] options, TextWriter error)
    {
        string? mod = null;
        var readOnly = false;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--mod" when mod == null && i + 1 < options.Length:
                    mod = options[++i];
                    break;
                case "--read-only" when !readOnly:
                    readOnly = true;
                    break;
                default:
                    return Fail(error, $"Unknown argument {options[i]}. Usage:\n{Usage}");
            }
        }
        if (mod == null)
            return Fail(error, $"The server needs the active mod. Add --mod <id>. Usage:\n{Usage}");
        if (!readOnly)
            return Fail(error, "This build of the server without a window only reads the data. Add --read-only.");

        SessionThread thread;
        try
        {
            thread = SessionThread.Start(() => HeadlessHost.OpenReadOnly(profile, mod));
        }
        catch (Exception e) when (e is ModLoadException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Fail(error, e.Message);
        }
        using (thread)
        {
            error.WriteLine($"oltool mcp serve: profile {thread.Host.Session!.Profile.Name}, mod {mod}, read-only. " +
                            "The server reads the MCP messages on stdin.");
            McpServe.ServeStdioAsync(thread, CancellationToken.None).GetAwaiter().GetResult();
        }
        return 0;
    }

    private static int Replay(string profile, string[] options, TextWriter output, TextWriter error)
    {
        if (options is not ["--mod", var mod, var calls])
            return Fail(error, $"Usage:\n{Usage}");
        if (!File.Exists(calls))
            return Fail(error, $"The file {calls} does not exist.");
        try
        {
            using var host = HeadlessHost.OpenReadOnly(profile, mod);
            using var reader = new StreamReader(calls);
            if (AgentReplay.Run(host, reader, output) is { } difference)
            {
                error.WriteLine(difference);
                return 1;
            }
            return 0;
        }
        catch (Exception e) when (e is ModLoadException or ArgumentException or InvalidDataException or IOException
                                      or UnauthorizedAccessException)
        {
            return Fail(error, e.Message);
        }
    }

    /// <summary>The token helper (section 6.1, item 7 of the MCP1 design). Claude Code gives the address of the server
    /// in CLAUDE_CODE_MCP_SERVER_URL. The helper reads no other variable.</summary>
    private static int Token(TextWriter output, TextWriter error)
    {
        var (headers, problem) = AgentDiscovery.Headers(AgentDiscovery.DefaultFolder,
            Environment.GetEnvironmentVariable("CLAUDE_CODE_MCP_SERVER_URL"));
        if (headers == null)
            return Fail(error, problem!);
        output.WriteLine(headers);
        return 0;
    }

    private static int Tools(TextWriter output, bool json)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(McpBinding.ToolList(),
                new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { WriteIndented = true }));
            return 0;
        }
        foreach (var tool in AgentTools.All)
        {
            var host = tool.Hosts == ToolHosts.Editor ? " (editor only)" : "";
            output.WriteLine($"{tool.Name}: {tool.Title}{host}");
            output.WriteLine($"    {tool.Description}");
        }
        return 0;
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 2;
    }
}
