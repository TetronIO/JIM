// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Globalization;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// Guards lossless Decimal change recording on <see cref="MetaverseObjectChange.AddAttributeValueChange"/>.
/// Unlike the LongNumber path (which narrows to int, tracked by #871), the Decimal path must record the
/// value exactly, with no narrowing or rounding of any kind.
/// </summary>
[TestFixture]
public class MetaverseObjectChangeTests
{
    private static MetaverseObjectAttributeValue CreateDecimalValue(decimal? value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = new MetaverseAttribute { Id = 1, Name = "Salary", Type = AttributeDataType.Decimal },
        AttributeId = 1,
        DecimalValue = value
    };

    private static readonly decimal[] ExactRecordingCases =
    [
        decimal.MaxValue,                          // 79228162514264337593543950335
        1.234567890123456789012345678m,            // 28 significant digits
        -0.0000000000000000000000000001m           // negative, maximum scale
    ];

    [Test]
    public void AddAttributeValueChange_DecimalValues_RecordsExactValueWithNoNarrowing(
        [ValueSource(nameof(ExactRecordingCases))] decimal input)
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(input);

        change.AddAttributeValueChange(value, ValueChangeType.Add);

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        Assert.That(recorded.DecimalValue, Is.EqualTo(input));
        Assert.That(recorded.ValueChangeType, Is.EqualTo(ValueChangeType.Add));
    }

    [Test]
    public void AddAttributeValueChange_DecimalScalePreserved_RecordsStoredScale()
    {
        // decimal equality is scale-insensitive (5.00m == 5m), so also assert the stored scale survives.
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(5.00m);

        change.AddAttributeValueChange(value, ValueChangeType.Add);

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        Assert.That(recorded.DecimalValue, Is.EqualTo(5.00m));
        Assert.That(recorded.DecimalValue!.Value.ToString(CultureInfo.InvariantCulture), Is.EqualTo("5.00"));
    }

    [Test]
    public void AddAttributeValueChange_DecimalAssertedNullMarker_RecordsNothing()
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(null);
        value.NullValue = true;

        change.AddAttributeValueChange(value, ValueChangeType.Remove);

        Assert.That(change.AttributeChanges, Is.Empty);
    }

    [Test]
    public void AddAttributeValueChange_DecimalNullHolderNotAssertedNull_ThrowsInvalidOperationException()
    {
        // A Decimal-typed row with no value that is not an asserted-null marker is corrupt; the
        // default arm must fail fast rather than record nothing.
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(null);

        Assert.That(
            () => change.AddAttributeValueChange(value, ValueChangeType.Add),
            Throws.InvalidOperationException);
    }

    [Test]
    public void MetaverseObjectChangeAttributeValue_DecimalConstructor_SetsAllProperties()
    {
        var attributeChange = new MetaverseObjectChangeAttribute { AttributeName = "Salary" };

        var value = new MetaverseObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, 123.45m);

        Assert.That(value.DecimalValue, Is.EqualTo(123.45m));
        Assert.That(value.MetaverseObjectChangeAttribute, Is.SameAs(attributeChange));
        Assert.That(value.ValueChangeType, Is.EqualTo(ValueChangeType.Add));
    }

    [Test]
    public void MetaverseObjectChangeAttributeValue_ToString_WithDecimalValue_RendersInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var attributeChange = new MetaverseObjectChangeAttribute { AttributeName = "Salary" };
            var value = new MetaverseObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, 123.45m);

            Assert.That(value.ToString(), Is.EqualTo("123.45"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// Contributor provenance (#1519): AddAttributeValueChange must copy the contributing Synchronisation
    /// Rule's id and name onto the recorded change, so change history is self-describing about which rule
    /// contributed the value even after the live value's own provenance has moved on.
    /// </summary>
    [Test]
    public void AddAttributeValueChange_ValueHasContributingSyncRuleLoaded_CopiesIdAndName()
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(42m);
        value.ContributedBySyncRuleId = 7;
        value.ContributedBySyncRule = new SyncRule { Id = 7, Name = "HR to AD - Users" };

        change.AddAttributeValueChange(value, ValueChangeType.Add);

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.ContributedBySyncRuleId, Is.EqualTo(7));
            Assert.That(recorded.ContributedBySyncRuleName, Is.EqualTo("HR to AD - Users"));
        }
    }

    /// <summary>
    /// When only the FK is set (the navigation not loaded, the normal shape for a projection or a
    /// no-tracking read), the id must still be copied, and the method must not trigger a lazy load of the
    /// navigation to get the name; the name is simply unavailable at this call and stays null.
    /// </summary>
    [Test]
    public void AddAttributeValueChange_ValueHasContributingSyncRuleIdOnly_CopiesIdAndLeavesNameNull()
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(42m);
        value.ContributedBySyncRuleId = 7;

        change.AddAttributeValueChange(value, ValueChangeType.Add);

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.ContributedBySyncRuleId, Is.EqualTo(7));
            Assert.That(recorded.ContributedBySyncRuleName, Is.Null);
        }
    }

    /// <summary>
    /// A value with no contributor at all (managed internally, not via a Synchronisation Rule) must record
    /// null for both fields rather than defaulting to zero or throwing.
    /// </summary>
    [Test]
    public void AddAttributeValueChange_ValueHasNoContributor_RecordsNullSyncRuleFields()
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(42m);

        change.AddAttributeValueChange(value, ValueChangeType.Add);

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.ContributedBySyncRuleId, Is.Null);
            Assert.That(recorded.ContributedBySyncRuleName, Is.Null);
        }
    }

    /// <summary>
    /// The defect this guards (observed live after #1519 landed): a Full Synchronisation creates a brand new
    /// <see cref="MetaverseObjectAttributeValue"/> with only <c>ContributedBySyncRuleId</c> set (the navigation
    /// is never loaded for a freshly-constructed value); without a resolver the name was silently lost. The
    /// resolver is the caller's way of supplying that name from whatever Synchronisation Rules it already has
    /// in memory, or a cached repository lookup, without this method triggering a lazy load itself.
    /// </summary>
    [Test]
    public void AddAttributeValueChange_ValueHasContributingSyncRuleIdOnlyWithResolver_ResolvesNameViaResolver()
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(42m);
        value.ContributedBySyncRuleId = 7;

        change.AddAttributeValueChange(value, ValueChangeType.Add, id => id == 7 ? "Resolved Rule" : null);

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.ContributedBySyncRuleId, Is.EqualTo(7));
            Assert.That(recorded.ContributedBySyncRuleName, Is.EqualTo("Resolved Rule"));
        }
    }

    /// <summary>
    /// The loaded navigation is always authoritative when present; the resolver exists only to cover the case
    /// where the navigation was never loaded, so it must not override a name that is already known.
    /// </summary>
    [Test]
    public void AddAttributeValueChange_ValueHasContributingSyncRuleLoadedWithResolver_PrefersLoadedNavigationOverResolver()
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(42m);
        value.ContributedBySyncRuleId = 7;
        value.ContributedBySyncRule = new SyncRule { Id = 7, Name = "Loaded Name" };

        change.AddAttributeValueChange(value, ValueChangeType.Add, _ => "Resolver Name");

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        Assert.That(recorded.ContributedBySyncRuleName, Is.EqualTo("Loaded Name"));
    }

    /// <summary>
    /// A resolver must only ever be consulted for a value that actually has a contributing Synchronisation
    /// Rule id; calling it regardless would be wasted work for every attribute value with no contributor.
    /// </summary>
    [Test]
    public void AddAttributeValueChange_ValueHasNoContributorWithResolver_ResolverNotInvoked()
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(42m);
        var resolverCalled = false;

        change.AddAttributeValueChange(value, ValueChangeType.Add, _ =>
        {
            resolverCalled = true;
            return "should not be used";
        });

        Assert.That(resolverCalled, Is.False);
    }
}
