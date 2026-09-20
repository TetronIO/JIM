// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Covers the decision of which password an initial-password configuration resolves to, split out on its own
/// (#1697) so it is reusable without a Connector to send to.
/// <para>
/// The interesting behaviour here is not the happy path. It is which configurations JIM refuses to send at all,
/// because a refusal always parks: only a person changing the configuration can produce a different answer, so
/// nothing here should ever suggest retrying unchanged.
/// </para>
/// </summary>
[TestFixture]
public class InitialPasswordResolverTests
{
    private InitialPasswordResolver _resolver = null!;
    private TestCredentialProtection _credentialProtection = null!;

    [SetUp]
    public void SetUp()
    {
        _credentialProtection = new TestCredentialProtection();
        _resolver = new InitialPasswordResolver(new PasswordGeneratorService(), _credentialProtection);
    }

    private SyncRuleInitialPassword StaticConfiguration(string? password) =>
        new()
        {
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPasswordEncryptedValue = password == null ? null : _credentialProtection.Protect(password),
            // Deliberately populated, so the tests below prove the static path ignores it rather than merely not
            // reaching it by accident.
            CustomPolicy = new PasswordGenerationPolicy { Length = 12 }
        };

    [Test]
    public void Resolve_MissingStaticPassword_ParksWithConfigurationFault()
    {
        var result = _resolver.Resolve(StaticConfiguration(null), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsUsable, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(result.Message, Does.Contain("no password has been set"));
        }
    }

    [Test]
    public void Resolve_UndecryptableStaticPassword_ParksAndNeverRepeatsTheCiphertextOrPassword()
    {
        // An encryption key that has been rotated or lost takes the password with it. Retrying reaches the same
        // answer for ever, and only an administrator setting the password again resolves it.
        var configuration = StaticConfiguration("Brown-Chicken-Ladder-47");
        _credentialProtection.FailToDecrypt = true;

        var result = _resolver.Resolve(configuration, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsUsable, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(result.Message, Does.Not.Contain(configuration.StaticPasswordEncryptedValue!),
                "the stored value must not be repeated into an Activity or a log on its way out");
            Assert.That(result.Message, Does.Not.Contain("Brown-Chicken-Ladder-47"));
        }
    }

    [Test]
    public void Resolve_EmptyStaticPassword_ParksWithConfigurationFault()
    {
        // The encrypted value is present (a whitespace-only password protects to something other than an empty
        // string), so this exercises the post-decrypt emptiness check rather than the "nothing stored at all"
        // path above.
        var result = _resolver.Resolve(StaticConfiguration(" "), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsUsable, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(result.Message, Does.Contain("is empty"));
        }
    }

    [Test]
    public void Resolve_StaticPasswordRefusedByTheDiscoveredPolicy_Parks()
    {
        // The same judgement as an unsatisfiable generator configuration, and it matters more here: one password
        // is going to every account this rule provisions, so a rejection is not one account's problem.
        var result = _resolver.Resolve(StaticConfiguration("short"), new ConnectedSystemPasswordPolicy { MinimumLength = 30 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsUsable, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
            Assert.That(result.Message, Does.Contain("30"), "the administrator needs to know what the target actually requires");
            Assert.That(result.Message, Does.Not.Contain("short"), "the reason must never repeat the password it refused");
        }
    }

    [Test]
    public void Resolve_UnsatisfiableCustomGeneratorPolicy_ParksWithoutGenerating()
    {
        // An impossible configuration is caught before anything is generated. Parking rather than retrying is
        // the same judgement as a policy rejection: only an administrator changing the configuration resolves it.
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy
            {
                Length = 4,
                MinimumUppercase = 3,
                MinimumLowercase = 3,
                MinimumDigits = 3,
                MinimumSymbols = 3
            }
        };

        var result = _resolver.Resolve(configuration, null);

        Assert.That(result.IsUsable, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo(PasswordSetFailureReason.ConfigurationFault));
    }

    [Test]
    public void Resolve_CustomSettingsThatCannotSatisfyTheDiscoveredPolicy_ParksWithoutGenerating()
    {
        // Custom settings decide what JIM generates; they do not exempt the result from what the target demands.
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy { Length = 12 }
        };

        var result = _resolver.Resolve(configuration, new ConnectedSystemPasswordPolicy { MinimumLength = 30 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsUsable, Is.False);
            Assert.That(result.Message, Does.Contain("30"), "the administrator needs to know what the target actually requires");
        }
    }

    [Test]
    public void Resolve_StaticSourceIgnoresTheGeneratorSettings()
    {
        // Custom settings are kept while the source is Static so that switching between the two is not
        // destructive. They must have no bearing on what is resolved while Static is selected.
        var result = _resolver.Resolve(StaticConfiguration("Brown-Chicken-Ladder-47"), new ConnectedSystemPasswordPolicy { MinimumLength = 8 });

        Assert.That(result.IsUsable, Is.True);
        Assert.That(result.Password, Has.Length.EqualTo(23), "a 12-character generated password would mean the generator ran");
    }

    [Test]
    public void Resolve_DiscoveredSource_DerivesFromTheDiscoveredPolicy()
    {
        // The point of the Discovered source: a target demanding more than JIM's default gets a password that
        // satisfies it, without an administrator retyping the rule.
        var configuration = new SyncRuleInitialPassword { Enabled = true, Source = InitialPasswordSource.Discovered };
        var discovered = new ConnectedSystemPasswordPolicy { MinimumLength = 24 };

        var result = _resolver.Resolve(configuration, discovered);

        Assert.That(result.IsUsable, Is.True);
        Assert.That(result.Password, Has.Length.GreaterThanOrEqualTo(24));
    }

    [Test]
    public void Resolve_CustomSource_UsesTheCustomPolicy()
    {
        // The mirror of the test above, and the reason Custom exists: an administrator who has set the rules
        // deliberately does not want JIM changing them underneath because a target published something else.
        var configuration = new SyncRuleInitialPassword
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            CustomPolicy = new PasswordGenerationPolicy { Length = 12 }
        };

        // A discovered policy the custom settings comfortably satisfy, so this isolates which settings were used
        // rather than whether the target would accept the result.
        var result = _resolver.Resolve(configuration, new ConnectedSystemPasswordPolicy { MinimumLength = 8 });

        Assert.That(result.Password, Has.Length.EqualTo(12));
    }

    [Test]
    public void Resolve_UsableConfiguration_ReturnsAGeneratedPasswordSatisfyingThePolicy()
    {
        var configuration = new SyncRuleInitialPassword { Enabled = true, Source = InitialPasswordSource.Discovered };
        var discovered = new ConnectedSystemPasswordPolicy { MinimumLength = 16 };

        var result = _resolver.Resolve(configuration, discovered);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsUsable, Is.True);
            Assert.That(result.Password, Is.Not.Null.And.Not.Empty);
            Assert.That(result.Password, Has.Length.GreaterThanOrEqualTo(16));
            Assert.That(result.FailureReason, Is.Null);
            Assert.That(result.Message, Is.Null);
        }
    }
}
