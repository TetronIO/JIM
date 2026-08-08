// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Models.Staging;
using JIM.Scim.Messages;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Turns a service provider's rejection into the error type JIM reacts to.
/// <para>
/// Shared by the per-object and bulk export paths deliberately. The same rejection arrives as an HTTP
/// response in one and as a status inside a bulk operation result in the other, and if the two
/// classified it differently the same provider behaviour would produce a retryable dependency error one
/// way and an unclassified failure the other, purely because of how the change happened to travel.
/// </para>
/// </summary>
internal static class ScimExportErrorClassifier
{
    /// <param name="statusCode">The HTTP status the provider gave the change, or null where it gave none.</param>
    /// <param name="scimType">The provider's canonical SCIM error keyword, where it supplied one.</param>
    public static ConnectedSystemExportErrorType Classify(int? statusCode, string? scimType)
    {
        // A 412 means the resource moved on between JIM reading it and writing it back. Retrying blindly
        // would just race again; the next import reconciles what actually changed.
        if (statusCode == (int)HttpStatusCode.PreconditionFailed)
            return ConnectedSystemExportErrorType.ConcurrencyConflict;

        // RFC 7644 makes the client responsible for creating dependencies first, so this says the
        // referenced object has not been exported yet rather than that the data is wrong.
        return statusCode == (int)HttpStatusCode.BadRequest
               && string.Equals(scimType, ScimErrorTypes.InvalidValue, StringComparison.OrdinalIgnoreCase)
            ? ConnectedSystemExportErrorType.MissingDependency
            : ConnectedSystemExportErrorType.General;
    }
}
