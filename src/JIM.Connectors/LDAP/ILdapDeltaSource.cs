// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// How a Delta Import learns what changed since the last import, for one family of directory. Each directory
/// family keeps its changes somewhere different (uSNChanged and the Deleted Objects container, cn=accesslog,
/// cn=changelog) and the import used to know all three; now it knows this contract, and
/// <see cref="LdapDeltaSources"/> picks the implementation from the directory type.
/// <para>
/// The order the import calls these in, on the first page of a Delta Import: <see cref="CaptureWatermarkAsync"/>
/// inside the rootDSE read, <see cref="VerifyContinuity"/> against the record the last import left, then
/// <see cref="VerifyReadinessAsync"/>, whose findings either stop the run or become its warning
/// (<see cref="LdapDeltaSourceFindings"/>), then <see cref="HasBaseline"/>, then <see cref="ReadChangesAsync"/>.
/// Later pages call only the last two. A Full Import calls <see cref="CaptureWatermarkAsync"/> alone, to leave a
/// baseline for the first Delta Import. Schema Discovery calls <see cref="VerifyReadinessAsync"/> alone, to warn.
/// </para>
/// <para>Implementations hold no per-run state: what they record goes on the context's notes or on the rootDSE record.</para>
/// </summary>
internal interface ILdapDeltaSource
{
    /// <summary>
    /// Writes this source's watermark onto the record about to be persisted, reading whatever the rootDSE entry
    /// does not itself carry. Runs on every import, Full and Delta.
    /// </summary>
    Task CaptureWatermarkAsync(SearchResultEntry rootDseEntry, LdapConnectorRootDse rootDse, TimeSpan searchTimeout);

    /// <summary>
    /// Establishes that the persisted watermark still means something against the directory just reached, and
    /// throws when it does not: a USN read back from a different domain controller, a changelog trimmed past the
    /// baseline. A source whose watermark cannot be invalidated this way does nothing.
    /// </summary>
    /// <exception cref="JIM.Models.Exceptions.CannotPerformDeltaImportException">The baseline no longer applies.</exception>
    void VerifyContinuity(LdapConnectorRootDse previous, LdapConnectorRootDse current);

    /// <summary>
    /// Establishes whether the account JIM connects as can read where this source keeps its changes, without
    /// reading any. One finding per subject checked; empty when there is nothing to check.
    /// </summary>
    /// <param name="namingContexts">The partitions in question; empty at Schema Discovery before any are selected, when a source falls back to the directory's default naming context or checks nothing.</param>
    Task<IReadOnlyList<LdapDeltaSourceFinding>> VerifyReadinessAsync(LdapConnectorRootDse rootDse, IReadOnlyCollection<string> namingContexts, CancellationToken cancellationToken);

    /// <summary>Whether the record the last import left carries a watermark this source can read from.</summary>
    bool HasBaseline(LdapConnectorRootDse previous);

    /// <summary>
    /// Reads this page's changes into the result. A source that pages across calls adds its pagination tokens to
    /// the result and the import calls again with them; one that reads everything in one call adds none.
    /// </summary>
    Task ReadChangesAsync(LdapDeltaReadContext context, ConnectedSystemImportResult result, CancellationToken cancellationToken);
}
