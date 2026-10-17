// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;

namespace JIM.Scim.Schema;

/// <summary>
/// What a POST or PUT body becomes in the Service Provider's store: the attributes a client may set, typed as the
/// schema says. <c>id</c>, <c>meta</c> and <c>schemas</c> are the server's to state, so a client's are never kept;
/// read-only and undeclared attributes are dropped; a write-only attribute (<c>password</c>) fails the request (D5);
/// and a resource missing a required attribute is refused.
/// <para>
/// A body is read as a <c>replace</c> with no path applied to an empty resource, so creation, replacement and PATCH share
/// one set of rules: the same typing, the same interoperability leniency, and the same single write-only guard.
/// </para>
/// </summary>
public static class ScimResourceIntake
{
    /// <summary>
    /// Reads a request body into the resource to store. The body is never changed.
    /// </summary>
    public static ScimIntakeResult Read(JsonObject body, ScimResourceSchema schema)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(schema);

        var applied = ScimPatchApplier.Apply(
            new JsonObject(),
            [new ScimPatchOperation { Op = ScimPatchOperations.Replace, Value = body.DeepClone() }],
            schema);

        if (applied.Error != null)
            return new ScimIntakeResult { Error = applied.Error, Coercions = applied.Coercions };

        var missing = CheckRequired(applied.Resource!, schema);
        return missing != null
            ? new ScimIntakeResult { Error = missing, Coercions = applied.Coercions }
            : new ScimIntakeResult { Resource = applied.Resource, Coercions = applied.Coercions };
    }

    /// <summary>
    /// The error for a resource missing a required attribute, or null where it has them all: the base schema's always,
    /// and an extension's where the resource carries that extension. A create or replace runs it through
    /// <see cref="Read"/>; a PATCH runs it on the patched resource, since an operation can remove one.
    /// </summary>
    public static ScimError? CheckRequired(JsonObject resource, ScimResourceSchema schema)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(schema);

        var missing = Missing(resource, schema, null).FirstOrDefault();
        if (missing == null)
        {
            missing = schema.ExtensionUrns
                .Select(urn => (Urn: urn, Container: ScimEntryFilter.Member(resource, urn) as JsonObject))
                .Where(extension => extension.Container != null)
                .SelectMany(extension => Missing(extension.Container!, schema, extension.Urn))
                .FirstOrDefault();
        }

        return missing == null
            ? null
            : ScimError.ForStatus(400, $"The attribute '{missing}' is required.", ScimErrorTypes.InvalidValue);
    }

    private static IEnumerable<string> Missing(JsonObject container, ScimResourceSchema schema, string? schemaUrn)
    {
        return schema.Attributes(schemaUrn)
            .Where(attribute => attribute.Required && !ScimResourceSchema.IsReadOnly(attribute))
            .Where(attribute => ScimEntryFilter.Member(container, attribute.Name!) is null or JsonArray { Count: 0 })
            .Select(attribute => attribute.Name!);
    }
}
