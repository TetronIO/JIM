// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Schema;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Walks a service provider's resources one page at a time, staging each as a Connected System Import
/// Object.
/// <para>
/// JIM drives this by calling the connector repeatedly until no pagination tokens come back, so a single
/// call reads exactly one page. The position travels in the token, which is also how the connector knows
/// which resource type it is partway through.
/// </para>
/// </summary>
internal sealed class ScimConnectorImport
{
    /// <summary>
    /// A backstop on the number of pages one resource type may take, so a provider that never advances
    /// fails the run instead of being read for ever. Set far above any real import: at the default page
    /// size this is ten million resources of a single type.
    /// </summary>
    internal const int MaximumPagesPerResourceType = 100_000;

    private readonly ScimHttpClient _client;
    private readonly ScimDiscoveryResult _discovery;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _runProfile;
    private readonly ILogger _logger;
    private readonly ScimWatermarkTracker _watermark;
    private readonly List<string> _excludedAttributes;

    /// <param name="watermark">
    /// Accumulates across every page of the run, so the delta watermark is recorded only once the run
    /// has read everything it was going to read.
    /// </param>
    public ScimConnectorImport(
        ScimHttpClient client,
        ScimDiscoveryResult discovery,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        ScimWatermarkTracker watermark,
        ILogger logger)
    {
        _client = client;
        _discovery = discovery;
        _connectedSystem = connectedSystem;
        _runProfile = runProfile;
        _watermark = watermark;
        _logger = logger;
        _excludedAttributes = ScimQueryBuilder.ParseExcludedAttributes(
            connectedSystem.SettingValues?.SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingExcludedAttributes)?.StringValue);
    }

    /// <summary>
    /// Reads one page of resources.
    /// </summary>
    /// <param name="position">Where the previous call got to.</param>
    /// <param name="filter">An optional SCIM filter, supplied by delta import.</param>
    /// <param name="cancellationToken">Checked before each request; a cancelled run must stop promptly.</param>
    public async Task<ConnectedSystemImportResult> ImportPageAsync(
        ScimImportPosition position,
        string? filter,
        CancellationToken cancellationToken)
    {
        var result = new ConnectedSystemImportResult();
        var targets = GetTargetResourceTypes();

        if (targets.Count == 0)
        {
            _logger.Warning("SCIM import: no object types are selected for import, or none of the selected ones match a resource type the service provider publishes.");
            return result;
        }

        // A position past the end means the previous page finished the last resource type. Returning no
        // tokens is how the connector tells JIM the import is complete.
        if (position.ResourceTypeIndex >= targets.Count)
            return result;

        cancellationToken.ThrowIfCancellationRequested();

        var target = targets[position.ResourceTypeIndex];
        var query = ScimQueryBuilder.BuildPageQuery(target.Endpoint, position, _runProfile.PageSize, filter, _excludedAttributes);

        _logger.Debug("SCIM import: reading {ObjectType} page from {Query}", LogSanitiser.Sanitise(target.Name), LogSanitiser.Sanitise(query));

        var response = await _client.GetWithMetadataAsync<ScimListResponse<JsonElement>>(query, cancellationToken);
        var page = response.Body ?? new ScimListResponse<JsonElement>();

        // The provider's own clock, taken from the first page that carries it: the next Delta Import
        // asks for changes since just before this run started reading.
        _watermark.ObserveProviderClock(response.ServerDate);

        var slotOverflows = 0;
        string? firstSlotOverflow = null;

        foreach (var resource in page.Resources)
        {
            if (ScimResourceReader.TryReadLastModified(resource, out var lastModified))
                _watermark.ObserveLastModified(lastModified);

            var read = ScimResourceReader.Read(resource, target.Attributes);
            result.ImportObjects.Add(ToImportObject(read, target.Name));

            if (read.Warnings.Count == 0)
                continue;

            slotOverflows += read.Warnings.Count;
            firstSlotOverflow ??= read.Warnings[0];
        }

        // Reported in aggregate rather than per object: one message per object would bury the run, but
        // dropping them entirely would present partial values as complete ones.
        if (slotOverflows > 0)
        {
            result.WarningMessage = slotOverflows == 1
                ? firstSlotOverflow
                : $"{slotOverflows} attribute values could not be imported because the service provider returned more entries than the corresponding flattened attribute holds. First occurrence: {firstSlotOverflow}";
            result.WarningErrorType = ActivityRunProfileExecutionItemErrorType.MultiValuedToSingleValued;
        }

        var next = Advance(position, page, targets.Count);
        if (next != null)
            result.PaginationTokens.Add(next.ToToken());
        else
            result.PersistedConnectorData = BuildPersistedConnectorData();

        _logger.Debug("SCIM import: staged {Count} {ObjectType} object(s); {More}",
            result.ImportObjects.Count, LogSanitiser.Sanitise(target.Name), next == null ? "import complete" : "more pages to read");

        return result;
    }

    /// <summary>
    /// Records the watermark for the next Delta Import, on the last page of the run only.
    /// <para>
    /// Waiting until the end is what makes an abandoned run safe: a run that fails or is cancelled
    /// partway through never reaches here, so the watermark stays where the last completed import left
    /// it and the resources this run did not get to are read again next time. Advancing it per page
    /// would lose them silently.
    /// </para>
    /// </summary>
    /// <returns>The state to persist, or null where the run learned nothing to base a watermark on.</returns>
    private string? BuildPersistedConnectorData()
    {
        var watermark = _watermark.Resolve();
        if (!watermark.HasValue)
            return null;

        _logger.Debug("SCIM import: recording a delta watermark of {Watermark:O} for the next Delta Import.", watermark.Value);
        return new ScimImportState { Watermark = watermark, CapturedAt = DateTimeOffset.UtcNow }.Serialise();
    }

    /// <summary>
    /// Works out where the next call should resume, or returns null when there is nothing left to read.
    /// </summary>
    private ScimImportPosition? Advance(ScimImportPosition position, ScimListResponse<JsonElement> page, int targetCount)
    {
        var returned = page.Resources.Count;

        // A provider that volunteers a cursor is offering the more reliable walk: index paging over a set
        // that changes mid-import can skip or repeat resources.
        if (!string.IsNullOrEmpty(page.NextCursor))
        {
            return new ScimImportPosition
            {
                ResourceTypeIndex = position.ResourceTypeIndex,
                Mode = ScimPaginationMode.Cursor,
                Cursor = page.NextCursor,
                StartIndex = position.StartIndex + returned
            };
        }

        // Under cursor paging, no cursor means the walk is over. Under index paging, the walk continues
        // unless the provider has demonstrably run out. An empty page says so outright. Short of that,
        // two independent signals have to agree: the page came back smaller than was asked for, AND the
        // provider's own totalResults has been reached. Either signal alone is unsafe, and both failures
        // are silent:
        //   - a provider capping the page size below the requested count returns a short page every
        //     time, so stopping on shortness alone would read one page and report success;
        //   - a provider reporting the page's size as totalResults rather than the collection's (a real
        //     and easy mistake to make) would truncate the walk on the total alone.
        var readSoFar = position.StartIndex - 1 + returned;
        var providerRanOut = returned == 0
                             || (page.TotalResults > 0 && readSoFar >= page.TotalResults && returned < _runProfile.PageSize);

        if (position.Mode != ScimPaginationMode.Cursor && !providerRanOut)
        {
            // A provider that ignores startIndex answers every request with the same page, and the walk
            // above would then never end. The ceiling is far beyond any real import; reaching it means
            // the provider is not paging, so the run fails rather than reading for ever.
            if (position.PagesRead + 1 >= MaximumPagesPerResourceType)
            {
                throw new InvalidOperationException(
                    $"The SCIM service provider returned {MaximumPagesPerResourceType:N0} pages without running out of resources, " +
                    "which means it is not honouring the startIndex parameter. The import has been stopped.");
            }

            return new ScimImportPosition
            {
                ResourceTypeIndex = position.ResourceTypeIndex,
                Mode = ScimPaginationMode.Index,
                StartIndex = position.StartIndex + returned,
                PagesRead = position.PagesRead + 1
            };
        }

        // This resource type is finished; move to the next, or finish the import.
        var nextTypeIndex = position.ResourceTypeIndex + 1;
        if (nextTypeIndex >= targetCount)
            return null;

        return new ScimImportPosition
        {
            ResourceTypeIndex = nextTypeIndex,
            // Cursors belong to the walk that issued them, so a new resource type starts a new walk.
            Mode = position.Mode == ScimPaginationMode.Cursor ? ScimPaginationMode.Cursor : ScimPaginationMode.Index,
            StartIndex = 1,
            Cursor = null
        };
    }

    private static ConnectedSystemImportObject ToImportObject(ScimResourceReadResult read, string objectType)
    {
        if (read.Error != null)
        {
            // The object is staged carrying its error rather than skipped, so the administrator sees it
            // on the Activity instead of it silently going missing from the run.
            return new ConnectedSystemImportObject
            {
                ObjectType = objectType,
                ChangeType = ObjectChangeType.NotSet,
                ErrorType = ConnectedSystemImportObjectError.AttributeValueError,
                ErrorMessage = read.Error
            };
        }

        return new ConnectedSystemImportObject
        {
            ObjectType = objectType,
            // A full import asserts the object exists; JIM decides whether that is a create or an update.
            ChangeType = ObjectChangeType.Added,
            Attributes = read.Attributes
        };
    }

    /// <summary>
    /// The resource types to walk: those the provider publishes that JIM has a selected Object Type for,
    /// in a stable order so a position taken on one call still means the same thing on the next.
    /// </summary>
    private List<ScimImportTarget> GetTargetResourceTypes()
    {
        var selected = _connectedSystem.ObjectTypes?
            .Where(o => o.Selected)
            .ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase) ?? [];

        return _discovery.ResourceTypes
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Endpoint))
            .Where(r => selected.ContainsKey(r.Name!))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new ScimImportTarget(
                r.Name!,
                r.Endpoint!,
                SelectedAttributes(selected[r.Name!], r.Name!)))
            .ToList();
    }

    /// <summary>
    /// The attributes to stage for a resource type: those the administrator has selected.
    /// <para>
    /// The provider is still asked for everything (naming an inclusive set risks it returning nothing
    /// else), but staging is another matter: an attribute deselected on purpose would otherwise be
    /// stored anyway, which is data JIM was told not to keep. Where the Connected System Object Type
    /// carries no attributes yet, nothing has been selected or deselected, so everything readable is
    /// staged rather than nothing.
    /// </para>
    /// </summary>
    private List<ScimFlattenedAttribute> SelectedAttributes(ConnectedSystemObjectType objectType, string resourceTypeName)
    {
        if (!_discovery.FlattenedAttributes.TryGetValue(resourceTypeName, out var attributes))
            return [];

        if (objectType.Attributes.Count == 0)
            return attributes;

        var selected = objectType.Attributes
            .Where(a => a.Selected)
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return attributes.Where(a => selected.Contains(a.Name)).ToList();
    }

    /// <summary>
    /// A resource type to walk, with the flattened attributes its resources are read through.
    /// </summary>
    private sealed record ScimImportTarget(string Name, string Endpoint, List<ScimFlattenedAttribute> Attributes);
}
