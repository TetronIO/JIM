// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JIM.Scim.Serialisation;

namespace JIM.Scim.Messages;

/// <summary>
/// What a service provider reports about one operation in a bulk request (RFC 7644 section 3.7.3).
/// </summary>
public class ScimBulkOperationResult
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>
    /// The client's identifier for the operation, echoed back. Required only where the operation was a
    /// POST, so a client cannot rely on it alone to know which change an outcome belongs to.
    /// </summary>
    [JsonPropertyName("bulkId")]
    public string? BulkId { get; set; }

    /// <summary>
    /// The resource the operation acted on. For a create this is the only place the provider-assigned
    /// id appears when the operation carries no response body.
    /// </summary>
    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// The HTTP status the equivalent standalone request would have returned, as a string per the
    /// specification. Kept as a string so a non-conformant value survives into the reported error
    /// instead of being lost to a parse failure; use <see cref="StatusCode"/> for the numeric form.
    /// </summary>
    [JsonPropertyName("status")]
    [JsonConverter(typeof(ScimFlexibleStringConverter))]
    public string? Status { get; set; }

    /// <summary>
    /// The body the equivalent standalone request would have returned. RFC 7644 requires it for an
    /// error; providers also use it to return the created resource on success, so it is held as raw
    /// JSON and interpreted per the status rather than assumed to be one or the other.
    /// </summary>
    [JsonPropertyName("response")]
    public JsonElement? Response { get; set; }

    /// <summary>
    /// <see cref="Status"/> parsed as an integer, or null when the provider sent a non-numeric value.
    /// </summary>
    [JsonIgnore]
    public int? StatusCode =>
        int.TryParse(Status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status) ? status : null;

    /// <summary>
    /// Whether the provider reported the operation as applied. An unparseable or absent status is not
    /// success: a change JIM cannot confirm was applied must never be recorded as exported.
    /// </summary>
    [JsonIgnore]
    public bool Succeeded => StatusCode is >= 200 and < 300;

    /// <summary>
    /// Reads <see cref="Response"/> as a SCIM error, returning null when the provider sent something
    /// else (a created resource, or nothing at all).
    /// </summary>
    public ScimError? ReadError()
    {
        if (Response is not { ValueKind: JsonValueKind.Object } body)
            return null;

        try
        {
            var error = body.Deserialize<ScimError>(ScimJson.Options);
            return error?.ScimType != null || error?.Detail != null ? error : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The provider-assigned identifier for the resource this operation acted on.
    /// <para>
    /// Read from <see cref="Location"/>, which RFC 7644 section 3.7.3 requires on a successful create,
    /// falling back to an <c>id</c> member in the response body for providers that return the resource
    /// instead. Without it a created object cannot be updated or deleted later, and the confirming
    /// import would create a second Connected System Object for the same resource.
    /// </para>
    /// </summary>
    public string? ReadResourceId()
    {
        if (Response is { ValueKind: JsonValueKind.Object } body
            && body.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(id.GetString()))
        {
            return id.GetString();
        }

        if (string.IsNullOrWhiteSpace(Location))
            return null;

        var path = Uri.TryCreate(Location, UriKind.Absolute, out var location) ? location.AbsolutePath : Location;
        var segment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

        return string.IsNullOrWhiteSpace(segment) ? null : Uri.UnescapeDataString(segment);
    }
}
