// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json.Serialization;
using JIM.Scim.Serialisation;

namespace JIM.Scim.Messages;

/// <summary>
/// The SCIM 2.0 error response (RFC 7644 section 3.12). The client connector parses this from service
/// providers to classify failures and to report meaningful detail on an RPEI; JIM's own service
/// provider emits it.
/// </summary>
public class ScimError
{
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; set; } = [ScimUrns.Error];

    /// <summary>
    /// The HTTP status code expressed as a string, per RFC 7644. Kept as a string (rather than an int)
    /// so a non-conformant provider value survives into logs and error messages instead of being lost
    /// to a parse failure; use <see cref="StatusCode"/> for the numeric form.
    /// </summary>
    [JsonPropertyName("status")]
    [JsonConverter(typeof(ScimFlexibleStringConverter))]
    public string? Status { get; set; }

    /// <summary>
    /// A canonical SCIM error keyword; see <see cref="ScimErrorTypes"/>. Absent on most 5xx responses.
    /// </summary>
    [JsonPropertyName("scimType")]
    public string? ScimType { get; set; }

    /// <summary>
    /// A human-readable explanation. Treat as untrusted, provider-controlled text: sanitise before logging.
    /// </summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>
    /// <see cref="Status"/> parsed as an integer, or null when the provider sent a non-numeric value.
    /// </summary>
    [JsonIgnore]
    public int? StatusCode =>
        int.TryParse(Status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status) ? status : null;

    /// <summary>
    /// Builds a conformant error for the given HTTP status code.
    /// </summary>
    public static ScimError ForStatus(int statusCode, string? detail = null, string? scimType = null)
    {
        return new ScimError
        {
            Status = statusCode.ToString(CultureInfo.InvariantCulture),
            Detail = detail,
            ScimType = scimType
        };
    }
}
