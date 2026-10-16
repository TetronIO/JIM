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
    /// The attributes the Connector wrote with an interim value, on a successful Create, or an Update Active Directory
    /// refused to apply enabled for want of a password, because JIM said a password will follow
    /// (<see cref="Transactional.PendingExport.InitialPasswordFollows"/>, #2039, #2158). Active Directory refuses an
    /// enabled account with no password, so the LDAP Connector writes the account disabled, adding ACCOUNTDISABLE to
    /// <c>userAccountControl</c>, and names that attribute here; the account is enabled once the password is set. The Pending Export still carries the value the Synchronisation Rule asked for, which is the one the
    /// confirming import is judged against. Null from every Connector that rewrote nothing, which is every Connector but
    /// the LDAP Connector against Active Directory.
    /// </summary>
    public IReadOnlyList<string>? AttributesAwaitingInitialPassword { get; set; }

    /// <summary>
    /// Whether this export wrote the attribute through which the Connected System enables or disables the account
    /// (#2064): <c>userAccountControl</c> on Active Directory and Samba AD. Reported on any successful export that wrote
    /// it, whether a Create's add, an Update's modify, or a Delete performed as a disable.
    /// <para>
    /// JIM records it on the account's owed Initial Password, and a password delivered to an account whose enabled
    /// state an export wrote leaves that state to the Attribute Flow, rather than applying the provisioning
    /// Synchronisation Rule's Enable Account. Without it an account the flow creates disabled (a pre-hire, or a person
    /// archived at creation) would be enabled with a password JIM knows. The Connector only observes: it decides
    /// nothing about the account.
    /// </para>
    /// <para>
    /// On Active Directory the same attribute carries "Password never expires", so the delivery leaves that flag to the
    /// export too, rather than applying the Initial Password's expiry over it (#2159). A Connector whose targets keep
    /// the enabled state and the expiry in separate attributes would need to report each separately.
    /// </para>
    /// <para>
    /// A non-empty <see cref="AttributesAwaitingInitialPassword"/> implies this is true: an enable withheld until the
    /// password exists is a flow-set enabled state, which is what lets the delivery complete it. False from every
    /// Connector whose password channel has no notion of enabling an account.
    /// </para>
    /// </summary>
    public bool SetsAccountEnabledState { get; set; }

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
    /// Whether the Connector never started this export because the run was cancelled before it reached it (#2083).
    /// JIM records the results of the exports the Connector did write, and puts this one back exactly as it was before
    /// the batch was sent: no attempt is counted and no execution item is recorded, because nothing was sent and
    /// nothing failed. <see cref="Success"/> is false, so anything that does not know this flag fails safe rather than
    /// reading the export as written.
    /// </summary>
    public bool IsNotAttempted { get; set; }

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
    /// Creates a successful result with external ID feedback, naming the attributes written with an interim value
    /// until an Initial Password is delivered (see <see cref="AttributesAwaitingInitialPassword"/>).
    /// </summary>
    public static ConnectedSystemExportResult Succeeded(string? externalId, string? secondaryExternalId, IReadOnlyList<string>? attributesAwaitingInitialPassword) =>
        new()
        {
            Success = true,
            ExternalId = externalId,
            SecondaryExternalId = secondaryExternalId,
            AttributesAwaitingInitialPassword = attributesAwaitingInitialPassword
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
    /// Creates the result for an export the Connector did not start because the run was cancelled (#2083). See
    /// <see cref="IsNotAttempted"/>.
    /// </summary>
    public static ConnectedSystemExportResult NotAttempted() =>
        new()
        {
            Success = false,
            IsNotAttempted = true,
            ErrorMessage = "Not attempted: the export run was cancelled before this change was started."
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
