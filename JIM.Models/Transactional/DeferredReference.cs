using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Tracks references that couldn't be resolved during export because
/// the target object doesn't yet exist in the target connected system.
///
/// This supports event-based sync where objects may arrive out of order
/// (e.g., an employee arrives before their manager).
///
/// When the target object is eventually exported to the target system,
/// deferred references are resolved and the source object is updated.
/// </summary>
public class DeferredReference
{
    public Guid Id { get; set; }

    /// <summary>
    /// The Connected System Object that has the unresolved reference attribute.
    /// </summary>
    public ConnectedSystemObject SourceCso { get; set; } = null!;
    public Guid SourceCsoId { get; set; }

    /// <summary>
    /// The attribute name on the CSO that contains the reference (e.g., "manager").
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The Metaverse Object being referenced (the target of the reference).
    /// </summary>
    public MetaverseObject TargetMvo { get; set; } = null!;
    public Guid TargetMvoId { get; set; }

    /// <summary>
    /// The Connected System where the reference needs to be resolved.
    /// This is where both the source CSO and the target CSO should exist.
    /// </summary>
    public ConnectedSystem TargetSystem { get; set; } = null!;
    public int TargetSystemId { get; set; }

    /// <summary>
    /// When this deferred reference was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this deferred reference was successfully resolved.
    /// Null if not yet resolved.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// How many times have we attempted to resolve this reference?
    /// </summary>
    public int RetryCount { get; set; }
}
