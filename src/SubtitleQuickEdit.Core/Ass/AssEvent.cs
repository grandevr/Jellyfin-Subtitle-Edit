namespace SubtitleQuickEdit.Core.Ass;

/// <summary>
/// Where a Dialogue line sits in the decoded text, and what an added line copies from it.
/// </summary>
/// <param name="LineStart">First character of the line.</param>
/// <param name="TermEnd">End (exclusive) of the line's terminator; equal to the line's end when it has none.</param>
/// <param name="Terminated">True when the line ends with a line terminator.</param>
/// <param name="Prefix">The line from its start up to the first field, e.g. <c>Dialogue: </c>.</param>
/// <param name="Fields">The fields before Text, as written; Start and End are replaced in a copy.</param>
internal sealed record AssEvent(int LineStart, int TermEnd, bool Terminated, string Prefix, string[] Fields);
