using JIM.Models.Staging;
namespace JIM.Models.Transactional;

public class PendingExportAttributeValueChange
{
    public Guid Id { get; set; }

    public ConnectedSystemObjectTypeAttribute Attribute { get; set; } = null!;
    public int AttributeId { get; set; }

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    public long? LongValue { get; set; }

    public byte[]? ByteValue { get; set; }

    public Guid? GuidValue { get; set; }

    public bool? BoolValue { get; set; }

    /// <summary>
    /// Contains the unique identifier that the connected system uses to refer to references in string form.
    /// </summary>
    public string? UnresolvedReferenceValue { get; set; }

    public PendingExportAttributeChangeType ChangeType { get; set; }

    #region Confirmation Tracking

    /// <summary>
    /// Current confirmation status of this attribute change.
    /// </summary>
    public PendingExportAttributeChangeStatus Status { get; set; } = PendingExportAttributeChangeStatus.Pending;

    /// <summary>
    /// How many times have we attempted to export this attribute change?
    /// Used to enforce retry limits and display in UI.
    /// </summary>
    public int ExportAttemptCount { get; set; }

    /// <summary>
    /// When was this attribute change last exported?
    /// </summary>
    public DateTime? LastExportedAt { get; set; }

    /// <summary>
    /// The value that was returned by the connected system during confirming import
    /// when it didn't match our expected value. Useful for debugging mismatches.
    /// </summary>
    public string? LastImportedValue { get; set; }

    #endregion
}