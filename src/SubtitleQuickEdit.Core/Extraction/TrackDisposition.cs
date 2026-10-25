using System;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// The default, forced and hearing-impaired flags of a subtitle track, copied into the extracted file's name.
/// </summary>
[Flags]
public enum TrackDisposition
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The track is the default one (<c>.default</c>).</summary>
    Default = 1,

    /// <summary>The track is forced (<c>.forced</c>).</summary>
    Forced = 2,

    /// <summary>The track is for the deaf and hard of hearing (<c>.sdh</c>).</summary>
    HearingImpaired = 4,
}
