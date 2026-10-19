using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A temporary workspace for the publish tests (milestone M11c): the folders <c>profiles</c>, <c>mods</c> and
/// <c>exports</c>, and hand-written mods. The images are synthetic: a PNG or JPEG header and chosen bytes. Deleted on
/// dispose.
/// </summary>
internal sealed class PublishWorkspace : IDisposable
{
    public PublishWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Mods);
        Directory.CreateDirectory(Profiles);
    }

    public string Root { get; }

    public string Mods => Path.Combine(Root, ModWorkspace.ModsFolder);

    public string Profiles => Path.Combine(Root, ModWorkspace.ProfilesFolder);

    /// <summary>The publish history folder of a mod.</summary>
    public string Exports(string id) => PublishHistory.FolderOf(Root, id);

    /// <summary>Writes a mod of format version 2 into <c>mods</c>, and returns its folder.</summary>
    /// <param name="more">More fields of mod.json, for example <c>, "dependsOn": [ "a" ]</c>.</param>
    public string Mod(string id, string name = "Starrunner", string version = "1.0.0", string more = "")
    {
        var folder = Path.Combine(Mods, id);
        Write(Path.Combine(folder, ModManifest.FileName),
            $$"""{ "id": "{{id}}", "name": "{{name}}", "version": "{{version}}", "formatVersion": 2, "tier": 1{{more}} }""");
        Write(Path.Combine(folder, "patches", "DATA", "EQUIPMENT", "goods.ini"), $"[Good]\nnickname = {id}_good\nprice = 5\n");
        return folder;
    }

    /// <summary>Writes a profile into <c>profiles</c> with the mod folders, and returns its path.</summary>
    public string Profile(string name, params string[] mods)
    {
        var path = Path.Combine(Profiles, name + ".json");
        var baseFolder = Path.Combine(Root, "base");
        Directory.CreateDirectory(baseFolder);
        Write(path, JsonSerializer.Serialize(new
        {
            @base = baseFolder,
            mods = mods.Select(m => Path.GetRelativePath(Profiles, m).Replace('\\', '/')).ToArray(),
        }));
        return path;
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>The header of a PNG image of the size, with a final byte that makes the bytes unique.</summary>
    public static byte[] Png(int width, int height, byte mark = 0)
    {
        var bytes = new byte[34];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        bytes[24] = 8;
        bytes[25] = 2;
        bytes[33] = mark;
        return bytes;
    }

    /// <summary>The start of a JPEG image of the size: an APP0 segment, then the frame header.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        var bytes = new byte[2 + 18 + 19 + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        // APP0 with 16 bytes after the marker.
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 16);
        "JFIF"u8.CopyTo(bytes.AsSpan(6));
        // SOF0: length 17, precision, height, width, 3 components.
        bytes[20] = 0xFF;
        bytes[21] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), 17);
        bytes[24] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(25), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(27), (ushort)width);
        bytes[29] = 3;
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }

    /// <summary>Writes a screenshot into <c>publish\screenshots</c> of a mod, and returns its path in the list.</summary>
    public static string Screenshot(string modFolder, string name, byte mark = 0)
    {
        WriteBytes(Path.Combine(modFolder, "publish", "screenshots", name), Png(640, 480, mark));
        return $"{PublishSettings.ScreenshotsFolder}/{name}";
    }

    /// <summary>Makes a junction with the command of Windows, which needs no extra right.</summary>
    public static void Junction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)));
        Assert.SkipUnless(process.ExitCode == 0 && Directory.Exists(link), "mklink /J did not make the junction.");
    }

    /// <summary>The text of a file.</summary>
    public static string Text(string path) => File.ReadAllText(path, Encoding.UTF8);

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Root))
                return;
            Unlink(Root);
            Directory.Delete(Root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that a test still holds open stays until the next temp cleanup.
        }
    }

    /// <summary>Deletes each junction below a folder, without a delete in its target.</summary>
    private static void Unlink(string folder)
    {
        foreach (var child in Directory.GetDirectories(folder))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(child);
            else
                Unlink(child);
        }
    }
}
