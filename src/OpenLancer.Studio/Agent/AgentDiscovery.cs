using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;

namespace OpenLancer.Studio.Agent;

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
/// The discovery files of the agent servers of the editor, and the token helper <c>oltool mcp token</c> (section 6.1,
/// items 6 and 7 of the MCP1 design). An editor writes its file when its server starts and deletes it when the server
/// stops. The files are in the folder of the user profile, so other users cannot read the tokens.
/// </summary>
/// <remarks>Each editor process has its own file, <c>editor-&lt;process id&gt;.json</c>. Two editors can run servers on
/// two ports, and then the file of one editor does not replace the file of the other. The helper picks the file of the
/// port that the client connects to.</remarks>
public static class AgentDiscovery
{
    private const string FilePrefix = "editor-";

    /// <summary><c>%LOCALAPPDATA%\OpenLancer\agent</c>.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.Name, "agent");

    /// <summary>The discovery file of an editor process: <c>editor-&lt;process id&gt;.json</c> in the folder.</summary>
    public static string PathOf(string folder, int processId) => Path.Combine(folder, $"{FilePrefix}{processId}.json");

    /// <summary>Writes the file in one step with <see cref="AtomicFile"/>: a reader sees the old file or the new file,
    /// never a part. It also deletes the files of editors that do not run now, for example after a crash.</summary>
    /// <param name="processStartUtc">Gives the start time of a running process (see <see cref="Headers"/>).</param>
    /// <exception cref="IOException">The file does not write.</exception>
    /// <exception cref="UnauthorizedAccessException">The file does not write.</exception>
    public static void Write(string path, AgentDiscoveryInfo info, Func<int, DateTime?>? processStartUtc = null)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(info, EditJson.Options));
        var own = Path.GetFullPath(path);
        foreach (var (file, other) in All(folder))
        {
            if (other is { } stale && !Path.GetFullPath(file).Equals(own, StringComparison.OrdinalIgnoreCase) &&
                !Runs(stale, processStartUtc ?? ProcessStart))
            {
                Delete(file, stale.Token);
            }
        }
    }

    /// <summary>Deletes the file when it holds the token of this start. A file with another token stays.</summary>
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
    /// helper gives a token only from the file of an editor that runs with the start time of the file. When the client
    /// gives its address, the address must be 127.0.0.1, and the helper takes the file with that port. Without the
    /// address, the helper takes the file only when one editor runs a server. So the token never goes to another
    /// program or to an old port.
    /// </summary>
    /// <remarks>The helper gives no token for <c>localhost</c>. The server listens on 127.0.0.1 only, and a client can
    /// connect to <c>localhost</c> on the IPv6 address first. Another program, also of another user, can listen there on
    /// the same port and get the token.</remarks>
    /// <param name="folder">The folder of the discovery files, normally <see cref="DefaultFolder"/>.</param>
    /// <param name="serverUrl">The address that the client connects to (<c>CLAUDE_CODE_MCP_SERVER_URL</c>), or
    /// null.</param>
    /// <param name="processStartUtc">Gives the start time of a running process, or null when no process has the id.
    /// The tests give a fake.</param>
    /// <returns>The JSON of the headers, or why the helper gives no token. <c>oltool mcp token</c> writes the reason
    /// to stderr.</returns>
    public static (string? Headers, string? Problem) Headers(string folder, string? serverUrl,
        Func<int, DateTime?>? processStartUtc = null)
    {
        int? port = null;
        if (serverUrl != null)
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
                uri.Host != "127.0.0.1")
            {
                return (null, $"The address {serverUrl} is not an address of the agent server of the editor. Use the " +
                              "address http://127.0.0.1:<port>/mcp in .mcp.json. Do not use localhost.");
            }
            port = uri.Port;
        }
        var start = processStartUtc ?? ProcessStart;
        var running = All(folder).Select(f => f.Info).OfType<AgentDiscoveryInfo>().Where(i => Runs(i, start)).ToList();
        if (running.Count == 0)
            return (null, "No agent server of the editor runs. Start it in the Agent menu of the editor.");
        var matches = port is { } p ? running.Where(i => i.Port == p).ToList() : running;
        if (matches.Count == 0)
        {
            return (null, $"No agent server of the editor runs on port {port}. Use the port of the agent settings of the " +
                          "editor in .mcp.json.");
        }
        if (matches.Count > 1)
        {
            return (null, $"{matches.Count} editors run an agent server. The helper needs the address of the server " +
                          "in CLAUDE_CODE_MCP_SERVER_URL.");
        }
        return (new JsonObject { ["Authorization"] = "Bearer " + matches[0].Token }.ToJsonString(), null);
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

    /// <summary>True when the process of the file runs with the start time of the file.</summary>
    private static bool Runs(AgentDiscoveryInfo info, Func<int, DateTime?> start) =>
        start(info.ProcessId) is { } started && (started - info.ProcessStartUtc).Duration() <= TimeSpan.FromSeconds(1);

    /// <summary>The discovery files of the folder with their content. A file that is not valid has no content.</summary>
    private static IEnumerable<(string File, AgentDiscoveryInfo? Info)> All(string folder)
    {
        string[] files;
        try
        {
            files = Directory.Exists(folder) ? Directory.GetFiles(folder, FilePrefix + "*.json") : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            files = [];
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).Select(f => (f, Read(f)));
    }
}
