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
    /// Maximum number of batches to process concurrently.
    /// Each parallel batch gets its own DbContext and connector instance.
    /// Default is 1 (sequential processing). Set higher to enable parallel batch export.
    /// </summary>
    public int MaxParallelism { get; set; } = 1;
}
