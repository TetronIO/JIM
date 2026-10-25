// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

[TestFixture]
public class ObjectChangeSearchStateTests
{
    [Test]
    public void WriteTo_ThenFromQuery_RoundTripsEveryFilter()
    {
        var state = new ObjectChangeSearchState
        {
            From = new DateTime(2026, 10, 1),
            To = new DateTime(2026, 10, 23),
            ObjectTypeId = 4,
            MatchMode = AttributeConditionMatchMode.Any,
            Conditions =
            [
                new ObjectChangeConditionRow { AttributeId = 7, Operator = AttributeChangeOperator.ChangedTo, Value = "Disabled: pending" },
                new ObjectChangeConditionRow { AttributeId = 9, Operator = AttributeChangeOperator.AnyChange }
            ]
        };
        var query = HttpUtility.ParseQueryString(string.Empty);

        state.WriteTo(query);
        var read = ObjectChangeSearchState.FromQuery(HttpUtility.ParseQueryString(query.ToString()!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.From, Is.EqualTo(new DateTime(2026, 10, 1)));
            Assert.That(read.To, Is.EqualTo(new DateTime(2026, 10, 23)));
            Assert.That(read.ObjectTypeId, Is.EqualTo(4));
            Assert.That(read.MatchMode, Is.EqualTo(AttributeConditionMatchMode.Any));
            Assert.That(read.Conditions.Select(c => (c.AttributeId, c.Operator, c.Value)), Is.EqualTo(new (int?, AttributeChangeOperator, string?)[]
            {
                (7, AttributeChangeOperator.ChangedTo, "Disabled: pending"),
                (9, AttributeChangeOperator.AnyChange, null)
            }));
        }
    }

    [Test]
    public void WriteTo_Defaults_WritesNothingAndKeepsOtherParameters()
    {
        var query = HttpUtility.ParseQueryString("q=ana&row=40&type=3&c=1:to:x");

        new ObjectChangeSearchState().WriteTo(query);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(query.AllKeys, Is.EquivalentTo(new[] { "q", "row" }));
            Assert.That(query["q"], Is.EqualTo("ana"));
        }
    }

    [Test]
    public void WriteTo_ConditionWithNoAttributeChosen_IsNotWritten()
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        var state = new ObjectChangeSearchState { Conditions = [new ObjectChangeConditionRow()] };

        state.WriteTo(query);

        Assert.That(query["c"], Is.Null);
    }

    [TestCase("c=abc:to:x")]
    [TestCase("c=5:sideways:x")]
    [TestCase("c=5")]
    [TestCase("type=notanumber&from=yesterday&mode=sometimes")]
    public void FromQuery_MalformedValues_AreIgnoredRatherThanThrowing(string queryString)
    {
        var state = ObjectChangeSearchState.FromQuery(HttpUtility.ParseQueryString(queryString));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Conditions, Is.Empty);
            Assert.That(state.ObjectTypeId, Is.Null);
            Assert.That(state.From, Is.Null);
            Assert.That(state.MatchMode, Is.EqualTo(AttributeConditionMatchMode.All));
        }
    }

    [Test]
    public void ToCriteria_SendsOnlyConditionsWithAnAttributeAndTheSearchText()
    {
        var state = new ObjectChangeSearchState
        {
            ObjectTypeId = 2,
            Conditions =
            [
                new ObjectChangeConditionRow { AttributeId = 7, Operator = AttributeChangeOperator.ChangedFrom, Value = "Active" },
                new ObjectChangeConditionRow { Value = "half-finished row" }
            ]
        };

        var criteria = state.ToCriteria("  Ana ");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(criteria.ObjectTypeId, Is.EqualTo(2));
            Assert.That(criteria.DisplayNameSearch, Is.EqualTo("Ana"));
            Assert.That(criteria.AttributeConditions, Has.Count.EqualTo(1));
            Assert.That(criteria.AttributeConditions[0].AttributeId, Is.EqualTo(7));
            Assert.That(criteria.AttributeConditions[0].Operator, Is.EqualTo(AttributeChangeOperator.ChangedFrom));
            Assert.That(criteria.AttributeConditions[0].Value, Is.EqualTo("Active"));
        }
    }

    [Test]
    public void ToCriteria_DateRange_CoversTheWholeOfBothLocalDaysInUtc()
    {
        var state = new ObjectChangeSearchState { From = new DateTime(2026, 10, 1), To = new DateTime(2026, 10, 2) };

        var criteria = state.ToCriteria(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(criteria.FromDate, Is.EqualTo(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime()));
            Assert.That(criteria.ToDate, Is.EqualTo(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Local).AddTicks(-1).ToUniversalTime()));
            Assert.That(criteria.DisplayNameSearch, Is.Null);
        }
    }

    [Test]
    public void HasActiveFilters_IgnoresConditionsWithNoAttributeChosen()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ObjectChangeSearchState { Conditions = [new ObjectChangeConditionRow()] }.HasActiveFilters, Is.False);
            Assert.That(new ObjectChangeSearchState { Conditions = [new ObjectChangeConditionRow { AttributeId = 1 }] }.HasActiveFilters, Is.True);
            Assert.That(new ObjectChangeSearchState { ObjectTypeId = 1 }.HasActiveFilters, Is.True);
        }
    }

    [Test]
    public void Summarise_PutsMatchedAttributesFirstThenOrdersByName()
    {
        var result = new MetaverseObjectChangeSearchResult
        {
            MatchedAttributeIds = [3],
            Change = new MvoChangeHistoryDto
            {
                AttributeChanges =
                [
                    Attribute(1, "Department", AttributePlurality.SingleValued),
                    Attribute(2, "Display Name", AttributePlurality.SingleValued),
                    Attribute(3, "Status", AttributePlurality.SingleValued)
                ]
            }
        };

        var summaries = ObjectChangeSummary.Summarise(result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summaries.Select(s => s.AttributeName), Is.EqualTo(new[] { "Status", "Department", "Display Name" }));
            Assert.That(summaries.Select(s => s.Matched), Is.EqualTo(new[] { true, false, false }));
        }
    }

    [Test]
    public void Summarise_SplitsAddedAndRemovedValuesAndNamesReferences()
    {
        var managerId = Guid.NewGuid();
        var change = Attribute(1, "Manager", AttributePlurality.SingleValued);
        change.ValueChanges =
        [
            new MvoValueChangeDto { ValueChangeType = ValueChangeType.Remove, ReferenceValue = new MvoChangeReferenceDto { Id = Guid.NewGuid(), DisplayName = "Tom Ellery" } },
            new MvoValueChangeDto { ValueChangeType = ValueChangeType.Add, ReferenceValue = new MvoChangeReferenceDto { Id = managerId, DisplayName = null } }
        ];
        var result = new MetaverseObjectChangeSearchResult { Change = new MvoChangeHistoryDto { AttributeChanges = [change] } };

        var summary = ObjectChangeSummary.Summarise(result).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.RemovedValues, Is.EqualTo(new[] { "Tom Ellery" }));
            Assert.That(summary.AddedValues, Is.EqualTo(new[] { managerId.ToString() }));
            Assert.That(summary.SingleValued, Is.True);
        }
    }

    [Test]
    public void Summarise_DeletedChange_ShowsTheRemovedValuesAsTheObjectsFinalValues()
    {
        var accountName = Attribute(1, "Account Name", AttributePlurality.SingleValued);
        accountName.ValueChanges = [new MvoValueChangeDto { ValueChangeType = ValueChangeType.Remove, StringValue = "tina.adams" }];
        var result = new MetaverseObjectChangeSearchResult
        {
            Change = new MvoChangeHistoryDto { ChangeType = ObjectChangeType.Deleted, AttributeChanges = [accountName] }
        };

        var summary = ObjectChangeSummary.Summarise(result).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.FinalValues, Is.True, "a deletion records the object's last values, not values being cleared");
            Assert.That(summary.RemovedValues, Is.EqualTo(new[] { "tina.adams" }));
        }
    }

    [Test]
    public void Summarise_UpdatedChange_IsNotFinalValues()
    {
        var result = new MetaverseObjectChangeSearchResult
        {
            Change = new MvoChangeHistoryDto { ChangeType = ObjectChangeType.AttributeFlow, AttributeChanges = [Attribute(1, "Department", AttributePlurality.SingleValued)] }
        };

        Assert.That(ObjectChangeSummary.Summarise(result).Single().FinalValues, Is.False);
    }

    private static MvoAttributeChangeDto Attribute(int id, string name, AttributePlurality plurality) => new()
    {
        AttributeId = id,
        AttributeName = name,
        AttributePlurality = plurality,
        ValueChanges = new List<MvoValueChangeDto>()
    };
}
