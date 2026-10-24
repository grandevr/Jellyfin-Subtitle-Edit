using System.Text;

namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// One-time registration of the .NET code-pages provider, needed for legacy encodings such as Windows-1250.
/// </summary>
internal static class EncodingSupport
{
    private static readonly object Gate = new();
    private static bool registered;

    internal static void EnsureCodePages()
    {
        lock (Gate)
        {
            if (!registered)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                registered = true;
            }
        }
    }
}
