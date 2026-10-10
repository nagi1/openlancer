using System.Collections.Generic;
using System.IO;
using LibreLancer.ContentEdit;
using LibreLancer.Data.Dll;
using OpenLancer.Data.Strings;

namespace OpenLancer.Runtime.Strings;

/// <summary>
/// Writes the generated text DLL with Librelancer's <see cref="DllWriter"/>.
/// </summary>
public sealed class DllStringResourceWriter : IStringResourceWriter
{
    public static readonly DllStringResourceWriter Instance = new();

    public byte[] Write(IReadOnlyDictionary<int, string> strings, IReadOnlyDictionary<int, string> infocards)
    {
        var dll = new ResourceDll();
        foreach (var (id, text) in strings)
            dll.Strings[id] = text;
        foreach (var (id, xml) in infocards)
            dll.Infocards[id] = xml;

        using var stream = new MemoryStream();
        DllWriter.Write(dll, stream);
        return stream.ToArray();
    }
}
