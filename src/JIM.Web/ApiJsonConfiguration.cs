// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Web;

/// <summary>
/// Central configuration for the JSON serialiser used by the REST API controllers.
/// Kept in one place (rather than inline in Program.cs) so the policy can be unit-tested
/// without booting the web host.
/// </summary>
public static class ApiJsonConfiguration
{
    /// <summary>
    /// Applies JIM's API JSON serialisation policy to the supplied options.
    /// </summary>
    /// <param name="options">The serialiser options to configure (typically the MVC input/output options).</param>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Serialise enums as their string names rather than numeric values, and reject numeric
        // values on the way in (allowIntegerValues: false). The default converter also accepts
        // integers on deserialisation, which lets a client send an out-of-range number for any
        // enum-typed request DTO property (for example {"mode": 99}); it binds without error,
        // passes model validation, and can be persisted as an undefined enum value that downstream
        // switch statements never account for. Rejecting integers wire-wide closes this hole for
        // every current and future request DTO at one site. Undefined string values already fail
        // deserialisation. Response serialisation is unaffected (it always emits string names).
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));

        // Reject payloads containing duplicate JSON property names. The JSON specification
        // does not define which value wins, so duplicates are ambiguous and are a known
        // request-smuggling vector when an upstream proxy and the application parser disagree
        // on which value to honour. Fail fast at the API boundary instead. (System.Text.Json,
        // .NET 10+; the default is permissive for backwards compatibility.)
        options.AllowDuplicateProperties = false;
    }
}
