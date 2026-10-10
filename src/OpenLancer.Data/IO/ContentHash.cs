using System;
using System.IO;
using System.Security.Cryptography;

namespace OpenLancer.Data.IO;

/// <summary>The SHA-256 of file content, in lowercase hex. The editor compares it to find changes outside the editor.</summary>
internal static class ContentHash
{
    public static string Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The hash of a file, or null when the file does not exist.</summary>
    public static string? OfFile(string path) => File.Exists(path) ? Of(File.ReadAllBytes(path)) : null;
}
