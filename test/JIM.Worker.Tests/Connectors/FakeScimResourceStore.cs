// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Scim;
using JIM.Scim.Service;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A Connected System's SCIM store held in memory, for driving the SCIM 2.0 Service Provider Connector. Reads follow
/// the repository's import semantics (a Full read is every live resource; a Delta read is every resource, tombstones
/// included, whose change sequence is in the range; both keyset on id), and every read and purge is recorded so a test
/// can assert on what the Connector asked for.
/// </summary>
internal sealed class FakeScimResourceStore : IScimResourceStore
{
    public FakeScimResourceStore()
    {
        var (schemas, resourceTypes) = ScimCoreDefinitions.Rows();
        for (var i = 0; i < resourceTypes.Count; i++)
            resourceTypes[i].Id = i + 1;

        Schemas = schemas;
        ResourceTypes = resourceTypes;
    }

    public List<ScimStoreSchema> Schemas { get; }

    public List<ScimStoreResourceType> ResourceTypes { get; }

    public ScimStoreState? State { get; set; }

    public List<ScimStoreResource> Resources { get; } = [];

    public Dictionary<Guid, List<ScimStoreReferenceTarget>> References { get; } = [];

    public List<ScimStoreImportQuery> Reads { get; } = [];

    public List<(long ImportedUpTo, DateTime DeletedBefore)> Purges { get; } = [];

    public int TypeId(string name) => ResourceTypes.Single(t => t.Name == name).Id;

    /// <summary>
    /// Adds a live resource at the next change sequence.
    /// </summary>
    public ScimStoreResource AddLive(string typeName, string payload, Guid? id = null)
    {
        var resource = new ScimStoreResource
        {
            Id = id ?? Guid.NewGuid(),
            ResourceTypeId = TypeId(typeName),
            Version = 1,
            ChangeSequence = NextSequence(),
            Created = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            LastModified = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            Payload = payload
        };
        Resources.Add(resource);
        return resource;
    }

    /// <summary>
    /// Turns a resource into a tombstone at the next change sequence.
    /// </summary>
    public void Tombstone(ScimStoreResource resource)
    {
        resource.ChangeSequence = NextSequence();
        resource.DeletedAt = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        resource.Payload = null;
        References.Remove(resource.Id);
    }

    public void Refer(ScimStoreResource source, string attribute, ScimStoreResource target)
    {
        if (!References.TryGetValue(source.Id, out var held))
            References[source.Id] = held = [];
        held.Add(new ScimStoreReferenceTarget(attribute, target.Id, target.ResourceTypeId));
    }

    private long NextSequence()
    {
        State ??= new ScimStoreState();
        return ++State.LastChangeSequence;
    }

    public Task<IReadOnlyList<ScimStoreResourceType>> GetResourceTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScimStoreResourceType>>(ResourceTypes);

    public Task<IReadOnlyList<ScimStoreSchema>> GetSchemasAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScimStoreSchema>>(Schemas);

    public Task<ScimStoreState?> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State == null ? null : new ScimStoreState
        {
            LastChangeSequence = State.LastChangeSequence,
            TombstonePurgeFloor = State.TombstonePurgeFloor
        });

    public Task<ScimStoreImportPage> ReadAsync(ScimStoreImportQuery query, CancellationToken cancellationToken = default)
    {
        Reads.Add(query);

        var selected = Resources
            .Where(r => query.ChangedAfter == null
                ? r.DeletedAt == null
                : r.ChangeSequence > query.ChangedAfter && r.ChangeSequence <= query.ChangedUpTo)
            .Where(r => query.After == null || r.Id.CompareTo(query.After.Value) > 0)
            .OrderBy(r => r.Id)
            .Take(query.Count + 1)
            .ToList();

        var page = selected.Take(query.Count).ToList();
        var references = page
            .Where(r => r.DeletedAt == null && References.ContainsKey(r.Id))
            .ToDictionary(r => r.Id, r => (IReadOnlyList<ScimStoreReferenceTarget>)References[r.Id]);

        return Task.FromResult(new ScimStoreImportPage { Resources = page, References = references, HasMore = selected.Count > query.Count });
    }

    public Task<int> PurgeTombstonesAsync(long importedUpTo, DateTime deletedBefore, CancellationToken cancellationToken = default)
    {
        Purges.Add((importedUpTo, deletedBefore));
        return Task.FromResult(0);
    }
}
