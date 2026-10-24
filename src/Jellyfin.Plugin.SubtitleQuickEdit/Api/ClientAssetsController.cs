using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api;

/// <summary>
/// Serves the client script and stylesheet that the File Transformation callback adds to jellyfin-web. The files are
/// public, read-only and hold no secrets, so no login is needed (the web page requests them without a token).
/// </summary>
[ApiController]
[Route("SubtitleQuickEdit/Client")]
[AllowAnonymous]
public class ClientAssetsController : ControllerBase
{
    // The script is the logic module followed by the page code, served as one file so the page needs one request.
    private static readonly Asset Script = Asset.Load("text/javascript; charset=utf-8", "Client.logic.js", "Client.quickedit.js");
    private static readonly Asset Style = Asset.Load("text/css; charset=utf-8", "Client.quickedit.css");

    /// <summary>
    /// Gets the client script.
    /// </summary>
    /// <returns>The script, or 304 when the caller's copy is current.</returns>
    [HttpGet("quickedit.js")]
    public IActionResult GetScript() => Serve(Script);

    /// <summary>
    /// Gets the client stylesheet.
    /// </summary>
    /// <returns>The stylesheet, or 304 when the caller's copy is current.</returns>
    [HttpGet("quickedit.css")]
    public IActionResult GetStyle() => Serve(Style);

    private IActionResult Serve(Asset asset)
    {
        Response.Headers[HeaderNames.ETag] = asset.ETag;
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";

        // "no-cache" means: keep a copy, but ask before using it. The ETag makes that question cheap.
        if (Request.Headers.IfNoneMatch.ToString().Contains(asset.ETag, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return File(asset.Bytes, asset.ContentType);
    }

    private sealed class Asset
    {
        private Asset(byte[] bytes, string contentType)
        {
            Bytes = bytes;
            ContentType = contentType;
            ETag = "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant() + "\"";
        }

        public byte[] Bytes { get; }

        public string ContentType { get; }

        public string ETag { get; }

        public static Asset Load(string contentType, params string[] resourceNames)
        {
            Assembly assembly = typeof(ClientAssetsController).Assembly;
            using var buffer = new MemoryStream();
            foreach (string resourceName in resourceNames)
            {
                using Stream stream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException("The embedded client file " + resourceName + " is missing from the plugin.");
                stream.CopyTo(buffer);

                // A line break between files, so a file without a final newline cannot run into the next one.
                buffer.WriteByte((byte)'\n');
            }

            return new Asset(buffer.ToArray(), contentType);
        }
    }
}
