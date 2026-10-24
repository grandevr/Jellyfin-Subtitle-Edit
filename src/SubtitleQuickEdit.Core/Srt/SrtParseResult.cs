namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// The result of <see cref="SrtDocument.Parse"/>.
/// </summary>
public sealed class SrtParseResult
{
    private SrtParseResult(SrtDocument? document, string? failureReason)
    {
        Document = document;
        FailureReason = failureReason;
    }

    /// <summary>
    /// Gets the parsed document, or null when the file is not editable.
    /// </summary>
    public SrtDocument? Document { get; }

    /// <summary>
    /// Gets why the file is not editable, or null when it is.
    /// </summary>
    public string? FailureReason { get; }

    internal static SrtParseResult Ok(SrtDocument document) => new(document, null);

    internal static SrtParseResult Fail(string reason) => new(null, reason);
}
