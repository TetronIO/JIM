// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using JIM.Models.Staging;

namespace JIM.Connectors.Graph;

/// <summary>
/// Reads a refused Graph write into a Pending Export's result. Graph answers several unlike things with the same 400, so
/// some are told apart by their message: a member already present, an object not there yet, a value already in use, and
/// a write to an attribute a synchronised object takes from Active Directory.
/// </summary>
internal static partial class GraphExportErrorClassifier
{
    /// <summary>Whether Graph refused a member or owner because the group already has it.</summary>
    public static bool IsAlreadyPresent(GraphBatchResponse response) =>
        response.Status == (int)HttpStatusCode.BadRequest
        && Message(response)?.Contains("object references already exist", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether Graph refused because something the write refers to does not exist, which for a just-created object is
    /// often replication not having reached it yet.
    /// </summary>
    public static bool IsMissingReference(GraphBatchResponse response) =>
        response.Status == (int)HttpStatusCode.NotFound
        || response.Status == (int)HttpStatusCode.BadRequest && Message(response) is { } message
           && (message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
               || message.Contains("don't exist", StringComparison.OrdinalIgnoreCase)
               || message.Contains("do not exist", StringComparison.OrdinalIgnoreCase));

    /// <summary>Classifies a refusal of <paramref name="action"/>, i.e. "create the user", in Graph's own words.</summary>
    public static ConnectedSystemExportResult Classify(GraphBatchResponse response, string action)
    {
        if (response.Status == 0)
            return ConnectedSystemExportResult.Failed($"Microsoft Graph did not answer the request to {action}: {response.Error}", ConnectedSystemExportErrorType.General);

        var code = Code(response);
        var message = Message(response);
        var described = $"Microsoft Graph refused to {action} (HTTP {response.Status}{(code == null ? "" : $", {code}")}){(message == null ? "." : $": {message}")}";

        if (response.Status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
            return ConnectedSystemExportResult.Failed($"{described} Grant the app registration the permission this change needs.", ConnectedSystemExportErrorType.AccessDenied);

        if (message?.Contains("on-premises mastered", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ConnectedSystemExportResult.Failed(
                $"{described} The object is synchronised from on-premises Active Directory, which owns this attribute. Write it there instead, " +
                "or scope the export Synchronisation Rule, on the Metaverse Attribute that holds sourceOfAuthority, so it writes only to cloud objects.",
                ConnectedSystemExportErrorType.RejectedByConnectedSystem);
        }

        if (message != null && ValueInUse().Match(message) is { Success: true } inUse)
            return ConnectedSystemExportResult.ValueAlreadyInUse(described, inUse.Groups["property"].Value);

        if (response.Status == (int)HttpStatusCode.BadRequest && IsMissingReference(response))
            return ConnectedSystemExportResult.Failed(described, ConnectedSystemExportErrorType.MissingDependency);

        return response.Status is >= 400 and < 500 and not (int)HttpStatusCode.TooManyRequests and not (int)HttpStatusCode.RequestTimeout
            ? ConnectedSystemExportResult.Failed(described, ConnectedSystemExportErrorType.RejectedByConnectedSystem)
            : ConnectedSystemExportResult.Failed(described, ConnectedSystemExportErrorType.General);
    }

    private static string? Code(GraphBatchResponse response) => ErrorProperty(response, "code");

    private static string? Message(GraphBatchResponse response) => ErrorProperty(response, "message");

    private static string? ErrorProperty(GraphBatchResponse response, string name) =>
        response.Body is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"same value for property (?<property>[A-Za-z0-9_]+) already exists", RegexOptions.IgnoreCase)]
    private static partial Regex ValueInUse();
}
