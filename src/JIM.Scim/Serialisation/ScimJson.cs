// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Scim.Serialisation;

/// <summary>
/// The single set of JSON options for all SCIM 2.0 payloads, used by both the client connector and
/// JIM's own service provider so the two sides cannot drift in how they read and write the protocol.
/// </summary>
public static class ScimJson
{
    /// <summary>
    /// Options for reading and writing SCIM payloads.
    /// <para>
    /// Case-insensitive property matching is a specification requirement, not a leniency:
    /// RFC 7643 section 2.1 states that attribute names are case insensitive. Null members are omitted
    /// on write because SCIM treats an absent attribute as unasserted, whereas an explicit null in a
    /// PUT would ask the provider to clear the value.
    /// </para>
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // SCIM attribute names are defined by the schema and carried verbatim in JsonPropertyName
            // attributes; no naming policy is applied, so unusual names such as "$ref" survive intact.
            PropertyNamingPolicy = null
        };
        // Freezing the instance makes the shared options safe to hand to concurrent serialiser calls;
        // the flag populates the default reflection-based resolver, which freezing otherwise requires.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
