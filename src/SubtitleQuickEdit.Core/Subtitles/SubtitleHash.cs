using System;
using System.Security.Cryptography;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// The content hash the API uses to tell versions of a subtitle file apart.
/// </summary>
public static class SubtitleHash
{
    /// <summary>
    /// SHA-256 of some bytes, lower-case hex.
    /// </summary>
    /// <param name="content">The bytes.</param>
    /// <returns>The hash.</returns>
    public static string Compute(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));
}
