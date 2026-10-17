// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Scim.Service;

/// <summary>
/// A membership-only PATCH (<see cref="ScimReferencePatch.TryPlan"/>): the references it names, which the server looks
/// up under the store's write lock, and how to turn what is held of them into changes.
/// </summary>
public sealed class ScimReferencePatchPlan
{
    private readonly ScimServiceResourceType _type;
    private readonly IReadOnlyList<ScimPatchOperation> _operations;
    private readonly IReadOnlySet<string> _replaced;

    internal ScimReferencePatchPlan(ScimServiceResourceType type, IReadOnlyList<ScimPatchOperation> operations,
        IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> mentionedIds, IReadOnlySet<string> replaced)
    {
        _type = type;
        _operations = operations;
        MentionedIds = mentionedIds;
        _replaced = replaced;
    }

    /// <summary>
    /// The resources the request names, per reference attribute (by store name). Values that cannot be resource ids are
    /// left out: nothing can hold them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> MentionedIds { get; }

    /// <summary>
    /// Applies the request to the named references the resource holds, and returns what changes.
    /// </summary>
    /// <param name="heldMentioned">Of <see cref="MentionedIds"/>, those the resource holds, per attribute, with their
    /// types. References the request does not name must not be passed: the changes are relative to these.</param>
    public ScimReferencePatchOutcome Resolve(IReadOnlyDictionary<string, IReadOnlyList<ScimHeldReference>> heldMentioned)
    {
        ArgumentNullException.ThrowIfNull(heldMentioned);

        var resource = ScimReferenceEntries.Compose(new JsonObject(), _type, heldMentioned);
        var applied = ScimPatchApplier.Apply(resource, _operations, _type.Schema);
        if (applied.Error != null)
            return new ScimReferencePatchOutcome { Error = applied.Error, Coercions = applied.Coercions };

        var split = ScimReferenceEntries.Split(applied.Resource!, _type);
        if (split.Error != null)
            return new ScimReferencePatchOutcome { Error = split.Error, Coercions = applied.Coercions };

        var changes = new List<ScimReferenceChange>();
        foreach (var attribute in MentionedIds.Keys.Concat(_replaced).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = heldMentioned.TryGetValue(attribute, out var held) ? held.Select(r => r.TargetId).ToHashSet() : [];
            var after = split.References.TryGetValue(attribute, out var result) ? result.ToHashSet() : [];

            if (_replaced.Contains(attribute))
            {
                changes.Add(new ScimReferenceChange(attribute, ReplaceAll: true, after, []));
                continue;
            }

            var add = after.Except(before).ToList();
            var remove = before.Except(after).ToList();
            if (add.Count > 0 || remove.Count > 0)
                changes.Add(new ScimReferenceChange(attribute, ReplaceAll: false, add, remove));
        }

        return new ScimReferencePatchOutcome { Changes = changes, Coercions = applied.Coercions };
    }
}
