// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// The per-Connected-System Password Synchronisation configuration (#1119): the settings that decide whether a
/// password change reaches a system at all, and how hard JIM tries before giving up.
/// <para>
/// The retry arithmetic is tested here rather than left to the delivery processor because it is the one part of
/// the configuration that can produce a nonsensical answer from sensible-looking inputs: a delay past the queued
/// event's own time to live schedules a retry for an event that will have expired by then, and unbounded
/// doubling overflows.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemPasswordSynchronisationTests
{
    private static ConnectedSystemPasswordSynchronisation Configuration() => new()
    {
        Enabled = true,
        TargetObjectTypeId = 1,
        MaxRetries = 5,
        RetryBackoffBase = TimeSpan.FromMinutes(5),
        RequireSecureTransport = false
    };

    [Test]
    public void EffectiveMaxRetries_WhenZeroOrNegative_FallsBackToTheDefault()
    {
        // Zero would mean "deliver once and park", which is a state the queue can already reach by exhausting
        // retries; obeying it here would make an unconfigured row behave as though somebody had chosen that.
        var configuration = Configuration();
        configuration.MaxRetries = 0;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.EffectiveMaxRetries,
                Is.EqualTo(ConnectedSystemPasswordSynchronisation.DefaultMaxRetries));

            configuration.MaxRetries = -3;
            Assert.That(configuration.EffectiveMaxRetries,
                Is.EqualTo(ConnectedSystemPasswordSynchronisation.DefaultMaxRetries));
        }
    }

    [Test]
    public void EffectiveRetryBackoffBase_WhenZeroOrNegative_FallsBackToTheDefault()
    {
        // A zero base would retry continuously, hammering a system that is already refusing us.
        var configuration = Configuration();
        configuration.RetryBackoffBase = TimeSpan.Zero;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.EffectiveRetryBackoffBase,
                Is.EqualTo(ConnectedSystemPasswordSynchronisation.DefaultRetryBackoffBase));

            configuration.RetryBackoffBase = TimeSpan.FromMinutes(-1);
            Assert.That(configuration.EffectiveRetryBackoffBase,
                Is.EqualTo(ConnectedSystemPasswordSynchronisation.DefaultRetryBackoffBase));
        }
    }

    [Test]
    public void CalculateRetryDelay_DoublesWithEachAttempt()
    {
        var configuration = Configuration();
        var timeToLive = TimeSpan.FromDays(7);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.CalculateRetryDelay(1, timeToLive), Is.EqualTo(TimeSpan.FromMinutes(5)),
                "The first retry waits one base interval.");
            Assert.That(configuration.CalculateRetryDelay(2, timeToLive), Is.EqualTo(TimeSpan.FromMinutes(10)));
            Assert.That(configuration.CalculateRetryDelay(3, timeToLive), Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(configuration.CalculateRetryDelay(4, timeToLive), Is.EqualTo(TimeSpan.FromMinutes(40)));
        }
    }

    [Test]
    public void CalculateRetryDelay_WithAttemptCountBelowOne_IsTreatedAsTheFirstAttempt()
    {
        var configuration = Configuration();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.CalculateRetryDelay(0, TimeSpan.FromDays(7)), Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(configuration.CalculateRetryDelay(-1, TimeSpan.FromDays(7)), Is.EqualTo(TimeSpan.FromMinutes(5)));
        }
    }

    [Test]
    public void CalculateRetryDelay_IsCappedAtTheTimeToLive()
    {
        // Scheduling a retry beyond the event's own expiry would book an attempt against something that will
        // have been expired by the time it comes round.
        var configuration = Configuration();
        var timeToLive = TimeSpan.FromHours(1);

        Assert.That(configuration.CalculateRetryDelay(10, timeToLive), Is.EqualTo(timeToLive));
    }

    [Test]
    public void CalculateRetryDelay_WithAnExtremeAttemptCount_DoesNotOverflow()
    {
        // Doubling without a bound overflows; the cap has to be applied to the arithmetic, not just to the result.
        var configuration = Configuration();
        var timeToLive = TimeSpan.FromDays(7);

        Assert.That(configuration.CalculateRetryDelay(int.MaxValue, timeToLive), Is.EqualTo(timeToLive));
    }

    [Test]
    public void CalculateRetryDelay_WithANonPositiveTimeToLive_CapsAtJimsDefault()
    {
        // Mirrors ConnectedSystem.EffectiveInitialPasswordTimeToLive: a zero or negative window is unconfigured
        // rather than an instruction to expire everything immediately.
        var configuration = Configuration();

        Assert.That(configuration.CalculateRetryDelay(int.MaxValue, TimeSpan.Zero),
            Is.EqualTo(PendingInitialPassword.DefaultTimeToLive));
    }

    [Test]
    public void WouldDeliverTheSameAs_WithIdenticalConfigurations_IsTrue()
    {
        Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(Configuration(), Configuration()),
            Is.True);
    }

    [Test]
    public void WouldDeliverTheSameAs_WithBothUnconfigured_IsTrue()
    {
        Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(null, null), Is.True);
    }

    [Test]
    public void WouldDeliverTheSameAs_WhenOneSideIsUnconfigured_IsFalse()
    {
        // Configuring a system for the first time, or removing its configuration, changes delivery.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(Configuration(), null), Is.False);
            Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(null, Configuration()), Is.False);
        }
    }

    [Test]
    public void WouldDeliverTheSameAs_WhenADeliverySettingDiffers_IsFalse()
    {
        var enabled = Configuration();
        var disabled = Configuration();
        disabled.Enabled = false;

        var moreRetries = Configuration();
        moreRetries.MaxRetries = 10;

        var slowerBackoff = Configuration();
        slowerBackoff.RetryBackoffBase = TimeSpan.FromMinutes(30);

        var secureOnly = Configuration();
        secureOnly.RequireSecureTransport = true;

        var otherObjectType = Configuration();
        otherObjectType.TargetObjectTypeId = 2;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(enabled, disabled), Is.False);
            Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(enabled, moreRetries), Is.False);
            Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(enabled, slowerBackoff), Is.False);
            Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(enabled, secureOnly), Is.False);
            Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(enabled, otherObjectType), Is.False);
        }
    }

    [Test]
    public void SnapshotDeliverySettings_CopiesEverythingTheComparisonReads()
    {
        // The portal holds a snapshot of the saved configuration while the editor mutates the live instance, so
        // it can tell an administrator before they save whether saving will set parked work retrying.
        var configuration = Configuration();
        var snapshot = configuration.SnapshotDeliverySettings();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(configuration, snapshot), Is.True);
            Assert.That(snapshot.Id, Is.Zero, "A snapshot is a value to compare against, never something to persist.");
        }
    }

    /// <summary>
    /// A delivery setting added without being compared would silently stop releasing parked work when it changed,
    /// which is precisely the failure the comparison exists to prevent. Anything genuinely not part of delivery
    /// is listed below with the reason.
    /// </summary>
    [Test]
    public void WouldDeliverTheSameAs_AccountsForEveryDeliverySetting()
    {
        var notDeliverySettings = new HashSet<string>
        {
            // Identity and navigation: not settings at all.
            nameof(ConnectedSystemPasswordSynchronisation.Id),
            nameof(ConnectedSystemPasswordSynchronisation.ConnectedSystem),
            nameof(ConnectedSystemPasswordSynchronisation.ConnectedSystemId),
            nameof(ConnectedSystemPasswordSynchronisation.TargetObjectType),

            // Derived from the settings the comparison already reads.
            nameof(ConnectedSystemPasswordSynchronisation.EffectiveMaxRetries),
            nameof(ConnectedSystemPasswordSynchronisation.EffectiveRetryBackoffBase)
        };

        var undetected = new List<string>();

        foreach (var property in typeof(ConnectedSystemPasswordSynchronisation)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite && !notDeliverySettings.Contains(p.Name)))
        {
            var left = Configuration();
            var right = Configuration();
            MutateAwayFromDefault(right, property);

            if (ConnectedSystemPasswordSynchronisation.WouldDeliverTheSameAs(left, right))
                undetected.Add($"{nameof(ConnectedSystemPasswordSynchronisation)}.{property.Name}");
        }

        Assert.That(undetected, Is.Empty,
            "These settings change what is delivered but are not compared, so changing one would not release " +
            "parked work. Add them to WouldDeliverTheSameAs and SnapshotDeliverySettings, or list them as " +
            "deliberately excluded in this test with the reason.");
    }

    private static void MutateAwayFromDefault(ConnectedSystemPasswordSynchronisation target, PropertyInfo property)
    {
        var current = property.GetValue(target);

        object? mutated = current switch
        {
            bool value => !value,
            int value => value + 7,
            TimeSpan value => value + TimeSpan.FromMinutes(11),
            Enum value => Enum.GetValues(value.GetType()).Cast<object>().First(v => !v.Equals(value)),
            null => throw new NotSupportedException(
                $"{property.Name} has no default value to mutate; extend this test to cover its type."),
            _ => throw new NotSupportedException(
                $"{property.Name} is of an unhandled type ({property.PropertyType.Name}); extend this test.")
        };

        property.SetValue(target, mutated);
    }
}
