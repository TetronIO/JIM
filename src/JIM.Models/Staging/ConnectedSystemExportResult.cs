// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Represents the result of an export operation to a Connected System.
/// Connectors return this to provide feedback about the export, including
/// any system-generated identifiers.
/// </summary>
public class ConnectedSystemExportResult
{
    /// <summary>
    /// Whether the export operation succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the export failed. Should be human-readable.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Classifies the type of error when the export failed.
    /// Null when the export succeeded.
    /// </summary>
    public ConnectedSystemExportErrorType? ErrorType { get; set; }

    /// <summary>
    /// When <see cref="ErrorType"/> is <see cref="ConnectedSystemExportErrorType.UniqueValueAlreadyInUse"/>, the
    /// Connected System attribute the server said holds a value already in use: <c>distinguishedName</c> when an
    /// LDAP entry already exists at the DN, a column name for a database. Null when the server did not name one,
    /// which is common, and for every other result. Never inferred from the export's contents: attribution that
    /// guesses would have Collision Remediation revise a value that was never in use.
    /// </summary>
    public string? RejectedAttributeName { get; set; }

    /// <summary>
    /// The primary external ID assigned by the target system.
    /// For LDAP, this would be the objectGUID.
    /// For systems that don't generate IDs, this may be null.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// The secondary external ID from the target system.
    /// For LDAP, this would be the DN (which may differ from what was requested if the server normalised it).
    /// For systems without secondary IDs, this may be null.
    /// </summary>
    public string? SecondaryExternalId { get; set; }

    /// <summary>
    /// Creates a successful result with no external ID feedback.
    /// </summary>
    public static ConnectedSystemExportResult Succeeded() => new() { Success = true };

    /// <summary>
    /// Creates a successful result with external ID feedback.
    /// </summary>
    public static ConnectedSystemExportResult Succeeded(string? externalId, string? secondaryExternalId = null) =>
        new()
        {
            Success = true,
            ExternalId = externalId,
            SecondaryExternalId = secondaryExternalId
        };

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static ConnectedSystemExportResult Failed(string errorMessage) =>
        new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            ErrorType = ConnectedSystemExportErrorType.General
        };

    /// <summary>
    /// Creates a failed result for a rejection because a value is already in use in the Connected System, naming
    /// the attribute where the server named it.
    /// </summary>
    /// <param name="errorMessage">The server's own message, kept whole so an administrator sees what it said.</param>
    /// <param name="rejectedAttributeName">The attribute the server named, or null when it named none.</param>
    public static ConnectedSystemExportResult ValueAlreadyInUse(string errorMessage, string? rejectedAttributeName) =>
        new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            ErrorType = ConnectedSystemExportErrorType.UniqueValueAlreadyInUse,
            RejectedAttributeName = rejectedAttributeName
        };

    /// <summary>
    /// Creates a failed result with an error message and a specific error classification.
    /// </summary>
    public static ConnectedSystemExportResult Failed(string errorMessage, ConnectedSystemExportErrorType errorType) =>
        new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            ErrorType = errorType
        };
}
