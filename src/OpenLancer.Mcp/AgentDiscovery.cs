using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Mcp;

/// <summary>What the discovery file of the agent server of the editor holds (section 6.1, item 6 of the MCP1
/// design).</summary>
/// <param name="Port">The port of the server on 127.0.0.1.</param>
/// <param name="Token">The token of this start of the server.</param>
/// <param name="ProcessId">The process of the editor.</param>
/// <param name="ProcessStartUtc">The start time of the process, so a file of an editor that crashed gives no
/// token.</param>
/// <param name="Profile">The name of the open profile, or null.</param>
/// <param name="ActiveMod">The id of the active mod, or null.</param>
public sealed record AgentDiscoveryInfo(int Port, string Token, int ProcessId, DateTime ProcessStartUtc, string? Profile,
    string? ActiveMod);

/// <summary>
/// The discovery file of the agent server of the editor, and the token helper <c>oltool mcp token</c> (section 6.1,
/// items 6 and 7 of the MCP1 design). The editor writes the file when its server starts and deletes it when the
/// server stops. The file is in the folder of the user profile, so other users cannot read the token.
/// </summary>
public static class AgentDiscovery
{
    /// <summary><c>%LOCALAPPDATA%\OpenLancer\agent\editor.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.Name, "agent", "editor.json");

    /// <summary>Writes the file in one step: a reader sees the old file or the new file, never a part.</summary>
    /// <exception cref="IOException">The file does not write.</exception>
    /// <exception cref="UnauthorizedAccessException">The file does not write.</exception>
    public static void Write(string path, AgentDiscoveryInfo info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(info, EditJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Deletes the file when it holds the token of this start. The file of another editor stays.</summary>
    public static void Delete(string path, string token)
    {
        try
        {
            if (Read(path) is { } info && info.Token == token)
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The next start writes the file again. The helper checks the process of the file.
        }
    }

    /// <summary>The content of the file, or null when it does not exist or is not valid.</summary>
    public static AgentDiscoveryInfo? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentDiscoveryInfo>(File.ReadAllText(path), EditJson.Options) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The headers for the <c>headersHelper</c> of Claude Code: <c>{"Authorization":"Bearer &lt;token&gt;"}</c>. The
    /// helper gives the token only when the process of the file runs with the start time of the file, and when the
    /// address of the client, if the client gives it, is 127.0.0.1 or localhost with the port of the file. So the
    /// token never goes to another program or to an old port.
    /// </summary>
    /// <param name="serverUrl">The address that the client connects to (<c>CLAUDE_CODE_MCP_SERVER_URL</c>), or
    /// null.</param>
    /// <param name="processStartUtc">Gives the start time of a running process, or null when no process has the id.
    /// The tests give a fake.</param>
    /// <returns>The JSON of the headers, or why the helper gives no token. <c>oltool mcp token</c> writes the reason
    /// to stderr.</returns>
    public static (string? Headers, string? Problem) Headers(string path, string? serverUrl,
        Func<int, DateTime?>? processStartUtc = null)
    {
        if (Read(path) is not { } info)
            return (null, "No agent server of the editor runs. Start it in the Agent menu of the editor.");
        if ((processStartUtc ?? ProcessStart)(info.ProcessId) is not { } started ||
            (started - info.ProcessStartUtc).Duration() > TimeSpan.FromSeconds(1))
            return (null, "The editor of the discovery file does not run. Start the agent server again.");
        if (serverUrl != null &&
            (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
             !(uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) ||
             uri.Port != info.Port))
        {
            return (null, $"The address {serverUrl} is not the agent server of the editor on http://127.0.0.1:{info.Port}. " +
                          "Use that address in .mcp.json.");
        }
        return (new JsonObject { ["Authorization"] = "Bearer " + info.Token }.ToJsonString(), null);
    }

    /// <summary>The start time of a running process in UTC, or null.</summary>
    public static DateTime? ProcessStart(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
