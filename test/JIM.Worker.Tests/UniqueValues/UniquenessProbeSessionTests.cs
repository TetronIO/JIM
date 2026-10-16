// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Worker.UniqueValues;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// The worker's run-scoped probe session (Unique Value Generation, #242, release 3, plan Phase 7 item 2): lazy open
/// and reuse, close at dispose, the control value rules, failure latching per system and per attribute, the one
/// warning per system per run, and a timed-out open that completes late being closed rather than left open.
/// </summary>
[TestFixture]
public class UniquenessProbeSessionTests
{
    private const int CorporateAdId = 7;
    private const int PartnerLdapId = 8;
    private static readonly UniquenessProbeTarget SamAccountName = new(CorporateAdId, 700);
    private static readonly UniquenessProbeTarget Mail = new(CorporateAdId, 701);
    private static readonly UniquenessProbeTarget PartnerUid = new(PartnerLdapId, 800);

    // ---- Lifetime ----

    [Test]
    public async Task ProbeAsync_FirstUse_OpensTheConnectionLazilyAndReusesItAsync()
    {
        var connector = new FakeProbingConnector("asmith");
        var host = CorporateAdHost(connector).WithControlValues(700, "asmith");
        var session = new UniquenessProbeSession(host, CancellationToken.None);

        Assert.That(host.LoadCount + host.CreateConnectorCount + connector.OpenCount, Is.Zero, "nothing is opened until the run first probes");

        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        await session.ProbeAsync(SamAccountName, ["ada.lovelace"]);
        await session.ProbeAsync(Mail, ["joe.bloggs@corp.local"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(host.LoadCount, Is.EqualTo(1));
            Assert.That(host.CreateConnectorCount, Is.EqualTo(1));
            Assert.That(connector.OpenCount, Is.EqualTo(1), "one connection per system for the whole run");
            Assert.That(connector.Requests, Has.Count.EqualTo(3));
        }

        await session.DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_ClosesAndDisposesEveryConnectionAndTheHostAsync()
    {
        var connector = new FakeProbingConnector();
        var host = CorporateAdHost(connector);
        var session = new UniquenessProbeSession(host, CancellationToken.None);
        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);

        await session.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.IsOpen, Is.False);
            Assert.That(connector.CloseCount, Is.EqualTo(1));
            Assert.That(connector.DisposeCount, Is.EqualTo(1));
            Assert.That(host.Disposed, Is.True);
        }
    }

    // ---- Answers and the control value ----

    [Test]
    public async Task ProbeAsync_ControlAvailable_SendsItAndReportsEachCandidateAsync()
    {
        var connector = new FakeProbingConnector("asmith", "joe.bloggs");
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector).WithControlValues(700, "asmith"), CancellationToken.None);

        var result = await session.ProbeAsync(SamAccountName, ["joe.bloggs", "joe.bloggs1"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConnectedSystemName, Is.EqualTo("Corporate AD"));
            Assert.That(result.IsProbed, Is.True);
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
            Assert.That(connector.Requests[0].AttributeName, Is.EqualTo("sAMAccountName"));
            Assert.That(connector.Requests[0].ControlValue, Is.EqualTo("asmith"));
        }
    }

    /// <summary>
    /// A SCIM provider searches one resource type's endpoint and a database one Object Type's table (#1941), so the
    /// request names the object type the attribute belongs to, not merely the attribute.
    /// </summary>
    [Test]
    public async Task ProbeAsync_AttributeOfASecondObjectType_NamesThatObjectTypeOnTheRequestAsync()
    {
        var connector = new FakeProbingConnector();
        var system = FakeUniquenessProbeHost.ProbingSystem(CorporateAdId, "Corporate AD", true, Attribute(700, "sAMAccountName"));
        system.ObjectTypes!.Add(new ConnectedSystemObjectType { Id = 71, Name = "group", Attributes = [Attribute(710, "displayName")] });
        await using var session = new UniquenessProbeSession(new FakeUniquenessProbeHost().WithSystem(system, _ => connector), CancellationToken.None);

        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        await session.ProbeAsync(new UniquenessProbeTarget(CorporateAdId, 710), ["Finance Team"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.Requests[0].ObjectTypeName, Is.EqualTo("user"));
            Assert.That(connector.Requests[1].ObjectTypeName, Is.EqualTo("group"));
            Assert.That(connector.Requests[1].AttributeName, Is.EqualTo("displayName"));
        }
    }

    /// <summary>
    /// Plan decision 14, revised 2026-10-06: an empty target is normal on a first load, so with no control the probe
    /// still runs, a hit is acted on, and nothing is reported.
    /// </summary>
    [Test]
    public async Task ProbeAsync_NoControlValueHeld_ProbesWithoutOneAndRaisesNoWarningAsync()
    {
        var connector = new FakeProbingConnector("joe.bloggs");
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        var result = await session.ProbeAsync(SamAccountName, ["joe.bloggs", "joe.bloggs1"]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.Requests[0].ControlValue, Is.Null);
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
            Assert.That(session.GetRunWarnings(), Is.Empty);
        }
    }

    [Test]
    public async Task ProbeAsync_EveryControlValueIsACandidate_ProbesWithoutAControlAsync()
    {
        var connector = new FakeProbingConnector("joe.bloggs");
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector).WithControlValues(700, "JOE.BLOGGS"), CancellationToken.None);

        var result = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.Requests[0].ControlValue, Is.Null, "a control that is itself a candidate proves nothing");
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
        }
    }

    [Test]
    public async Task ProbeAsync_FirstControlIsACandidate_UsesAnotherAsync()
    {
        var connector = new FakeProbingConnector("joe.bloggs", "asmith");
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector).WithControlValues(700, "joe.bloggs", "asmith"), CancellationToken.None);

        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);

        Assert.That(connector.Requests[0].ControlValue, Is.EqualTo("asmith"));
    }

    // ---- Not probed ----

    [Test]
    public async Task ProbeAsync_ConnectorDoesNotProbe_IsNotProbedAndCreatesNoConnectorAsync()
    {
        var host = new FakeUniquenessProbeHost().WithSystem(
            FakeUniquenessProbeHost.ProbingSystem(CorporateAdId, "Corporate AD", supportsProbe: false, Attribute(700, "sAMAccountName")),
            _ => new FakeProbingConnector());
        await using var session = new UniquenessProbeSession(host, CancellationToken.None);

        var result = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsProbed, Is.False);
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.NotFound));
            Assert.That(host.CreateConnectorCount, Is.Zero);
            Assert.That(session.GetRunWarnings(), Is.Empty);
        }
    }

    [Test]
    public async Task ProbeAsync_AttributeTheConnectorCannotProbe_IsNotProbedAsync()
    {
        var connector = new FakeProbingConnector();
        connector.UnprobeableAttributes.Add("sAMAccountName");
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        var result = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsProbed, Is.False);
            Assert.That(connector.Requests, Is.Empty);
        }
    }

    // ---- Failure latching ----

    [Test]
    public async Task ProbeAsync_OpenFails_LatchesTheSystemWithoutRetryingAndLeavesOthersAloneAsync()
    {
        var failing = new FakeProbingConnector { OpenThrows = new InvalidOperationException("Connection refused.") };
        var partner = new FakeProbingConnector("joe.bloggs");
        var host = CorporateAdHost(failing).WithSystem(
            FakeUniquenessProbeHost.ProbingSystem(PartnerLdapId, "Partner LDAP", true, Attribute(800, "uid")), _ => partner);
        await using var session = new UniquenessProbeSession(host, CancellationToken.None);

        var first = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        var second = await session.ProbeAsync(SamAccountName, ["ada.lovelace"]);
        var other = await session.ProbeAsync(PartnerUid, ["joe.bloggs"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(first.IsProbed, Is.True);
            Assert.That(second.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(failing.OpenCount, Is.EqualTo(1), "a system that could not be opened is not retried per object");
            Assert.That(other.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
        }
    }

    [Test]
    public async Task ProbeAsync_SearchThrows_LatchesTheSystemAsync()
    {
        var connector = new FakeProbingConnector { ProbeThrows = new InvalidOperationException("Server down.") };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        var first = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        await session.ProbeAsync(Mail, ["joe.bloggs@corp.local"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(connector.Requests, Has.Count.EqualTo(1), "the system is not asked again this run, for any attribute");
        }
    }

    [Test]
    public async Task ProbeAsync_SearchFailed_LatchesTheSystemAsync()
    {
        var connector = new FakeProbingConnector { ProbeAnswer = r => UniquenessProbeResult.Failed(r.Candidates.Count, "The directory refused the probe") };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        await session.ProbeAsync(Mail, ["joe.bloggs@corp.local"]);

        Assert.That(connector.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ProbeAsync_ControlNotReturned_LatchesThatAttributeOnlyAsync()
    {
        var connector = new FakeProbingConnector("asmith", "asmith@corp.local") { BlindToControl = true };
        var host = CorporateAdHost(connector).WithControlValues(700, "asmith");
        await using var session = new UniquenessProbeSession(host, CancellationToken.None);

        var blind = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        await session.ProbeAsync(SamAccountName, ["ada.lovelace"]);
        var other = await session.ProbeAsync(Mail, ["joe.bloggs@corp.local"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blind.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(connector.Requests.Count(r => r.AttributeName == "sAMAccountName"), Is.EqualTo(1), "the blind attribute is not asked again");
            Assert.That(other.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }), "the system's other attributes are still probed");
        }
    }

    [Test]
    public async Task ProbeAsync_ConnectedSystemNotFound_IsUndeterminedAsync()
    {
        await using var session = new UniquenessProbeSession(new FakeUniquenessProbeHost(), CancellationToken.None);

        var result = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(session.GetRunWarnings().Single(), Does.Contain("It could not be found"));
        }
    }

    // ---- Run warnings ----

    [Test]
    public async Task GetRunWarnings_SystemCouldNotBeProbed_OneWarningCountingTheValuesChosenAsync()
    {
        var connector = new FakeProbingConnector { OpenThrows = new InvalidOperationException("Connection refused.") };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        await session.ProbeAsync(SamAccountName, ["ada.lovelace"]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);
        session.RecordValueChosenWithoutProbe(CorporateAdId);

        Assert.That(session.GetRunWarnings(), Is.EqualTo(new[]
        {
            "JIM couldn't probe Corporate AD for values already in use. Connecting to it failed: Connection refused. JIM chose 2 values using its own records only."
        }));
    }

    [Test]
    public async Task GetRunWarnings_OneValueChosen_UsesTheSingularAsync()
    {
        var connector = new FakeProbingConnector { OpenThrows = new InvalidOperationException("Connection refused.") };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);

        Assert.That(session.GetRunWarnings().Single(), Does.EndWith("JIM chose 1 value using its own records only."));
    }

    [Test]
    public async Task GetRunWarnings_UndeterminedButNoValueChosenWithoutAProbe_RaisesNoWarningAsync()
    {
        var connector = new FakeProbingConnector { OpenThrows = new InvalidOperationException("Connection refused.") };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);

        Assert.That(session.GetRunWarnings(), Is.Empty);
    }

    /// <summary>
    /// #1940: a probe that answered but searched less than everywhere the value must be unique keeps its answers, and
    /// the run says once what it could not reach, however many objects were probed.
    /// </summary>
    [Test]
    public async Task ProbeAsync_AnsweredWithACaveat_KeepsTheAnswersAndWarnsOnceAsync()
    {
        const string caveat = "The userPrincipalName attribute is unique across the Active Directory forest, but no Global Catalog could be searched";
        var connector = new FakeProbingConnector
        {
            ProbeAnswer = request => UniquenessProbeResult.FromValuesFound(request, ["joe.bloggs@corp.local"]).WithCaveat(caveat)
        };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        var first = await session.ProbeAsync(Mail, ["joe.bloggs@corp.local", "joe.bloggs2@corp.local"]);
        await session.ProbeAsync(Mail, ["ada.lovelace@corp.local"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
            Assert.That(connector.Requests, Has.Count.EqualTo(2), "a caveat latches nothing: the next object is probed too");
            Assert.That(session.GetRunWarnings(), Is.EqualTo(new[]
            {
                $"JIM's probe of Corporate AD for values already in use was incomplete. {caveat}."
            }));
        }
    }

    [Test]
    public async Task GetRunWarnings_DifferentCaveats_OneWarningEachInTheOrderMetAsync()
    {
        var connector = new FakeProbingConnector
        {
            ProbeAnswer = request => UniquenessProbeResult.FromValuesFound(request, []).WithCaveat($"The {request.AttributeName} attribute was searched in one domain only")
        };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None);

        await session.ProbeAsync(Mail, ["joe.bloggs@corp.local"]);
        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        await session.ProbeAsync(Mail, ["ada.lovelace@corp.local"]);

        Assert.That(session.GetRunWarnings(), Is.EqualTo(new[]
        {
            "JIM's probe of Corporate AD for values already in use was incomplete. The mail attribute was searched in one domain only.",
            "JIM's probe of Corporate AD for values already in use was incomplete. The sAMAccountName attribute was searched in one domain only."
        }));
    }

    [Test]
    public async Task GetRunWarnings_EveryProbeAnswered_RaisesNoWarningAsync()
    {
        var connector = new FakeProbingConnector("asmith");
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector).WithControlValues(700, "asmith"), CancellationToken.None);

        await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);

        Assert.That(session.GetRunWarnings(), Is.Empty);
    }

    // ---- Timeouts ----

    /// <summary>
    /// An open that outlives the timeout may still succeed afterwards. Closing at the timeout would race it and leave
    /// the late connection open, so the session lets go and the open closes its own connection once it finishes:
    /// exactly once, and never left open.
    /// </summary>
    [Test]
    public async Task ProbeAsync_OpenTimesOutThenCompletesLate_ClosesAndDisposesTheConnectionExactlyOnceAsync()
    {
        using var gate = new ManualResetEventSlim(false);
        var connector = new FakeProbingConnector { OpenGate = gate };
        var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None, TimeSpan.FromMilliseconds(200));

        var result = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);
        var warnings = session.GetRunWarnings();

        gate.Set();
        await WaitUntilAsync(() => connector.CloseCount > 0 && connector.DisposeCount > 0);
        await session.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(warnings.Single(), Does.Contain("did not complete within"));
            Assert.That(connector.IsOpen, Is.False, "the late connection must not be left open");
            Assert.That(connector.CloseCount, Is.EqualTo(1));
            Assert.That(connector.DisposeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ProbeAsync_SearchTimesOut_LatchesTheSystemAsync()
    {
        var connector = new FakeProbingConnector
        {
            ProbeAnswer = r =>
            {
                Thread.Sleep(1000);
                return UniquenessProbeResult.FromValuesFound(r, []);
            }
        };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None, TimeSpan.FromMilliseconds(200));

        var result = await session.ProbeAsync(SamAccountName, ["joe.bloggs"]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(session.GetRunWarnings().Single(), Does.Contain("did not answer within"));
        }
    }

    // ---- Several attributes of one system at once (#2023) ----

    [Test]
    public async Task ProbeAttributesAsync_SeveralAttributesOfOneSystem_SendsThemToTheConnectorTogetherAsync()
    {
        var connector = new FakeProbingConnector("asmith", "asmith@corp.local", "joe.bloggs@corp.local");
        var host = CorporateAdHost(connector).WithControlValues(700, "asmith").WithControlValues(701, "asmith@corp.local");
        await using var session = new UniquenessProbeSession(host, CancellationToken.None);

        var results = await session.ProbeAttributesAsync(CorporateAdId,
            [new UniquenessProbeBatch(700, ["joe.bloggs", "joe.bloggs1"]), new UniquenessProbeBatch(701, ["joe.bloggs@corp.local"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.BatchSizes, Is.EqualTo(new[] { 2 }), "one call for the system");
            Assert.That(connector.Requests.Select(r => (r.AttributeName, r.ControlValue)), Is.EqualTo(new[]
            {
                ("sAMAccountName", "asmith"),
                ("mail", "asmith@corp.local")
            }), "each attribute vouched for by its own control value");
            Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
            {
                new[] { UniquenessProbeOutcome.NotFound, UniquenessProbeOutcome.NotFound },
                new[] { UniquenessProbeOutcome.Found }
            }));
            Assert.That(results.Select(r => r.ConnectedSystemName), Is.All.EqualTo("Corporate AD"));
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_AttributeASystemWideSearchCannotAnswerFor_IsNotProbedAndTheRestAreAsync()
    {
        var connector = new FakeProbingConnector("asmith");
        connector.UnprobeableAttributes.Add("mail");
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector).WithControlValues(700, "asmith"), CancellationToken.None);

        var results = await session.ProbeAttributesAsync(CorporateAdId,
            [new UniquenessProbeBatch(700, ["joe.bloggs"]), new UniquenessProbeBatch(701, ["joe.bloggs@corp.local"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.IsProbed), Is.EqualTo(new[] { true, false }));
            Assert.That(connector.Requests.Select(r => r.AttributeName), Is.EqualTo(new[] { "sAMAccountName" }));
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_OneAttributeBlindToItsControl_LatchesThatAttributeOnlyAsync()
    {
        var connector = new FakeProbingConnector("asmith")
        {
            ProbeAnswer = r => r.AttributeName == "mail"
                ? UniquenessProbeResult.FromValuesFound(r, [])
                : UniquenessProbeResult.FromValuesFound(r, ["asmith"])
        };
        var host = CorporateAdHost(connector).WithControlValues(700, "asmith").WithControlValues(701, "asmith@corp.local");
        await using var session = new UniquenessProbeSession(host, CancellationToken.None);

        var first = await session.ProbeAttributesAsync(CorporateAdId,
            [new UniquenessProbeBatch(700, ["joe.bloggs"]), new UniquenessProbeBatch(701, ["joe.bloggs@corp.local"])]);
        var second = await session.ProbeAttributesAsync(CorporateAdId,
            [new UniquenessProbeBatch(700, ["ada.lovelace"]), new UniquenessProbeBatch(701, ["ada.lovelace@corp.local"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first[0].Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(first[1].Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(second[0].Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }), "the system's other attributes are still asked");
            Assert.That(second[1].Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(connector.BatchSizes, Is.EqualTo(new[] { 2, 1 }), "mail is not asked again this run");
        }
    }

    /// <summary>
    /// A failure says the system is not to be relied on for the rest of the run, but answers it gave alongside it in the
    /// same call were complete searches, and stand.
    /// </summary>
    [Test]
    public async Task ProbeAttributesAsync_ConnectorFailsOneAttribute_KeepsTheOthersAnswersAndLatchesTheSystemAsync()
    {
        var connector = new FakeProbingConnector("asmith", "joe.bloggs")
        {
            ProbeAnswer = r => r.AttributeName == "mail"
                ? UniquenessProbeResult.Failed(r.Candidates.Count, "The directory refused the probe")
                : UniquenessProbeResult.FromValuesFound(r, ["asmith", "joe.bloggs"])
        };
        var host = CorporateAdHost(connector).WithControlValues(700, "asmith").WithControlValues(701, "asmith@corp.local");
        await using var session = new UniquenessProbeSession(host, CancellationToken.None);

        var first = await session.ProbeAttributesAsync(CorporateAdId,
            [new UniquenessProbeBatch(700, ["joe.bloggs"]), new UniquenessProbeBatch(701, ["joe.bloggs@corp.local"])]);
        var second = await session.ProbeAttributesAsync(CorporateAdId, [new UniquenessProbeBatch(700, ["ada.lovelace"])]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first[0].Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
            Assert.That(first[1].Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(second[0].Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(connector.BatchSizes, Is.EqualTo(new[] { 2 }), "the system is not asked again this run");
            Assert.That(session.GetRunWarnings().Single(), Does.Contain("refused"));
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_ConnectorAnswersForADifferentNumberOfAttributes_LatchesTheSystemAsync()
    {
        var connector = new FakeProbingConnector("asmith") { AlterBatchAnswer = results => [results[0]] };
        var host = CorporateAdHost(connector).WithControlValues(700, "asmith").WithControlValues(701, "asmith@corp.local");
        await using var session = new UniquenessProbeSession(host, CancellationToken.None);

        var results = await session.ProbeAttributesAsync(CorporateAdId,
            [new UniquenessProbeBatch(700, ["joe.bloggs"]), new UniquenessProbeBatch(701, ["joe.bloggs@corp.local"])]);
        session.RecordValueChosenWithoutProbe(CorporateAdId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results.SelectMany(r => r.Outcomes), Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine),
                "nothing it said can be matched to an attribute");
            Assert.That(session.GetRunWarnings().Single(), Does.Contain("different number"));
        }
    }

    /// <summary>
    /// A Connector that searches one attribute at a time takes up to the timeout for each, so a call carrying several
    /// is given that much longer before the system is treated as unreachable.
    /// </summary>
    [Test]
    public async Task ProbeAttributesAsync_SeveralAttributes_AllowsTheTimeoutForEachAsync()
    {
        var connector = new FakeProbingConnector
        {
            ProbeAnswer = r =>
            {
                Thread.Sleep(700);
                return UniquenessProbeResult.FromValuesFound(r, []);
            }
        };
        await using var session = new UniquenessProbeSession(CorporateAdHost(connector), CancellationToken.None, TimeSpan.FromSeconds(1));

        var results = await session.ProbeAttributesAsync(CorporateAdId,
            [new UniquenessProbeBatch(700, ["joe.bloggs"]), new UniquenessProbeBatch(701, ["joe.bloggs@corp.local"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.SelectMany(r => r.Outcomes), Is.All.EqualTo(UniquenessProbeOutcome.NotFound));
            Assert.That(session.GetRunWarnings(), Is.Empty);
        }
    }

    private static FakeUniquenessProbeHost CorporateAdHost(FakeProbingConnector connector) =>
        new FakeUniquenessProbeHost().WithSystem(
            FakeUniquenessProbeHost.ProbingSystem(CorporateAdId, "Corporate AD", true, Attribute(700, "sAMAccountName"), Attribute(701, "mail")),
            _ => connector);

    private static ConnectedSystemObjectTypeAttribute Attribute(int id, string name) => new() { Id = id, Name = name, Type = AttributeDataType.Text };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
    }
}
