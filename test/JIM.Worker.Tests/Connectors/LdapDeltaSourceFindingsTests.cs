// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Exceptions;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What the two moments that ask a change source about its readiness do with its findings, for any source: a
/// Delta Import refuses on a proven unavailability and carries a warning for an unknown; Schema Discovery warns
/// of both. The AD-specific wording of the findings themselves is tested with the USN source.
/// </summary>
[TestFixture]
public class LdapDeltaSourceFindingsTests
{
    private const string Changelog = "cn=changelog";
    private const string Accesslog = "cn=accesslog";

    private static LdapDeltaSourceFinding Finding(LdapDeltaSourceOutcome outcome, string subject, string detail) => new()
    {
        Subject = subject,
        Outcome = outcome,
        Detail = detail,
        DeltaImportText = outcome == LdapDeltaSourceOutcome.Available ? null : $"Delta Import text about {subject}: {detail}",
        SchemaDiscoveryText = outcome == LdapDeltaSourceOutcome.Available ? null : $"Schema Discovery text about {subject}: {detail}"
    };

    private static LdapDeltaSourceNotes Fold(params LdapDeltaSourceFinding[] findings) =>
        LdapDeltaSourceFindings.ThrowOnUnavailableOrNote(findings, Log.Logger);

    #region a Delta Import

    [Test]
    public void ThrowOnUnavailableOrNote_OneUnavailableAmongAvailable_ThrowsWithThatSubjectsDeltaImportText()
    {
        var findings = new[]
        {
            Finding(LdapDeltaSourceOutcome.Available, Accesslog, "readable"),
            Finding(LdapDeltaSourceOutcome.Unavailable, Changelog, "no such object")
        };

        Assert.That(() => Fold(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.EqualTo($"Delta Import text about {Changelog}: no such object"));
    }

    /// <summary>
    /// Every unavailable subject is named in the one failure, so the administrator fixes them all in one go
    /// rather than one per failed run.
    /// </summary>
    [Test]
    public void ThrowOnUnavailableOrNote_TwoUnavailable_NamesBothSubjects()
    {
        var findings = new[]
        {
            Finding(LdapDeltaSourceOutcome.Unavailable, Accesslog, "refused"),
            Finding(LdapDeltaSourceOutcome.Unavailable, Changelog, "no such object")
        };

        Assert.That(() => Fold(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.Contains(Accesslog).And.Message.Contains(Changelog));
    }

    /// <summary>
    /// A proven unavailability stops the run, and the failure speaks only to what was proven: an unknown about
    /// another subject is not folded into it.
    /// </summary>
    [Test]
    public void ThrowOnUnavailableOrNote_OneUnavailableAndOneUndetermined_ThrowsWithoutTheUndeterminedText()
    {
        var findings = new[]
        {
            Finding(LdapDeltaSourceOutcome.CouldNotDetermine, Accesslog, "could not tell"),
            Finding(LdapDeltaSourceOutcome.Unavailable, Changelog, "no such object")
        };

        Assert.That(() => Fold(findings), Throws.TypeOf<CannotPerformDeltaImportException>()
            .With.Message.Contains(Changelog).And.Message.Not.Contains("could not tell"));
    }

    [Test]
    public void ThrowOnUnavailableOrNote_UndeterminedOnly_ReturnsItsDeltaImportTextAsTheWarningWithoutThrowing()
    {
        LdapDeltaSourceNotes? notes = null;
        Assert.That(() => notes = Fold(Finding(LdapDeltaSourceOutcome.CouldNotDetermine, Changelog, "could not tell")), Throws.Nothing);

        Assert.That(notes!.Warning, Is.EqualTo($"Delta Import text about {Changelog}: could not tell"));
    }

    [Test]
    public void ThrowOnUnavailableOrNote_TwoUndetermined_WarnsAboutBothSubjectsInOrder()
    {
        var notes = Fold(
            Finding(LdapDeltaSourceOutcome.CouldNotDetermine, Accesslog, "first"),
            Finding(LdapDeltaSourceOutcome.CouldNotDetermine, Changelog, "second"));

        Assert.That(notes.Warning, Is.EqualTo($"Delta Import text about {Accesslog}: first Delta Import text about {Changelog}: second"));
    }

    /// <summary>
    /// Null rather than empty: the connector chains this warning with the pinning note using null-coalescing, so
    /// an empty string would both hide that note and put a blank warning on the Activity.
    /// </summary>
    [Test]
    public void ThrowOnUnavailableOrNote_AllAvailable_ReturnsNoWarning() =>
        Assert.That(Fold(Finding(LdapDeltaSourceOutcome.Available, Accesslog, "readable")).Warning, Is.Null);

    [Test]
    public void ThrowOnUnavailableOrNote_NoFindings_ReturnsNoWarning() =>
        Assert.That(Fold().Warning, Is.Null);

    #endregion

    #region Schema Discovery

    [Test]
    public void SchemaDiscoveryWarnings_MixedFindings_WarnsOfEverythingButAnAvailabilityInOrder()
    {
        var warnings = LdapDeltaSourceFindings.SchemaDiscoveryWarnings(new[]
        {
            Finding(LdapDeltaSourceOutcome.Unavailable, Changelog, "no such object"),
            Finding(LdapDeltaSourceOutcome.Available, Accesslog, "readable"),
            Finding(LdapDeltaSourceOutcome.CouldNotDetermine, "CN=Deleted Objects,DC=x", "could not tell")
        }).ToList();

        Assert.That(warnings, Is.EqualTo(new[]
        {
            $"Schema Discovery text about {Changelog}: no such object",
            "Schema Discovery text about CN=Deleted Objects,DC=x: could not tell"
        }));
    }

    [Test]
    public void SchemaDiscoveryWarnings_AllAvailable_WarnsOfNothing() =>
        Assert.That(LdapDeltaSourceFindings.SchemaDiscoveryWarnings(new[] { Finding(LdapDeltaSourceOutcome.Available, Accesslog, "readable") }), Is.Empty);

    #endregion

    #region notes gathered as the import runs

    /// <summary>
    /// Later, stronger evidence about a subject replaces the earlier note about it: the up-front check could only
    /// say it was unsure, where the search that followed knows nothing was detected and why.
    /// </summary>
    [Test]
    public void Record_ANoteForASubjectAlreadyNoted_ReplacesTheEarlierNoteInPlace()
    {
        var notes = Fold(
            Finding(LdapDeltaSourceOutcome.CouldNotDetermine, Accesslog, "could not tell"),
            Finding(LdapDeltaSourceOutcome.CouldNotDetermine, Changelog, "could not tell either"));

        notes.Record(Accesslog, "the search was refused");

        Assert.That(notes.Warning, Is.EqualTo($"the search was refused Delta Import text about {Changelog}: could not tell either"));
    }

    [Test]
    public void Record_NotesForTwoSubjects_KeepsBoth()
    {
        var notes = new LdapDeltaSourceNotes();

        notes.Record(Accesslog, "first");
        notes.Record(Changelog, "second");

        Assert.That(notes.Warning, Is.EqualTo("first second"));
    }

    [Test]
    public void Record_TheSameSubjectInAnotherCase_IsTheSameSubject()
    {
        var notes = new LdapDeltaSourceNotes();

        notes.Record("CN=Deleted Objects,DC=x", "first");
        notes.Record("cn=deleted objects,dc=x", "second");

        Assert.That(notes.Warning, Is.EqualTo("second"));
    }

    [Test]
    public void Warning_WithNothingRecorded_IsNull() =>
        Assert.That(new LdapDeltaSourceNotes().Warning, Is.Null);

    #endregion
}
