using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;

namespace JIM.Workflow.Tests.Harness;

/// <summary>
/// Captures a snapshot of the database state at a point in time during workflow execution.
/// Use this to verify state transitions and diagnose issues between workflow steps.
/// </summary>
public class WorkflowStateSnapshot
{
    /// <summary>
    /// When this snapshot was taken.
    /// </summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Descriptive name for this snapshot (e.g., "After Import", "After Export Evaluation").
    /// </summary>
    public string StepName { get; }

    /// <summary>
    /// All Metaverse Objects at the time of snapshot.
    /// </summary>
    public IReadOnlyList<MetaverseObjectSnapshot> MetaverseObjects { get; }

    /// <summary>
    /// All Connected System Objects, grouped by system name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ConnectedSystemObjectSnapshot>> ConnectedSystemObjects { get; }

    /// <summary>
    /// All Pending Exports at the time of snapshot.
    /// </summary>
    public IReadOnlyList<PendingExportSnapshot> PendingExports { get; }

    /// <summary>
    /// Quick access to MVO count.
    /// </summary>
    public int MvoCount => MetaverseObjects.Count;

    /// <summary>
    /// Quick access to total CSO count across all systems.
    /// </summary>
    public int TotalCsoCount => ConnectedSystemObjects.Values.Sum(list => list.Count);

    /// <summary>
    /// Quick access to pending export count.
    /// </summary>
    public int PendingExportCount => PendingExports.Count;

    private WorkflowStateSnapshot(
        string stepName,
        IReadOnlyList<MetaverseObjectSnapshot> metaverseObjects,
        IReadOnlyDictionary<string, IReadOnlyList<ConnectedSystemObjectSnapshot>> connectedSystemObjects,
        IReadOnlyList<PendingExportSnapshot> pendingExports)
    {
        Timestamp = DateTime.UtcNow;
        StepName = stepName;
        MetaverseObjects = metaverseObjects;
        ConnectedSystemObjects = connectedSystemObjects;
        PendingExports = pendingExports;
    }

    /// <summary>
    /// Creates a snapshot of the current database state.
    /// </summary>
    public static async Task<WorkflowStateSnapshot> CaptureAsync(JimDbContext dbContext, string stepName)
    {
        // Capture MVOs
        var mvos = await dbContext.MetaverseObjects
            .AsNoTracking()
            .Include(m => m.Type)
            .Include(m => m.AttributeValues)
                .ThenInclude(av => av.Attribute)
            .ToListAsync();

        var mvoSnapshots = mvos.Select(m => new MetaverseObjectSnapshot(m)).ToList();

        // Capture CSOs grouped by system
        var csos = await dbContext.ConnectedSystemObjects
            .AsNoTracking()
            .Include(c => c.ConnectedSystem)
            .Include(c => c.Type)
            .Include(c => c.AttributeValues)
                .ThenInclude(av => av.Attribute)
            .ToListAsync();

        var csosBySystem = csos
            .GroupBy(c => c.ConnectedSystem?.Name ?? $"System_{c.ConnectedSystemId}")
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ConnectedSystemObjectSnapshot>)g.Select(c => new ConnectedSystemObjectSnapshot(c)).ToList());

        // Capture Pending Exports
        var pendingExports = await dbContext.PendingExports
            .AsNoTracking()
            .Include(pe => pe.ConnectedSystem)
            .Include(pe => pe.AttributeValueChanges)
                .ThenInclude(avc => avc.Attribute)
            .ToListAsync();

        var peSnapshots = pendingExports.Select(pe => new PendingExportSnapshot(pe)).ToList();

        return new WorkflowStateSnapshot(stepName, mvoSnapshots, csosBySystem, peSnapshots);
    }

    /// <summary>
    /// Gets CSOs for a specific connected system.
    /// </summary>
    public IReadOnlyList<ConnectedSystemObjectSnapshot> GetCsos(string systemName)
    {
        return ConnectedSystemObjects.TryGetValue(systemName, out var csos)
            ? csos
            : Array.Empty<ConnectedSystemObjectSnapshot>();
    }

    /// <summary>
    /// Gets CSOs with a specific status.
    /// </summary>
    public IReadOnlyList<ConnectedSystemObjectSnapshot> GetCsosWithStatus(ConnectedSystemObjectStatus status)
    {
        return ConnectedSystemObjects.Values
            .SelectMany(list => list)
            .Where(c => c.Status == status)
            .ToList();
    }

    /// <summary>
    /// Gets pending exports with null CSO FK (the issue #234 symptom).
    /// </summary>
    public IReadOnlyList<PendingExportSnapshot> GetPendingExportsWithNullCsoFk()
    {
        return PendingExports.Where(pe => pe.ConnectedSystemObjectId == null).ToList();
    }

    /// <summary>
    /// Gets pending exports with a specific status.
    /// </summary>
    public IReadOnlyList<PendingExportSnapshot> GetPendingExportsWithStatus(PendingExportStatus status)
    {
        return PendingExports.Where(pe => pe.Status == status).ToList();
    }

    /// <summary>
    /// Creates a diff between this snapshot and another.
    /// </summary>
    public SnapshotDiff DiffFrom(WorkflowStateSnapshot previous)
    {
        return new SnapshotDiff(previous, this);
    }

    /// <summary>
    /// Generates a diagnostic summary of the snapshot.
    /// </summary>
    public string ToSummary()
    {
        var lines = new List<string>
        {
            $"=== Snapshot: {StepName} at {Timestamp:HH:mm:ss.fff} ===",
            $"MVOs: {MvoCount}",
            $"Total CSOs: {TotalCsoCount}"
        };

        foreach (var (systemName, csos) in ConnectedSystemObjects)
        {
            var byStatus = csos.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());
            var statusStr = string.Join(", ", byStatus.Select(kv => $"{kv.Key}: {kv.Value}"));
            lines.Add($"  {systemName}: {csos.Count} ({statusStr})");
        }

        lines.Add($"Pending Exports: {PendingExportCount}");

        var nullCsoCount = GetPendingExportsWithNullCsoFk().Count;
        if (nullCsoCount > 0)
        {
            lines.Add($"  WARNING: {nullCsoCount} pending exports have NULL CSO FK!");
        }

        var byPeStatus = PendingExports.GroupBy(pe => pe.Status).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (status, count) in byPeStatus)
        {
            lines.Add($"  {status}: {count}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Immutable snapshot of a Metaverse Object.
/// </summary>
public class MetaverseObjectSnapshot
{
    public Guid Id { get; }
    public string TypeName { get; }
    public MetaverseObjectOrigin Origin { get; }
    public IReadOnlyDictionary<string, object?> Attributes { get; }

    public MetaverseObjectSnapshot(MetaverseObject mvo)
    {
        Id = mvo.Id;
        TypeName = mvo.Type?.Name ?? "Unknown";
        Origin = mvo.Origin;

        var attrs = new Dictionary<string, object?>();
        foreach (var av in mvo.AttributeValues)
        {
            var attrName = av.Attribute?.Name ?? $"Attr_{av.AttributeId}";
            attrs[attrName] = av.StringValue ?? av.IntValue as object ?? av.GuidValue as object ?? av.DateTimeValue as object;
        }
        Attributes = attrs;
    }
}

/// <summary>
/// Immutable snapshot of a Connected System Object.
/// </summary>
public class ConnectedSystemObjectSnapshot
{
    public Guid Id { get; }
    public string SystemName { get; }
    public int SystemId { get; }
    public string TypeName { get; }
    public ConnectedSystemObjectStatus Status { get; }
    public ConnectedSystemObjectJoinType JoinType { get; }
    public Guid? MetaverseObjectId { get; }
    public IReadOnlyDictionary<string, object?> Attributes { get; }

    public ConnectedSystemObjectSnapshot(ConnectedSystemObject cso)
    {
        Id = cso.Id;
        SystemName = cso.ConnectedSystem?.Name ?? $"System_{cso.ConnectedSystemId}";
        SystemId = cso.ConnectedSystemId;
        TypeName = cso.Type?.Name ?? "Unknown";
        Status = cso.Status;
        JoinType = cso.JoinType;
        MetaverseObjectId = cso.MetaverseObjectId;

        var attrs = new Dictionary<string, object?>();
        foreach (var av in cso.AttributeValues)
        {
            var attrName = av.Attribute?.Name ?? $"Attr_{av.AttributeId}";
            attrs[attrName] = av.StringValue ?? av.IntValue as object ?? av.GuidValue as object ?? av.DateTimeValue as object;
        }
        Attributes = attrs;
    }
}

/// <summary>
/// Immutable snapshot of a Pending Export.
/// </summary>
public class PendingExportSnapshot
{
    public Guid Id { get; }
    public string SystemName { get; }
    public int SystemId { get; }
    public Guid? ConnectedSystemObjectId { get; }
    public Guid? SourceMetaverseObjectId { get; }
    public PendingExportChangeType ChangeType { get; }
    public PendingExportStatus Status { get; }
    public int AttributeChangeCount { get; }
    public IReadOnlyList<string> AttributeNames { get; }
    public IReadOnlyList<PendingExportAttributeValueChangeSnapshot> AttributeValueChanges { get; }

    public PendingExportSnapshot(PendingExport pe)
    {
        Id = pe.Id;
        SystemName = pe.ConnectedSystem?.Name ?? $"System_{pe.ConnectedSystemId}";
        SystemId = pe.ConnectedSystemId;
        ConnectedSystemObjectId = pe.ConnectedSystemObjectId;
        SourceMetaverseObjectId = pe.SourceMetaverseObjectId;
        ChangeType = pe.ChangeType;
        Status = pe.Status;
        AttributeChangeCount = pe.AttributeValueChanges.Count;
        AttributeNames = pe.AttributeValueChanges
            .Select(avc => avc.Attribute?.Name ?? $"Attr_{avc.AttributeId}")
            .ToList();
        AttributeValueChanges = pe.AttributeValueChanges
            .Select(avc => new PendingExportAttributeValueChangeSnapshot(avc))
            .ToList();
    }
}

/// <summary>
/// Immutable snapshot of a Pending Export Attribute Value Change.
/// </summary>
public class PendingExportAttributeValueChangeSnapshot
{
    public Guid Id { get; }
    public int AttributeId { get; }
    public PendingExportAttributeChangeType ChangeType { get; }
    public string? StringValue { get; }
    public int? IntValue { get; }
    public Guid? GuidValue { get; }
    public DateTime? DateTimeValue { get; }
    public string? UnresolvedReferenceValue { get; }
    public PendingExportAttributeValueChangeSnapshot? Attribute { get; }

    /// <summary>
    /// Convenience property for accessing attribute details through the snapshot.
    /// Note: This creates a lightweight "attribute info" for test assertions.
    /// </summary>
    public AttributeInfo? AttributeInfo { get; }

    public PendingExportAttributeValueChangeSnapshot(PendingExportAttributeValueChange avc)
    {
        Id = avc.Id;
        AttributeId = avc.AttributeId;
        ChangeType = avc.ChangeType;
        StringValue = avc.StringValue;
        IntValue = avc.IntValue;
        GuidValue = avc.GuidValue;
        DateTimeValue = avc.DateTimeValue;
        UnresolvedReferenceValue = avc.UnresolvedReferenceValue;

        if (avc.Attribute != null)
        {
            AttributeInfo = new AttributeInfo(avc.Attribute.Name);
        }
    }
}

/// <summary>
/// Lightweight attribute info for test assertions.
/// </summary>
public class AttributeInfo
{
    public string Name { get; }

    public AttributeInfo(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Represents the differences between two snapshots.
/// </summary>
public class SnapshotDiff
{
    public IReadOnlyList<Guid> CreatedMvos { get; }
    public IReadOnlyList<Guid> DeletedMvos { get; }
    public IReadOnlyList<Guid> CreatedCsos { get; }
    public IReadOnlyList<Guid> DeletedCsos { get; }
    public IReadOnlyList<(Guid CsoId, ConnectedSystemObjectStatus From, ConnectedSystemObjectStatus To)> CsoStatusChanges { get; }
    public IReadOnlyList<Guid> CreatedPendingExports { get; }
    public IReadOnlyList<Guid> DeletedPendingExports { get; }

    public SnapshotDiff(WorkflowStateSnapshot previous, WorkflowStateSnapshot current)
    {
        var prevMvoIds = previous.MetaverseObjects.Select(m => m.Id).ToHashSet();
        var currMvoIds = current.MetaverseObjects.Select(m => m.Id).ToHashSet();
        CreatedMvos = currMvoIds.Except(prevMvoIds).ToList();
        DeletedMvos = prevMvoIds.Except(currMvoIds).ToList();

        var prevCsos = previous.ConnectedSystemObjects.Values.SelectMany(l => l).ToDictionary(c => c.Id);
        var currCsos = current.ConnectedSystemObjects.Values.SelectMany(l => l).ToDictionary(c => c.Id);
        CreatedCsos = currCsos.Keys.Except(prevCsos.Keys).ToList();
        DeletedCsos = prevCsos.Keys.Except(currCsos.Keys).ToList();

        var statusChanges = new List<(Guid, ConnectedSystemObjectStatus, ConnectedSystemObjectStatus)>();
        foreach (var (id, currCso) in currCsos)
        {
            if (prevCsos.TryGetValue(id, out var prevCso) && prevCso.Status != currCso.Status)
            {
                statusChanges.Add((id, prevCso.Status, currCso.Status));
            }
        }
        CsoStatusChanges = statusChanges;

        var prevPeIds = previous.PendingExports.Select(pe => pe.Id).ToHashSet();
        var currPeIds = current.PendingExports.Select(pe => pe.Id).ToHashSet();
        CreatedPendingExports = currPeIds.Except(prevPeIds).ToList();
        DeletedPendingExports = prevPeIds.Except(currPeIds).ToList();
    }

    public string ToSummary()
    {
        var lines = new List<string>();

        if (CreatedMvos.Count > 0) lines.Add($"Created MVOs: {CreatedMvos.Count}");
        if (DeletedMvos.Count > 0) lines.Add($"Deleted MVOs: {DeletedMvos.Count}");
        if (CreatedCsos.Count > 0) lines.Add($"Created CSOs: {CreatedCsos.Count}");
        if (DeletedCsos.Count > 0) lines.Add($"Deleted CSOs: {DeletedCsos.Count}");
        if (CsoStatusChanges.Count > 0)
        {
            lines.Add($"CSO Status Changes: {CsoStatusChanges.Count}");
            foreach (var (id, from, to) in CsoStatusChanges.Take(5))
            {
                lines.Add($"  {id}: {from} -> {to}");
            }
            if (CsoStatusChanges.Count > 5) lines.Add($"  ... and {CsoStatusChanges.Count - 5} more");
        }
        if (CreatedPendingExports.Count > 0) lines.Add($"Created PendingExports: {CreatedPendingExports.Count}");
        if (DeletedPendingExports.Count > 0) lines.Add($"Deleted PendingExports: {DeletedPendingExports.Count}");

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "No changes";
    }
}
