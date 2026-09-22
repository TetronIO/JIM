// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exceptions;
using Serilog;
namespace JIM.Connectors.LDAP;

/// <summary>
/// What the two moments that ask a change source about its readiness do with the answer. Static so that the
/// decision is the same for every source and testable on its own.
/// </summary>
internal static class LdapDeltaSourceFindings
{
    /// <summary>
    /// Folds a Delta Import's findings into what the run does next: throws on any proven unavailability, naming
    /// every unavailable subject at once so the administrator fixes them in one go; otherwise returns a note per
    /// subject JIM could not be sure about, for the run to carry as its warning and to add to as the searches
    /// themselves report. Nothing is noted for an available subject.
    /// </summary>
    /// <exception cref="CannotPerformDeltaImportException">At least one subject is provably unavailable.</exception>
    internal static LdapDeltaSourceNotes ThrowOnUnavailableOrNote(IReadOnlyCollection<LdapDeltaSourceFinding> findings, ILogger logger)
    {
        var unavailable = findings.Where(f => f.Outcome == LdapDeltaSourceOutcome.Unavailable).ToList();
        if (unavailable.Count > 0)
        {
            logger.Warning("LdapDeltaSourceFindings: Refusing the Delta Import; {Count} change source(s) cannot be read by the account JIM connects as", unavailable.Count);
            throw new CannotPerformDeltaImportException(string.Join(" ", unavailable.Select(f => f.DeltaImportText)));
        }

        var notes = new LdapDeltaSourceNotes();
        foreach (var finding in findings.Where(f => f.Outcome == LdapDeltaSourceOutcome.CouldNotDetermine))
            notes.Record(finding.Subject, finding.DeltaImportText ?? finding.Detail);

        return notes;
    }

    /// <summary>
    /// The warnings Schema Discovery adds to the schema for every finding that is not an availability, in the
    /// order the source reported them.
    /// </summary>
    internal static IEnumerable<string> SchemaDiscoveryWarnings(IReadOnlyCollection<LdapDeltaSourceFinding> findings) =>
        findings.Where(f => f.Outcome != LdapDeltaSourceOutcome.Available)
            .Select(f => f.SchemaDiscoveryText ?? f.Detail);
}
