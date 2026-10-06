// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.RegularExpressions;
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
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // The ways a provider's detail plainly names the attribute that must be unique. RFC 7644 leaves the wording
    // to the provider, and its own description of uniqueness ("One or more of the attribute values are already
    // in use or are reserved.") names none, so a detail matching none of these is left unattributed rather than
    // guessed at (Unique Value Generation, decision 9).
    private static readonly Regex[] AttributeNamings =
    [
        // "The attribute 'userName' must be unique", "Uniqueness violated for attribute emails.value". Unquoted,
        // only a name shaped like a SCIM attribute (camelCase or a sub-attribute path), so that RFC 7644's own
        // "the attribute values are already in use" is not read as an attribute called "values".
        new(@"\b[Aa]ttribute\s+(?:['""](?<attribute>[A-Za-z][\w.:]*)['""]|(?<attribute>[a-z][a-z0-9]*(?:[A-Z][A-Za-z0-9]*)+(?:\.[a-z][A-Za-z0-9]*)?|[a-z][A-Za-z0-9]*(?:\.[a-z][A-Za-z0-9]*)+)\b)",
            RegexOptions.CultureInvariant, MatchTimeout),
        // "'externalId' must be unique", "\"userName\" is not unique". Not "'x' is already in use": the quoted
        // token there is as often the value as the attribute.
        new(@"['""](?<attribute>[A-Za-z][\w.:]*)['""]\s+(?:must be unique|is not unique)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout),
        // "userName 'alice' is already in use": a schema-style attribute name (lower-case first letter, so a
        // resource type such as "User 'alice' already exists" is not mistaken for one) followed by the quoted value
        new(@"(?<![\w.])(?<attribute>[a-z][A-Za-z0-9]*(?:\.[a-z][A-Za-z0-9]*)?)\s+['""][^'""]*['""]\s+(?:is\s+)?already\s+(?:in use|exists|taken)",
            RegexOptions.CultureInvariant, MatchTimeout)
    ];

    /// <param name="statusCode">The HTTP status the provider gave the change, or null where it gave none.</param>
    /// <param name="scimType">The provider's canonical SCIM error keyword, where it supplied one.</param>
    public static ConnectedSystemExportErrorType Classify(int? statusCode, string? scimType)
    {
        // A 412 means the resource moved on between JIM reading it and writing it back. Retrying blindly
        // would just race again; the next import reconciles what actually changed.
        if (statusCode == (int)HttpStatusCode.PreconditionFailed)
            return ConnectedSystemExportErrorType.ConcurrencyConflict;

        // RFC 7644 section 3.12: 409 with "uniqueness" is a value already in use at the provider.
        if (IsUniquenessRejection(statusCode, scimType))
            return ConnectedSystemExportErrorType.UniqueValueAlreadyInUse;

        // RFC 7644 makes the client responsible for creating dependencies first, so this says the
        // referenced object has not been exported yet rather than that the data is wrong.
        return statusCode == (int)HttpStatusCode.BadRequest
               && string.Equals(scimType, ScimErrorTypes.InvalidValue, StringComparison.OrdinalIgnoreCase)
            ? ConnectedSystemExportErrorType.MissingDependency
            : ConnectedSystemExportErrorType.General;
    }

    /// <summary>
    /// The result for a change the provider rejected, classified, and naming the attribute in use where the
    /// provider's detail named exactly one.
    /// </summary>
    /// <param name="message">The failure as JIM reports it, the provider's detail included.</param>
    /// <param name="statusCode">The HTTP status the provider gave the change, or null where it gave none.</param>
    /// <param name="error">The provider's SCIM error body, where it sent one.</param>
    public static ConnectedSystemExportResult Failure(string message, int? statusCode, ScimError? error) =>
        TryClassifyUniqueness(statusCode, error?.ScimType, error?.Detail, out var rejectedAttributeName)
            ? ConnectedSystemExportResult.ValueAlreadyInUse(message, rejectedAttributeName)
            : ConnectedSystemExportResult.Failed(message, Classify(statusCode, error?.ScimType));

    /// <summary>
    /// Whether the rejection means a value is already in use (409 with <c>scimType</c> <c>uniqueness</c>), and
    /// which attribute the provider's detail named, if it plainly named exactly one.
    /// </summary>
    internal static bool TryClassifyUniqueness(int? statusCode, string? scimType, string? detail, out string? rejectedAttributeName)
    {
        rejectedAttributeName = null;
        if (!IsUniquenessRejection(statusCode, scimType))
            return false;

        if (string.IsNullOrWhiteSpace(detail))
            return true;

        var named = AttributeNamings
            .SelectMany(naming => naming.Matches(detail))
            .Select(match => match.Groups["attribute"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (named.Count == 1)
            rejectedAttributeName = named[0];

        return true;
    }

    private static bool IsUniquenessRejection(int? statusCode, string? scimType) =>
        statusCode == (int)HttpStatusCode.Conflict
        && string.Equals(scimType, ScimErrorTypes.Uniqueness, StringComparison.OrdinalIgnoreCase);
}
