namespace SubtitleQuickEdit.Core.Ass;

/// <summary>
/// The result of parsing an ASS/SSA file: a document, or the reason there is none.
/// </summary>
public sealed class AssParseResult
{
    private AssParseResult(AssDocument? document, string? failureReason)
    {
        Document = document;
        FailureReason = failureReason;
    }

    /// <summary>
    /// Gets the parsed document, or null when the file cannot be edited.
    /// </summary>
    public AssDocument? Document { get; }

    /// <summary>
    /// Gets why the file cannot be edited, or null when it can.
    /// </summary>
    public string? FailureReason { get; }

    internal static AssParseResult Ok(AssDocument document) => new(document, null);

    internal static AssParseResult Fail(string reason) => new(null, reason);
}
