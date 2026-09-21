// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP.Security;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// What JIM established about its ability to see deletions in one partition of a directory.
/// </summary>
internal enum DeletedObjectsAccessOutcome
{
    /// <summary>The account JIM binds as may list the partition's Deleted Objects container.</summary>
    Granted,

    /// <summary>It may not, and JIM read the container's access control list in full to be sure of that.</summary>
    Denied,

    /// <summary>JIM could not see enough to say. Never to be presented as a denial.</summary>
    CouldNotDetermine
}

/// <summary>
/// What the access check found for one partition.
/// </summary>
internal sealed class DeletedObjectsAccessFinding
{
    /// <summary>The partition whose deletions are in question.</summary>
    internal required string NamingContext { get; init; }

    /// <summary>The Deleted Objects container that was checked.</summary>
    internal required string ContainerDn { get; init; }

    internal required DeletedObjectsAccessOutcome Outcome { get; init; }

    /// <summary>A plain statement of what was found, and where the outcome is not a grant, why.</summary>
    internal required string Detail { get; init; }
}

/// <summary>
/// Establishes whether the account JIM binds as can list a partition's Deleted Objects container, which is where
/// a Delta Import looks for tombstones. Reads the container's access control list and evaluates it, without
/// writing anything.
/// <para>
/// The container is the one place an ordinary delegation never reaches: its security descriptor is protected
/// from inheritance and owned by SYSTEM, so rights granted at an organisational unit or at the partition head do
/// not apply to it. An account without rights over it is not refused when it searches there; the search succeeds
/// with no rows, so the import finds no deletions and reports nothing was deleted. This check exists so that
/// silence is caught before it costs a customer a stale directory.
/// </para>
/// <para>
/// Reading the container's entry is not itself a faithful probe: the entry is visible to any caller with List
/// Contents on the naming context head ([MS-ADTS] 3.1.1.3.1.3.1), which says nothing about whether the caller
/// may list the container's own children. So JIM asks for the container's security descriptor and evaluates
/// RIGHT_DS_LIST_CONTENTS against it, the same way <see cref="LdapConnectorResetRights"/> evaluates the
/// reset-password right on a user.
/// </para>
/// <para>
/// <b>Every silence is an unknown, never a denial.</b> A missing entry, a missing attribute, or a descriptor JIM
/// cannot read all report <see cref="DeletedObjectsAccessOutcome.CouldNotDetermine"/>; a denial is only ever
/// claimed from an access control list that was read in full.
/// </para>
/// </summary>
internal class LdapConnectorDeletedObjectsAccess
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapConnectorDeletedObjectsAccess(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// The Deleted Objects container of a partition, which a directory places directly under the partition head
    /// ([MS-ADTS] 6.1.1.4.2). The same construction the Delta Import's tombstone search uses.
    /// </summary>
    internal static string ContainerDnFor(string namingContext) => "CN=Deleted Objects," + namingContext;

    /// <summary>
    /// The schemaIDGUID of the container class, which the Deleted Objects container is always an instance of
    /// ([MS-ADTS] 6.1.1.4.2). Handed to the evaluator so that an object entry whose ObjectType is this class,
    /// which addresses the container as a whole ([MS-ADTS] 5.1.3.3.3), is read as the grant or deny it is.
    /// </summary>
    internal static readonly Guid ContainerClass = new("bf967a8b-0de6-11d0-a285-00aa003049e2");

    /// <summary>
    /// Reads the partition's Deleted Objects container and evaluates whether the bound account may list it.
    /// </summary>
    internal async Task<DeletedObjectsAccessFinding> CheckAsync(string namingContext, CancellationToken cancellationToken)
    {
        var containerDn = ContainerDnFor(namingContext);

        var callerSids = await LdapCallerSecurityContext.ReadAsync(_executor, _logger, nameof(LdapConnectorDeletedObjectsAccess));
        if (callerSids == null)
        {
            // Without the full set of groups the account belongs to, no denial can be justified: the right may
            // well be held through a group JIM never saw.
            return Undetermined(namingContext, containerDn, "JIM could not read which groups the account it connects as belongs to");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var request = new SearchRequest(containerDn, "(objectClass=*)", SearchScope.Base, LdapConnectorResetRights.AttributeSecurityDescriptor);

        // The container is itself hidden from an ordinary search, so the request carries the same control the
        // tombstone search does. Critical, so that a directory that does not honour it refuses rather than
        // quietly answering for something else.
        request.Controls.Add(new DirectoryControl(LdapConnectorConstants.LDAP_SERVER_SHOW_DELETED_OID, null, true, true));

        // Without this, the directory reads the request as also asking for the audit portion of the descriptor,
        // which needs a privilege a least-privileged service account has no reason to hold. It then omits the
        // whole attribute rather than refusing, so the container would look like it had no access control list.
        request.Controls.Add(new SecurityDescriptorFlagControl(SecurityMasks.Owner | SecurityMasks.Group | SecurityMasks.Dacl));

        SearchResponse response;
        try
        {
            response = (SearchResponse)await _executor.SendRequestAsync(request);
        }
        catch (DirectoryOperationException ex)
        {
            return Undetermined(namingContext, containerDn, $"the directory answered: {LogSanitiser.Sanitise(ex.Message)}");
        }
        catch (LdapException ex)
        {
            return Undetermined(namingContext, containerDn, $"the directory answered: {LogSanitiser.Sanitise(ex.Message)}");
        }

        if (response.Entries.Count == 0)
            return Undetermined(namingContext, containerDn, "the directory did not return the container's entry");

        var attribute = response.Entries[0].Attributes[LdapConnectorResetRights.AttributeSecurityDescriptor];
        if (attribute == null || attribute.Count == 0)
            return Undetermined(namingContext, containerDn, "the directory did not return the container's permissions, which needs Read Permissions on it");

        if (attribute.GetValues(typeof(byte[])).OfType<byte[]>().FirstOrDefault() is not { } descriptorBytes)
            return Undetermined(namingContext, containerDn, "the directory returned the container's permissions in a form JIM could not read");

        var securityDescriptor = SecurityDescriptorParser.TryParse(descriptorBytes);
        if (securityDescriptor == null)
            return Undetermined(namingContext, containerDn, "JIM could not make sense of the container's permissions as the directory returned them");

        var listContents = AccessMaskEvaluator.Evaluate(securityDescriptor, callerSids, AccessMaskEvaluator.ListContents, ContainerClass);
        var readProperty = AccessMaskEvaluator.Evaluate(securityDescriptor, callerSids, AccessMaskEvaluator.ReadProperty, ContainerClass);

        if (listContents != AccessCheckOutcome.Granted)
        {
            _logger.Warning("LdapConnectorDeletedObjectsAccess: The account JIM connects as may not list '{Container}', so a Delta Import there would find no deletions.",
                LogSanitiser.Sanitise(containerDn));

            return new DeletedObjectsAccessFinding
            {
                NamingContext = namingContext,
                ContainerDn = containerDn,
                Outcome = DeletedObjectsAccessOutcome.Denied,
                Detail = "List Contents is not granted"
            };
        }

        // Listing finds the tombstones; reading their properties is what fills in the attributes the import
        // keys on. A grant of the first without the second is still a grant, but one worth naming.
        var detail = readProperty == AccessCheckOutcome.Granted
            ? "List Contents and Read Property are both granted"
            : "List Contents is granted; Read Property is not, so tombstone attributes may not be readable";

        _logger.Debug("LdapConnectorDeletedObjectsAccess: Evaluated access to '{Container}': {Detail}",
            LogSanitiser.Sanitise(containerDn), detail);

        return new DeletedObjectsAccessFinding
        {
            NamingContext = namingContext,
            ContainerDn = containerDn,
            Outcome = DeletedObjectsAccessOutcome.Granted,
            Detail = detail
        };
    }

    /// <summary>
    /// The text Schema Discovery shows for a finding that is not a grant, so an administrator learns of the
    /// problem when setting the Connected System up rather than from an import that found nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The finding is a grant, for which there is nothing to say.</exception>
    internal static string DescribeForSchemaDiscovery(DeletedObjectsAccessFinding finding) => finding.Outcome switch
    {
        DeletedObjectsAccessOutcome.Denied =>
            $"The account JIM connects as is not allowed to list the Deleted Objects container ({finding.ContainerDn}). " +
            "Delta Imports from this domain will refuse to run until it is granted List Contents, Read Property and Read Permissions on that container; " +
            "the LDAP Connector documentation, under Service Account Permissions, gives the commands.",
        DeletedObjectsAccessOutcome.CouldNotDetermine => DescribeUndetermined(finding),
        _ => throw new InvalidOperationException("A grant has nothing to describe.")
    };

    /// <summary>
    /// The text a Delta Import reports for a finding that is not a grant: the failure that stops it running when
    /// the container is denied, and the warning it carries when JIM could not be sure.
    /// </summary>
    /// <exception cref="InvalidOperationException">The finding is a grant, for which there is nothing to say.</exception>
    internal static string DescribeForDeltaImport(DeletedObjectsAccessFinding finding) => finding.Outcome switch
    {
        DeletedObjectsAccessOutcome.Denied =>
            $"Deletions cannot be detected: the account JIM connects as is not allowed to list the Deleted Objects container ({finding.ContainerDn}), " +
            "so objects deleted in the directory would stay in JIM. Grant the account List Contents, Read Property and Read Permissions on that container " +
            "(see the LDAP Connector documentation, Service Account Permissions), or run a Full Import, which detects deletions by absence.",
        DeletedObjectsAccessOutcome.CouldNotDetermine => DescribeUndetermined(finding),
        _ => throw new InvalidOperationException("A grant has nothing to describe.")
    };

    /// <summary>
    /// The note a Delta Import records when the directory refuses the tombstone search itself, which is definite
    /// where the up-front check could only say it was unsure: no deletions were detected there, and this is why.
    /// </summary>
    internal static string DescribeRefusedSearch(string containerDn, string? directoryMessage) =>
        $"Deletions were not detected in {containerDn}: the directory refused the search ({directoryMessage}). " +
        "Objects deleted since the last import may still be present in JIM.";

    /// <summary>
    /// The note a Delta Import records when the directory stopped the tombstone search at its own size limit, so
    /// that nothing from it could safely be imported (#1724). Definite, like a refusal: none of the deletions since
    /// the last import were detected, and a Full Import is the way to catch them up.
    /// </summary>
    internal static string DescribeLimitExceeded(string containerDn) =>
        $"Deletions were not detected in {containerDn}: the directory returned more deleted objects than it answers in one search, and none of them were imported. " +
        "Objects deleted since the last import are still present in JIM; a Full Import detects them by absence.";

    /// <summary>
    /// The note a Delta Import records when the partition has no Deleted Objects container to search.
    /// </summary>
    internal static string DescribeMissingContainer(string containerDn) =>
        $"Deletions were not detected in {containerDn}: the container was not found. " +
        "Objects deleted since the last import may still be present in JIM.";

    /// <summary>
    /// The one text for an unknown, in both places: what could not be confirmed, why, what it would mean, and
    /// the right that lets JIM answer next time.
    /// </summary>
    private static string DescribeUndetermined(DeletedObjectsAccessFinding finding) =>
        $"JIM could not confirm that the account it connects as can list the Deleted Objects container ({finding.ContainerDn}): {finding.Detail}. " +
        "If it cannot, Delta Imports from this domain import no deletions. " +
        "Granting Read Permissions on that container alongside List Contents and Read Property lets JIM give a definite answer.";

    private DeletedObjectsAccessFinding Undetermined(string namingContext, string containerDn, string detail)
    {
        _logger.Warning("LdapConnectorDeletedObjectsAccess: Could not establish whether the account JIM connects as may list '{Container}': {Detail}",
            LogSanitiser.Sanitise(containerDn), detail);

        return new DeletedObjectsAccessFinding
        {
            NamingContext = namingContext,
            ContainerDn = containerDn,
            Outcome = DeletedObjectsAccessOutcome.CouldNotDetermine,
            Detail = detail
        };
    }
}
