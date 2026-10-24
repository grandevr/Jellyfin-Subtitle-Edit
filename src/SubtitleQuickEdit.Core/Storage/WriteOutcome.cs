using System.Collections.Generic;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// The result of a successful write.
/// </summary>
/// <param name="Method">How the file was replaced.</param>
/// <param name="NewHash">SHA-256 of the file as written.</param>
/// <param name="Notes">Things that happened on the way, such as recovered leftovers. Fit for the log.</param>
public sealed record WriteOutcome(WriteMethod Method, string NewHash, IReadOnlyList<string> Notes);
