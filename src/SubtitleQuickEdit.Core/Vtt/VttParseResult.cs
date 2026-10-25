namespace SubtitleQuickEdit.Core.Vtt;

/// <summary>
/// The result of parsing a WebVTT file: a document, or the reason there is none.
/// </summary>
public sealed class VttParseResult
{
    private VttParseResult(VttDocument? document, string? failureReason)
    {
        Document = document;
        FailureReason = failureReason;
    }

    /// <summary>
    /// Gets the parsed document, or null when the file cannot be edited.
    /// </summary>
    public VttDocument? Document { get; }

    /// <summary>
    /// Gets why the file cannot be edited, or null when it can.
    /// </summary>
    public string? FailureReason { get; }

    internal static VttParseResult Ok(VttDocument document) => new(document, null);

    internal static VttParseResult Fail(string reason) => new(null, reason);
}
