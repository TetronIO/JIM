using JIM.Models.Enums;
namespace JIM.Models.Activities.DTOs;

public class ActivityRunProfileExecutionItemHeader
{
    public Guid Id { get; set; }

    public string? ExternalIdValue { get; set; }

    public string? DisplayName { get; set; }

    public string? ConnectedSystemObjectType { get; set; }

    public ObjectChangeType ObjectChangeType { get; set; }

    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; }

    /// <summary>
    /// Denormalised outcome summary for stat chip rendering in list views.
    /// Comma-separated outcome types with counts, e.g., "Projected:1,AttributeFlow:12,PendingExportCreated:2".
    /// Null for legacy RPEIs or when outcome tracking is disabled.
    /// </summary>
    public string? OutcomeSummary { get; set; }
}