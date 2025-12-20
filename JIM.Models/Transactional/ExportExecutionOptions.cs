namespace JIM.Models.Transactional;

/// <summary>
/// Options for controlling export execution behaviour.
/// </summary>
public class ExportExecutionOptions
{
    /// <summary>
    /// Number of exports to process in each batch.
    /// Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of parallel database operations.
    /// Default is 4.
    /// </summary>
    public int MaxParallelism { get; set; } = 4;
}
