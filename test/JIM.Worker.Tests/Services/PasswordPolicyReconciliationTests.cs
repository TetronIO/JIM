// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Covers combining several Connected Systems' discovered password policies into one set of generator settings
/// (issue #1172), which is what setting the same password across a person's accounts requires.
/// <para>
/// Combining is not averaging, and the direction each constraint folds in is the whole of the behaviour: take
/// the longest length any system demands, and count only the character categories every system recognises.
/// Getting either backwards produces passwords the strictest system refuses on every account, which is
/// indistinguishable at the target from JIM being broken.
/// </para>
/// </summary>
[TestFixture]
public class PasswordPolicyReconciliationTests
{
    private IPasswordGeneratorService _generator = null!;

    [SetUp]
    public void Setup()
    {
        _generator = new PasswordGeneratorService();
    }

    private static PasswordPolicyForSystem System(string name, ConnectedSystemPasswordPolicy? policy) =>
        new() { ConnectedSystemName = name, Policy = policy };

    private static ConnectedSystemPasswordPolicy Policy(
        int? minimumLength = null,
        bool? complexityRequired = null,
        int? requiredClasses = null,
        PasswordCharacterClasses recognised = PasswordCharacterClasses.None,
        FineGrainedPolicySignal fineGrained = FineGrainedPolicySignal.Absent) =>
        new()
        {
            MinimumLength = minimumLength,
            ComplexityRequired = complexityRequired,
            RequiredCharacterClassCount = requiredClasses,
            RecognisedCharacterClasses = recognised,
            FineGrainedPolicySignal = fineGrained
        };

    #region length folds to the strictest

    /// <summary>
    /// The load-bearing direction. Taking anything but the longest produces passwords the strictest system
    /// refuses on every account it is asked to set one on.
    /// </summary>
    [Test]
    public void Reconcile_WithDifferentMinimumLengths_TakesTheLongest()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(minimumLength: 20)),
            System("Fabrikam HR", Policy(minimumLength: 8))
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reconciliation.Policy.Length, Is.GreaterThanOrEqualTo(20));
            Assert.That(reconciliation.Constraints, Has.Some.Contains("20 characters or more"));
        }
    }

    /// <summary>
    /// A target willing to accept eight characters is not a reason for JIM to generate eight; the default floor
    /// stands where no system demands more.
    /// </summary>
    [Test]
    public void Reconcile_WhenEverySystemIsLaxerThanTheDefault_KeepsTheDefaultLength()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(minimumLength: 8)),
            System("Fabrikam HR", Policy(minimumLength: 6))
        ]);

        Assert.That(reconciliation.Policy.Length, Is.EqualTo(new PasswordGenerationPolicy().Length));
    }

    #endregion

    #region categories fold to the intersection

    /// <summary>
    /// The direction that is easy to get backwards. A category only one system counts is worthless for
    /// satisfying another system's "at least N categories", so taking the union would promise a compliance the
    /// password does not have. Here Fabrikam counts only three categories, so the combination must too.
    /// </summary>
    [Test]
    public void Reconcile_WithDifferentRecognisedCategories_CountsOnlyThoseEverySystemRecognises()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(complexityRequired: true, requiredClasses: 3,
                recognised: PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                            PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol |
                            PasswordCharacterClasses.OtherUnicodeLetter)),
            System("Fabrikam HR", Policy(complexityRequired: true, requiredClasses: 3,
                recognised: PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                            PasswordCharacterClasses.Digit))
        ]);

        Assert.That(reconciliation.Constraints, Has.Some.Contains("3 of 3 character categories"));
    }

    [Test]
    public void Reconcile_WithDifferentRequiredCategoryCounts_TakesTheHighest()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(complexityRequired: true, requiredClasses: 2)),
            System("Fabrikam HR", Policy(complexityRequired: true, requiredClasses: 4))
        ]);

        Assert.That(reconciliation.Constraints, Has.Some.Contains("4 of"));
    }

    /// <summary>
    /// A system that did not say which categories it counts must not narrow the intersection to nothing.
    /// Reading a silence as a denial is the same mistake the preflight was built to avoid.
    /// </summary>
    [Test]
    public void Reconcile_WhenASystemDidNotSayWhichCategoriesItCounts_DoesNotNarrowTheOthers()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(complexityRequired: true, requiredClasses: 3,
                recognised: PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                            PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol)),
            System("Fabrikam HR", Policy(minimumLength: 10))
        ]);

        Assert.That(reconciliation.Constraints, Has.Some.Contains("3 of 4 character categories"));
    }

    #endregion

    #region systems JIM knows nothing about

    /// <summary>
    /// A system with no discovered policy is not a system that will accept anything. It is reported, so the
    /// dialog can say that a password cannot be checked against it in advance.
    /// </summary>
    [Test]
    public void Reconcile_WithASystemThatPublishedNoPolicy_ReportsItRatherThanIgnoringIt()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(minimumLength: 15)),
            System("Research LDAP", null)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reconciliation.SystemsWithNoDiscoveredPolicy, Is.EqualTo(new[] { "Research LDAP" }));
            Assert.That(reconciliation.Policy.Length, Is.GreaterThanOrEqualTo(15), "the systems that did publish still apply");
        }
    }

    /// <summary>
    /// A policy row that exists but reported nothing useful counts as unknown, not as unconstrained. The two
    /// lead an administrator to very different conclusions.
    /// <para>
    /// It is reported <b>once</b>, as unknown. An undiscovered policy row carries the default "could not
    /// determine" fine-grained signal, so folding it into the known set would additionally announce that the
    /// combination is a floor, saying the same thing about the same system twice in two different vocabularies.
    /// </para>
    /// </summary>
    [Test]
    public void Reconcile_WithAPolicyRowThatDiscoveredNothing_TreatsItAsUnknownAndSaysSoOnlyOnce()
    {
        var reconciliation = _generator.Reconcile([
            System("Research LDAP", new ConnectedSystemPasswordPolicy())
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reconciliation.SystemsWithNoDiscoveredPolicy, Is.EqualTo(new[] { "Research LDAP" }));
            Assert.That(reconciliation.Constraints, Is.Empty);
            Assert.That(reconciliation.MayBeStricterThanDiscovered, Is.False);
        }
    }

    [Test]
    public void Reconcile_WithNoSystemsAtAll_ReturnsUsableDefaults()
    {
        var reconciliation = _generator.Reconcile([]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reconciliation.IsUsable, Is.True);
            Assert.That(reconciliation.Constraints, Is.Empty);
            Assert.That(reconciliation.Policy.Length, Is.EqualTo(new PasswordGenerationPolicy().Length));
        }
    }

    #endregion

    #region the floor caveat

    /// <summary>
    /// One system that may hold a stricter policy for some accounts makes the whole combination a floor. The
    /// administrator needs to know that before concluding a rejection means JIM got the rules wrong.
    /// </summary>
    [Test]
    public void Reconcile_WhenOneSystemMayHoldStricterPolicies_ReportsTheCombinationAsAFloor()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(minimumLength: 15, fineGrained: FineGrainedPolicySignal.Present)),
            System("Fabrikam HR", Policy(minimumLength: 8))
        ]);

        Assert.That(reconciliation.MayBeStricterThanDiscovered, Is.True);
    }

    /// <summary>
    /// "Could not tell" is not "none". A directory withholds what a caller may not see by omitting it, so an
    /// empty answer from a system JIM lacks rights over must not read as a guarantee.
    /// </summary>
    [Test]
    public void Reconcile_WhenOneSystemCouldNotBeAsked_ReportsTheCombinationAsAFloor()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(minimumLength: 15, fineGrained: FineGrainedPolicySignal.CouldNotDetermine))
        ]);

        Assert.That(reconciliation.MayBeStricterThanDiscovered, Is.True);
    }

    [Test]
    public void Reconcile_WhenEverySystemProvedNoneExist_DoesNotWarn()
    {
        var reconciliation = _generator.Reconcile([
            System("Contoso AD", Policy(minimumLength: 15, fineGrained: FineGrainedPolicySignal.Absent)),
            System("Fabrikam HR", Policy(minimumLength: 8, fineGrained: FineGrainedPolicySignal.Absent))
        ]);

        Assert.That(reconciliation.MayBeStricterThanDiscovered, Is.False);
    }

    #endregion

    #region what comes out actually satisfies every system

    /// <summary>
    /// The end-to-end promise, checked rather than assumed: generate from the reconciled settings and confirm
    /// each system's own assessment of that configuration passes. This is what makes the combination worth
    /// anything, and it is cheap to state.
    /// </summary>
    [Test]
    public void Reconcile_ProducesSettingsEverySelectedSystemWouldAccept()
    {
        var systems = new[]
        {
            System("Contoso AD", Policy(minimumLength: 20, complexityRequired: true, requiredClasses: 3,
                recognised: PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                            PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol)),
            System("Fabrikam HR", Policy(minimumLength: 12, complexityRequired: true, requiredClasses: 4,
                recognised: PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                            PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol)),
            System("Research LDAP", Policy(minimumLength: 8))
        };

        var reconciliation = _generator.Reconcile(systems);

        Assert.That(reconciliation.IsUsable, Is.True, string.Join(" ", reconciliation.Conflicts));
        using (Assert.EnterMultipleScope())
        {
            foreach (var system in systems)
                Assert.That(_generator.Assess(reconciliation.Policy, system.Policy).IsUsable, Is.True,
                    $"{system.ConnectedSystemName} would refuse the reconciled configuration");

            Assert.That(_generator.Generate(reconciliation.Policy), Has.Length.GreaterThanOrEqualTo(20));
        }
    }

    #endregion
}
