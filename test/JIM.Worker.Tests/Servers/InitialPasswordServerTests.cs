// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Tests.Services;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What is wrong with a Synchronisation Rule's initial-password settings, assessed before they are saved
/// (#1121). Delivering the password itself is the Password Delivery Service's queue (#1697): provisioned
/// accounts are staged onto it and attempted, retried and parked there, which is tested on that service and
/// on <c>InitialPasswordProvisioningDatabaseTests</c>. What is under test here is purely the pre-save
/// assessment, which stays on this class regardless of where delivery happens.
/// </summary>
[TestFixture]
public class InitialPasswordServerTests
{
    private InitialPasswordServer _server = null!;

    [SetUp]
    public void Setup()
    {
        var syncRepo = new SyncRepository();
        _server = new InitialPasswordServer(syncRepo, new PasswordGeneratorService(), () => new TestCredentialProtection());
    }

    #region assessing a configuration before it is saved

    /// <summary>
    /// A rule with no initial-password configuration at all has nothing that could be wrong with it. This is the
    /// state every Synchronisation Rule starts in, so a caller must be able to ask about it without a guard.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithNoConfiguration_FindsNothingToFix()
    {
        Assert.That(_server.AssessConfiguration(null, null), Is.Empty);
    }

    /// <summary>
    /// Settings that are switched off are not settings that will run, so they are nobody's problem to fix. A rule
    /// carrying an unusable configuration it does not use must still be savable, or turning the feature off would
    /// not be a way out of an unsatisfiable one.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithTheFeatureOff_FindsNothingToFix()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = false,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = UnsatisfiablePolicy()
        };

        Assert.That(_server.AssessConfiguration(configuration, null), Is.Empty);
    }

    /// <summary>
    /// A rule set to give every account one password, with no password to give, would park every account it
    /// provisions. That is the same class of fault as an unsatisfiable generator configuration and is reported
    /// the same way, before it is saved rather than one account at a time afterwards.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithAStaticSourceAndNoPasswordSet_ReportsThatNoPasswordHasBeenSet()
    {
        var configuration = new SyncRuleInitialPassword { Enabled = true, Source = InitialPasswordSource.Static };

        var problems = _server.AssessConfiguration(configuration, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problems, Has.Count.EqualTo(1));
            Assert.That(problems[0], Does.Contain("no password has been set"));
        }
    }

    /// <summary>
    /// A stored password needs no re-checking: it was assessed when it was set, and it is never decrypted to be
    /// looked at again.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithAStaticSourceAndAPasswordSet_FindsNothingToFix()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPasswordEncryptedValue = "an-encrypted-value",
            // Deliberately unsatisfiable, and deliberately ignored: no password is generated for a static source.
            CustomPolicy = UnsatisfiablePolicy()
        };

        Assert.That(_server.AssessConfiguration(configuration, null), Is.Empty);
    }

    /// <summary>
    /// A generator configuration that cannot produce a password is reported in the generator's own words, so the
    /// administrator is told what to change rather than that something is wrong.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithAnUnsatisfiableCustomPolicy_ReportsWhatTheGeneratorFound()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = UnsatisfiablePolicy()
        };

        Assert.That(_server.AssessConfiguration(configuration, null), Is.Not.Empty);
    }

    /// <summary>
    /// The discovered source generates from settings derived from the target, not from the custom policy sitting
    /// beside it, so an unusable custom policy must not be reported against a rule that is not using it.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithTheDiscoveredSource_IgnoresTheCustomPolicyItIsNotUsing()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Discovered,
            CustomPolicy = UnsatisfiablePolicy()
        };

        Assert.That(_server.AssessConfiguration(configuration, null), Is.Empty);
    }

    /// <summary>
    /// The target's own policy is part of the question: a configuration JIM can satisfy but the target would
    /// refuse parks every account just as surely as one JIM cannot satisfy at all.
    /// </summary>
    [Test]
    public void AssessConfiguration_WithACustomPolicyTheTargetWouldRefuse_ReportsIt()
    {
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy { Style = PasswordGenerationStyle.RandomCharacters, Length = 12 }
        };

        var problems = _server.AssessConfiguration(configuration, new ConnectedSystemPasswordPolicy { MinimumLength = 20 });

        Assert.That(problems, Is.Not.Empty, "the target requires more characters than this configuration can produce");
    }

    /// <summary>
    /// A passphrase of no words cannot be generated, and is the shortest way to express an unsatisfiable
    /// configuration without depending on any particular target policy.
    /// </summary>
    private static PasswordGenerationPolicy UnsatisfiablePolicy() =>
        new() { Style = PasswordGenerationStyle.Words, WordCount = 0 };

    #endregion
}
