// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// The comparison that decides whether saving a Synchronisation Rule releases the initial passwords parked
/// against it (#1221).
/// <para>
/// Getting this wrong is not a cosmetic matter in either direction. Too eager, and an unrelated edit sets
/// parked accounts retrying against settings the target has already refused, inflating an attempt count that is
/// meant to count distinct configurations. Too reluctant, and the administrator's fix never reaches the parked
/// work, which leaves those accounts stuck for ever: nothing else in JIM moves a record out of Parked.
/// </para>
/// </summary>
[TestFixture]
public class SyncRuleInitialPasswordComparisonTests
{
    private static SyncRuleInitialPassword Configuration() => new()
    {
        Enabled = true,
        Source = InitialPasswordSource.Discovered,
        ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
        EnableAccount = true,
        CustomPolicy = new PasswordGenerationPolicy()
    };

    [Test]
    public void WouldDeliverTheSameAs_WithIdenticalConfigurations_IsTrue()
    {
        Assert.That(SyncRuleInitialPassword.WouldDeliverTheSameAs(Configuration(), Configuration()), Is.True);
    }

    /// <summary>
    /// Both nulls means the rule did not set initial passwords before and still does not. There is nothing
    /// parked against it that a save could make deliverable.
    /// </summary>
    [Test]
    public void WouldDeliverTheSameAs_WithNoConfigurationOnEitherSide_IsTrue()
    {
        Assert.That(SyncRuleInitialPassword.WouldDeliverTheSameAs(null, null), Is.True);
    }

    /// <summary>
    /// Configuring initial passwords for the first time, or removing the configuration, changes the delivery as
    /// surely as editing it does.
    /// </summary>
    [Test]
    public void WouldDeliverTheSameAs_WhenOneSideHasNoConfiguration_IsFalse()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRuleInitialPassword.WouldDeliverTheSameAs(null, Configuration()), Is.False);
            Assert.That(SyncRuleInitialPassword.WouldDeliverTheSameAs(Configuration(), null), Is.False);
        }
    }

    /// <summary>
    /// The case the feature exists for: a target refused the generated password, so the administrator lengthens
    /// it. That has to count as a change, or their fix never reaches the parked accounts.
    /// </summary>
    [Test]
    public void WouldDeliverTheSameAs_WhenTheGeneratorSettingsDiffer_IsFalse()
    {
        var lengthened = Configuration();
        lengthened.CustomPolicy.Length += 4;

        Assert.That(SyncRuleInitialPassword.WouldDeliverTheSameAs(Configuration(), lengthened), Is.False);
    }

    /// <summary>
    /// Switching the rule off is a change of delivery even though nothing else moved.
    /// </summary>
    [Test]
    public void WouldDeliverTheSameAs_WhenOnlyTheEnabledFlagDiffers_IsFalse()
    {
        var disabled = Configuration();
        disabled.Enabled = false;

        Assert.That(SyncRuleInitialPassword.WouldDeliverTheSameAs(Configuration(), disabled), Is.False);
    }

    /// <summary>
    /// The custom settings are compared whichever Source is selected. An administrator can correct them while
    /// the rule is still on Discovered and switch over afterwards; comparing only the settings the current
    /// Source uses would see no change on the first save and lose the release.
    /// </summary>
    [Test]
    public void WouldDeliverTheSameAs_WhenCustomSettingsDifferButSourceIsDiscovered_IsFalse()
    {
        var edited = Configuration();
        edited.CustomPolicy.Style = PasswordGenerationStyle.Words;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Configuration().Source, Is.EqualTo(InitialPasswordSource.Discovered), "precondition");
            Assert.That(SyncRuleInitialPassword.WouldDeliverTheSameAs(Configuration(), edited), Is.False);
        }
    }

    /// <summary>
    /// The guard that keeps the comparison honest as the model grows.
    /// <para>
    /// A hand-written comparison falls behind the moment somebody adds a setting, and the failure is silent:
    /// the new setting is edited, nothing is released, and the parked accounts stay parked. Rather than trusting
    /// that nobody forgets, this walks both models and asserts that changing each property in turn is detected.
    /// A new property that the comparison ignores fails here, naming itself.
    /// </para>
    /// </summary>
    [Test]
    public void WouldDeliverTheSameAs_ForEverySettingOnTheModel_DetectsAChangeToIt()
    {
        var ignored = new HashSet<string>
        {
            // Identity and the link back to the owning rule; neither is a setting, and both are equal by
            // construction on the two sides of a comparison for the same rule.
            nameof(SyncRuleInitialPassword.Id),
            nameof(SyncRuleInitialPassword.SyncRule),
            nameof(SyncRuleInitialPassword.SyncRuleId),
            // The policy is compared property by property below rather than by reference.
            nameof(SyncRuleInitialPassword.CustomPolicy)
        };

        var undetected = new List<string>();

        foreach (var property in typeof(SyncRuleInitialPassword)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite && !ignored.Contains(p.Name)))
        {
            var mutated = Configuration();
            property.SetValue(mutated, Different(property.GetValue(mutated), property.PropertyType));

            if (SyncRuleInitialPassword.WouldDeliverTheSameAs(Configuration(), mutated))
                undetected.Add($"{nameof(SyncRuleInitialPassword)}.{property.Name}");
        }

        foreach (var property in typeof(PasswordGenerationPolicy)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite))
        {
            var mutated = Configuration();
            property.SetValue(mutated.CustomPolicy, Different(property.GetValue(mutated.CustomPolicy), property.PropertyType));

            if (SyncRuleInitialPassword.WouldDeliverTheSameAs(Configuration(), mutated))
                undetected.Add($"{nameof(PasswordGenerationPolicy)}.{property.Name}");
        }

        Assert.That(undetected, Is.Empty,
            "these settings change what password is delivered, or how, but WouldDeliverTheSameAs does not " +
            "notice them changing. Until it does, editing one of them will not release the accounts parked " +
            "against the Synchronisation Rule, and they will stay parked for ever: " + string.Join(", ", undetected));
    }

    /// <summary>
    /// The sibling guard for the snapshot the portal holds while an administrator edits: a setting the snapshot
    /// forgets to copy reads as changed the moment the editor opens, so the panel would promise to release the
    /// parked accounts on a save that changes nothing.
    /// <para>
    /// Every setting is first moved off its default, so a forgotten copy leaves that setting at its default on
    /// the snapshot and the comparison catches it. A snapshot of a default-valued configuration would prove
    /// nothing.
    /// </para>
    /// </summary>
    [Test]
    public void SnapshotDeliverySettings_WithEverySettingMovedOffItsDefault_MatchesWhatItCopied()
    {
        var configuration = Configuration();

        foreach (var property in typeof(SyncRuleInitialPassword)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite && p.Name is not (nameof(SyncRuleInitialPassword.Id)
                         or nameof(SyncRuleInitialPassword.SyncRule)
                         or nameof(SyncRuleInitialPassword.SyncRuleId)
                         or nameof(SyncRuleInitialPassword.CustomPolicy))))
        {
            property.SetValue(configuration, Different(property.GetValue(configuration), property.PropertyType));
        }

        foreach (var property in typeof(PasswordGenerationPolicy)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite))
        {
            property.SetValue(configuration.CustomPolicy, Different(property.GetValue(configuration.CustomPolicy), property.PropertyType));
        }

        Assert.That(SyncRuleInitialPassword.WouldDeliverTheSameAs(configuration, configuration.SnapshotDeliverySettings()), Is.True,
            "SnapshotDeliverySettings does not copy every setting WouldDeliverTheSameAs compares. The portal " +
            "holds the snapshot as 'what was saved', so a missed setting makes an untouched rule look edited and " +
            "promises a release that saving will not perform.");
    }

    /// <summary>
    /// The snapshot exists to be compared against later, never to be written back. Carrying the identity would
    /// make it look like a persistable row.
    /// </summary>
    [Test]
    public void SnapshotDeliverySettings_DoesNotCarryTheIdentityOfWhatItCopied()
    {
        var configuration = Configuration();
        configuration.Id = 12;
        configuration.SyncRuleId = 34;

        var snapshot = configuration.SnapshotDeliverySettings();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Id, Is.Zero);
            Assert.That(snapshot.SyncRuleId, Is.Zero);
            Assert.That(snapshot.CustomPolicy, Is.Not.SameAs(configuration.CustomPolicy),
                "a shared policy instance would track the live edits it is supposed to be compared against");
        }
    }

    /// <summary>
    /// Produces a value of the right type that differs from the one supplied, so the guard above can mutate a
    /// property without knowing anything about it.
    /// </summary>
    private static object Different(object? current, System.Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(bool))
            return !(bool)(current ?? false);

        if (underlying == typeof(int))
            return (int)(current ?? 0) + 1;

        if (underlying == typeof(string))
            return (string?)current == "jim-different" ? "jim-other" : "jim-different";

        if (underlying.IsEnum)
        {
            var values = Enum.GetValues(underlying).Cast<object>().ToList();
            return values.First(v => !Equals(v, current));
        }

        throw new NotSupportedException(
            $"The completeness guard cannot vary a property of type '{underlying.Name}'. Teach Different() how " +
            "to change one, so the new setting is actually covered rather than silently skipped.");
    }
}
