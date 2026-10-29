// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// When a saved configuration change must be recorded as awaiting synchronisation (#2615). The decision is the plan's
/// decision 2: graded by the existing classification, with a change JIM cannot classify recorded rather than
/// dropped, because processing a type needlessly costs time and missing one leaves objects wrong.
/// </summary>
[TestFixture]
public class ConfigurationChangeSynchronisationPolicyTests
{
    [TestCase(ConfigurationChangeClass.Cosmetic, false)]
    [TestCase(ConfigurationChangeClass.SyncAffecting, true)]
    [TestCase(ConfigurationChangeClass.Destructive, true)]
    [TestCase(ConfigurationChangeClass.NotClassified, true)]
    public void RequiresSynchronisation_ChangedConfiguration_IsDecidedByItsClass(ConfigurationChangeClass changeClass, bool expected)
    {
        var outcome = ConfigurationChangeCaptureOutcome.Changed(changeClass, ["Scope"]);

        Assert.That(ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(outcome, isInForce: true), Is.EqualTo(expected));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RequiresSynchronisation_NothingChanged_NeverRecords(bool isInForce)
    {
        Assert.That(ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(ConfigurationChangeCaptureOutcome.NoChange, isInForce), Is.False);
    }

    [Test]
    public void RequiresSynchronisation_CreatedInForce_Records()
    {
        // A newly created enabled rule reaches objects that already exist in the Connected System.
        Assert.That(ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(ConfigurationChangeCaptureOutcome.Created, isInForce: true), Is.True);
    }

    [Test]
    public void RequiresSynchronisation_CreatedNotInForce_DoesNotRecord()
    {
        // A rule created disabled applies to nothing. Enabling it later is a Sync-affecting change in its own right,
        // and records then.
        Assert.That(ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(ConfigurationChangeCaptureOutcome.Created, isInForce: false), Is.False);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RequiresSynchronisation_TrackingOff_RecordsEvenWhenNotInForce(bool isInForce)
    {
        // Nothing was compared, so JIM cannot say the change was harmless: a disabled rule may just have been enabled.
        Assert.That(ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(ConfigurationChangeCaptureOutcome.NotTracked, isInForce), Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RequiresSynchronisation_CaptureFailed_RecordsEvenWhenNotInForce(bool isInForce)
    {
        Assert.That(ConfigurationChangeSynchronisationPolicy.RequiresSynchronisation(ConfigurationChangeCaptureOutcome.Failed, isInForce), Is.True);
    }

    [Test]
    public void Describe_ChangedConfiguration_NamesTheSubjectTheSectionsAndTheClass()
    {
        var outcome = ConfigurationChangeCaptureOutcome.Changed(ConfigurationChangeClass.SyncAffecting, ["Scope", "Enabled"]);

        var reason = ConfigurationChangeSynchronisationPolicy.Describe("HR Import", outcome, isNew: false);

        Assert.That(reason, Does.Contain("HR Import").And.Contain("Scope").And.Contain("Enabled").And.Contain("Sync-affecting"));
    }

    [Test]
    public void Describe_ChangedConfigurationWithNoClass_OmitsTheClass()
    {
        var outcome = ConfigurationChangeCaptureOutcome.Changed(ConfigurationChangeClass.NotClassified, ["Scope"]);

        var reason = ConfigurationChangeSynchronisationPolicy.Describe("HR Import", outcome, isNew: false);

        Assert.That(reason, Does.Contain("Scope").And.Not.Contain("NotClassified").And.Not.Contain("()"));
    }

    [Test]
    public void Describe_ChangedConfigurationWithNoSectionsNamed_StillReads()
    {
        var outcome = ConfigurationChangeCaptureOutcome.Changed(ConfigurationChangeClass.Destructive, []);

        var reason = ConfigurationChangeSynchronisationPolicy.Describe("HR Import", outcome, isNew: false);

        Assert.That(reason, Does.Contain("HR Import").And.Contain("Destructive"));
    }

    [Test]
    public void Describe_NewSubject_SaysItWasCreated()
    {
        var reason = ConfigurationChangeSynchronisationPolicy.Describe("HR Import", ConfigurationChangeCaptureOutcome.Created, isNew: true);

        Assert.That(reason, Does.Contain("HR Import").And.Contain("created"));
    }

    [Test]
    public void Describe_ExistingSubjectWithNoEarlierVersion_DoesNotClaimItWasCreated()
    {
        var reason = ConfigurationChangeSynchronisationPolicy.Describe("HR Import", ConfigurationChangeCaptureOutcome.Created, isNew: false);

        Assert.That(reason, Does.Contain("HR Import").And.Not.Contain("created"));
    }

    [Test]
    public void Describe_TrackingOff_SaysWhatChangedIsUnknown()
    {
        var reason = ConfigurationChangeSynchronisationPolicy.Describe("HR Import", ConfigurationChangeCaptureOutcome.NotTracked, isNew: false);

        Assert.That(reason, Does.Contain("HR Import").And.Contain("unknown"));
    }

    [Test]
    public void Describe_CaptureFailed_SaysWhatChangedIsUnknown()
    {
        var reason = ConfigurationChangeSynchronisationPolicy.Describe("HR Import", ConfigurationChangeCaptureOutcome.Failed, isNew: false);

        Assert.That(reason, Does.Contain("HR Import").And.Contain("unknown"));
    }
}
