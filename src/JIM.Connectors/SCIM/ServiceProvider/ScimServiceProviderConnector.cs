// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Scim;
using JIM.Models.Staging;
using JIM.Scim.Schema;
using JIM.Scim.Service;
using Serilog;

namespace JIM.Connectors.SCIM.ServiceProvider;

/// <summary>
/// SCIM 2.0 Service Provider Connector (#124). JIM is the SCIM service provider: SCIM clients write to JIM's own endpoint,
/// which keeps what they write in the Connected System's store, and this Connector imports that store. Nothing is called
/// out to, so there are no connection settings; the store, its definitions and its change sequence are the whole of the
/// Connected System.
/// </summary>
/// <remarks>
/// <para>
/// A Full Import reads every live resource. A Delta Import reads every change, deletions included, after the watermark
/// the last completed import reached and at or below the store's committed sequence when this one began (its horizon);
/// a write committed mid-run is left for the next import, so the watermark never claims more than was read. Either way
/// the horizon is what the run returns as the next watermark.
/// </para>
/// <para>
/// Declares no phases (<see cref="IConnectorPhases"/>): each page is one read of a local table, and JIM's own per-page
/// counts already say everything a step would.
/// </para>
/// Implementation plan: engineering/plans/doing/SCIM_SERVER_DESIGN.md.
/// </remarks>
public class ScimServiceProviderConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorImportUsingCalls, IConnectorScimResourceStoreAware
{
    /// <summary>
    /// How long a tombstone is kept after an import has read it, so a deletion is still there to read for a while after a
    /// later import fails or is rolled back, before the purge removes it.
    /// </summary>
    internal static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(7);

    private const string AfterToken = "after";

    private IScimResourceStore? _store;

    // Held for the length of an import run, decided on its first page: every page must read the same range, or the pages
    // would not add up to one consistent view of the store.
    private ImportRun? _run;

    /// <summary>
    /// The clock the purge measures tombstone age by. Settable so a test can fix it.
    /// </summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    #region IConnector members
    public string Name => ConnectorConstants.ScimServiceProviderConnectorName;

    public string? Description => "Lets SCIM 2.0 clients provision into JIM. JIM acts as the SCIM service provider: clients write Users and Groups to JIM's own SCIM endpoint, and this Connector imports what they wrote.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapabilities members
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => true;

    /// <summary>
    /// SCIM clients write to JIM, so JIM has nothing to write back: a Connected System of this type is a source only.
    /// </summary>
    public bool SupportsExport => false;

    public bool SupportsPartitions => false;
    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => false;

    /// <summary>
    /// The store assigns every resource its <c>id</c> and never changes it, which is exactly what an external identifier
    /// has to be.
    /// </summary>
    public bool SupportsUserSelectedExternalId => false;

    public bool SupportsUserSelectedAttributeTypes => false;
    public bool SupportsUserSelectedAttributePlurality => false;
    public bool SupportsAutoConfirmExport => false;
    public bool SupportsParallelExport => false;
    public bool SupportsPaging => true;
    public bool SupportsFilePaths => false;
    public bool SupportsPasswordSet => false;
    public bool SupportsPasswordPolicyDiscovery => false;
    public bool SupportsUniquenessProbe => false;
    public bool SupportsUniquenessRejectionClassification => false;

    /// <summary>
    /// The store's definitions are SCIM 2.0 schemas by definition, so the advisory Standard Mappings apply.
    /// </summary>
    public AttributeStandard SchemaStandard => AttributeStandard.Scim;
    #endregion

    #region IConnectorSettings members
    /// <summary>
    /// None: the Connector reads the Connected System's own store, so there is nothing to connect to or authenticate with.
    /// </summary>
    public List<ConnectorSetting> GetSettings() => [];

    public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger) => [];
    #endregion

    #region IConnectorScimResourceStoreAware members
    public void SetScimResourceStore(IScimResourceStore? store)
    {
        _store = store;
    }
    #endregion

    #region IConnectorSchema members
    /// <summary>
    /// The store's resource types, each an Object Type anchored on <c>id</c>.
    /// </summary>
    public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        var definitions = await ScimServiceProviderDefinitions.LoadAsync(RequireStore(), CancellationToken.None);

        var schema = new ConnectorSchema();
        foreach (var type in definitions.Compiled.ResourceTypes)
        {
            schema.ObjectTypes.Add(new ConnectorSchemaObjectType(type.Name)
            {
                RecommendedExternalIdAttribute = ScimCommonAttributes.For(type.Schema.BaseSchemaUrn)
                    .Single(a => a.Name == ScimCommonAttributes.Id)
                    .ToConnectorSchemaAttribute(),
                Attributes = definitions.Attributes[type.Name].Select(a => a.ToConnectorSchemaAttribute()).ToList()
            });
        }

        logger.Debug("SCIM 2.0 Service Provider Connector: the store defines {ObjectTypeCount} resource type(s).", schema.ObjectTypes.Count);
        return schema;
    }
    #endregion

    #region IConnectorImportUsingCalls members
    /// <summary>
    /// Nothing to open: the store is read through the seam JIM handed over. Clears what a previous run held.
    /// </summary>
    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger)
    {
        _run = null;
    }

    /// <summary>
    /// Reads one page of the store. JIM calls this repeatedly until no pagination tokens come back.
    /// </summary>
    public async Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        var store = RequireStore();
        var firstPage = _run == null;
        _run ??= await BeginRunAsync(store, connectedSystem, runProfile, persistedConnectorData, logger, cancellationToken);

        var page = await store.ReadAsync(new ScimStoreImportQuery
        {
            After = ReadAfter(paginationTokens),
            Count = Math.Max(runProfile.PageSize, 1),
            ChangedAfter = _run.ChangedAfter,
            ChangedUpTo = _run.ChangedAfter == null ? null : _run.Horizon
        }, cancellationToken);

        var result = new ConnectedSystemImportResult();
        var overflows = new List<string>();
        result.ImportObjects.AddRange(page.Resources
            .Select(resource => ToImportObject(resource, page.References, overflows))
            .OfType<ConnectedSystemImportObject>());

        if (page.HasMore && page.Resources.Count > 0)
            result.PaginationTokens.Add(new ConnectedSystemPaginationToken(AfterToken, ScimReferenceEntries.Format(page.Resources[^1].Id)));
        else
            result.PersistedConnectorData = new ScimServiceProviderImportState { ChangeSequence = _run.Horizon }.Serialise();

        ApplyWarnings(result, firstPage ? _run.Warning : null, overflows);

        logger.Debug("SCIM 2.0 Service Provider import: staged {Count} object(s) from {Read} resource(s) read; {More}",
            result.ImportObjects.Count, page.Resources.Count, result.PaginationTokens.Count > 0 ? "more pages to read" : "import complete");
        return result;
    }

    /// <returns>Always null: the watermark travels on the last page's result.</returns>
    public string? CloseImportConnection()
    {
        _run = null;
        return null;
    }
    #endregion

    /// <summary>
    /// Decides what the run reads, once, on its first page: the horizon, the range, and the purge of tombstones the
    /// previous import read.
    /// </summary>
    private async Task<ImportRun> BeginRunAsync(IScimResourceStore store, ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile,
        string? persistedConnectorData, ILogger logger, CancellationToken cancellationToken)
    {
        var definitions = await ScimServiceProviderDefinitions.LoadAsync(store, cancellationToken);
        var state = await store.GetStateAsync(cancellationToken);
        var horizon = state?.LastChangeSequence ?? 0;
        var purgeFloor = state?.TombstonePurgeFloor ?? 0;
        var previous = ScimServiceProviderImportState.Read(persistedConnectorData, logger)?.ChangeSequence;

        long? changedAfter = null;
        string? warning = null;
        if (runProfile.RunType == ConnectedSystemRunType.DeltaImport)
        {
            if (previous == null)
            {
                warning = "A Delta Import was requested, but no previous import has recorded how far it read. Every resource was read instead, which has established the watermark, so the next Delta Import should run normally.";
                logger.Warning("SCIM 2.0 Service Provider import: {Warning}", warning);
            }
            else if (previous < purgeFloor)
            {
                // Deletions after the watermark may already be purged, and a read that cannot see them would leave their
                // Connected System Objects in place with nothing to say they had gone.
                throw new CannotPerformDeltaImportException(
                    $"This Delta Import cannot run: the last import read the store up to change {previous}, but deletions up to change {purgeFloor} have since been purged, so some may never be seen. Run a Full Import, which finds every deletion by what it no longer reads.");
            }
            else if (previous > horizon)
            {
                throw new CannotPerformDeltaImportException(
                    $"This Delta Import cannot run: the last import read the store up to change {previous}, but the store has only reached change {horizon}. The store no longer matches what was imported from it; run a Full Import.");
            }
            else
            {
                changedAfter = previous;
            }
        }

        // Only tombstones an import has already read are purged, so a deletion is never lost; the retention keeps them a
        // while longer for a run that has to be repeated. Bound by the purge's own floor, the next Delta Import above it
        // is unaffected.
        if (previous is { } importedUpTo && importedUpTo <= horizon)
        {
            var purged = await store.PurgeTombstonesAsync(importedUpTo, UtcNow() - TombstoneRetention, cancellationToken);
            if (purged > 0)
                logger.Information("SCIM 2.0 Service Provider import: purged {Count} tombstone(s) read by an earlier import.", purged);
        }

        return new ImportRun(definitions, SelectedAttributes(connectedSystem, definitions), horizon, changedAfter, warning);
    }

    /// <summary>
    /// The attributes to read each selected resource type through, keyed by name. A resource type with no selected Object
    /// Type is not imported.
    /// </summary>
    private static Dictionary<string, List<ScimFlattenedAttribute>> SelectedAttributes(ConnectedSystem connectedSystem, ScimServiceProviderDefinitions definitions)
    {
        var selected = new Dictionary<string, List<ScimFlattenedAttribute>>(StringComparer.OrdinalIgnoreCase);
        foreach (var objectType in connectedSystem.ObjectTypes?.Where(o => o.Selected && definitions.Attributes.ContainsKey(o.Name)) ?? [])
        {
            var attributes = definitions.Attributes[objectType.Name];
            if (objectType.Attributes.Count == 0)
            {
                selected[objectType.Name] = attributes;
                continue;
            }

            var names = objectType.Attributes.Where(a => a.Selected).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected[objectType.Name] = attributes.Where(a => names.Contains(a.Name)).ToList();
        }

        return selected;
    }

    /// <summary>
    /// One resource as a Connected System Object: a tombstone as a deletion carrying only its <c>id</c>, a live resource
    /// projected as JIM's SCIM endpoint returns it and read by the client Connector's convention, so the same resource
    /// becomes the same object whichever side of the exchange JIM is on. Null for a resource type that is not selected.
    /// </summary>
    private ConnectedSystemImportObject? ToImportObject(ScimStoreResource resource,
        IReadOnlyDictionary<Guid, IReadOnlyList<ScimStoreReferenceTarget>> references, List<string> overflows)
    {
        var definitions = _run!.Definitions.Compiled;
        var type = definitions.FindById(resource.ResourceTypeId)
                   ?? throw new InvalidOperationException($"SCIM resource {resource.Id} has a resource type ({resource.ResourceTypeId}) its store does not define.");
        if (!_run.Selected.TryGetValue(type.Name, out var attributes))
            return null;

        if (resource.DeletedAt != null)
        {
            return new ConnectedSystemImportObject
            {
                ObjectType = type.Name,
                ChangeType = ObjectChangeType.Deleted,
                Attributes =
                [
                    new ConnectedSystemImportObjectAttribute { Name = ScimCommonAttributes.Id, StringValues = [ScimReferenceEntries.Format(resource.Id)] }
                ]
            };
        }

        var payload = JsonNode.Parse(resource.Payload ?? throw new InvalidOperationException($"Live SCIM resource {resource.Id} has no payload.")) as JsonObject
                      ?? throw new InvalidOperationException($"SCIM resource {resource.Id}'s payload is not a JSON object.");
        var held = references.TryGetValue(resource.Id, out var targets)
            ? Held(targets, definitions)
            : new Dictionary<string, IReadOnlyList<ScimHeldReference>>();
        var metadata = new ScimResourceMetadata
        {
            Id = ScimReferenceEntries.Format(resource.Id),
            ResourceType = type.Name,
            Created = new DateTimeOffset(DateTime.SpecifyKind(resource.Created, DateTimeKind.Utc)),
            LastModified = new DateTimeOffset(DateTime.SpecifyKind(resource.LastModified, DateTimeKind.Utc)),
            Version = resource.Version
        };

        var projected = ScimResourceProjection.Project(ScimReferenceEntries.Compose(payload, type, held), metadata, type.Schema);
        var read = ScimResourceReader.Read(JsonSerializer.SerializeToElement(projected), attributes);
        overflows.AddRange(read.Warnings);
        return ScimConnectorImport.ToImportObject(read, type.Name);
    }

    private static Dictionary<string, IReadOnlyList<ScimHeldReference>> Held(IReadOnlyList<ScimStoreReferenceTarget> references,
        ScimServiceDefinitions definitions) =>
        references.GroupBy(r => r.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyList<ScimHeldReference>)g.Select(r => new ScimHeldReference(r.TargetId, TypeName(definitions, r))).ToList(),
                StringComparer.OrdinalIgnoreCase);

    private static string TypeName(ScimServiceDefinitions definitions, ScimStoreReferenceTarget reference) =>
        definitions.FindById(reference.TargetResourceTypeId)?.Name
        ?? throw new InvalidOperationException($"A SCIM reference names resource {reference.TargetId} of a resource type ({reference.TargetResourceTypeId}) its store does not define.");

    /// <summary>
    /// The run's fallback warning on its first page, and any values a single-valued attribute could not hold, reported in
    /// aggregate: JIM keeps one warning per page, and one per object would bury the run.
    /// </summary>
    private static void ApplyWarnings(ConnectedSystemImportResult result, string? runWarning, List<string> overflows)
    {
        string? overflowWarning = overflows.Count switch
        {
            0 => null,
            1 => overflows[0],
            _ => $"{overflows.Count} attribute values could not be imported because a resource held more entries than the corresponding flattened attribute holds. First occurrence: {overflows[0]}"
        };

        if (runWarning != null)
        {
            result.WarningMessage = overflowWarning == null ? runWarning : $"{runWarning} {overflowWarning}";
            result.WarningErrorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport;
        }
        else if (overflowWarning != null)
        {
            result.WarningMessage = overflowWarning;
            result.WarningErrorType = ActivityRunProfileExecutionItemErrorType.MultiValuedToSingleValued;
        }
    }

    private static Guid? ReadAfter(List<ConnectedSystemPaginationToken> paginationTokens)
    {
        var token = paginationTokens.SingleOrDefault(t => t.Name == AfterToken);
        if (token == null)
            return null;

        return Guid.TryParse(token.StringValue, out var after)
            ? after
            : throw new InvalidOperationException("The SCIM 2.0 Service Provider Connector was handed a pagination token it did not issue.");
    }

    private IScimResourceStore RequireStore() =>
        _store ?? throw new InvalidOperationException("The SCIM 2.0 Service Provider Connector was not given its Connected System's SCIM store.");

    /// <summary>
    /// What one import run reads: the definitions, the selected attributes, the horizon it will return as the next
    /// watermark, and for a Delta Import the watermark it reads changes after (null for a read of every live resource).
    /// </summary>
    private sealed record ImportRun(
        ScimServiceProviderDefinitions Definitions,
        Dictionary<string, List<ScimFlattenedAttribute>> Selected,
        long Horizon,
        long? ChangedAfter,
        string? Warning);
}
