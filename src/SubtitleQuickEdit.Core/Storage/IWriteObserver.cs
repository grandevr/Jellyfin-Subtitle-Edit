namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// Receives progress callbacks from <see cref="SafeFileWriter"/>.
/// </summary>
public interface IWriteObserver
{
    /// <summary>
    /// Called at each stage.
    /// </summary>
    /// <param name="stage">The stage reached.</param>
    void OnStage(WriteStage stage);
}
