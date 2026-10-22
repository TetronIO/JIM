// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Search;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Pins the portal's Template Attribute editor model (#1466): which generation rules apply to which data type, that a
/// saved attribute carries only the rules that apply (so changing the attribute or the value source can never leave a
/// stale rule behind for the server to reject), and that the editor refuses what the server would refuse before the
/// administrator presses Save.
/// </summary>
[TestFixture]
public class ExampleDataTemplateAttributeDraftTests
{
    private static readonly MetaverseAttribute DisplayName = new() { Id = 1, Name = "Display Name", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute EmployeeId = new() { Id = 2, Name = "Employee ID", Type = AttributeDataType.Number };
    private static readonly MetaverseAttribute StartDate = new() { Id = 3, Name = "Start Date", Type = AttributeDataType.DateTime };
    private static readonly MetaverseAttribute Active = new() { Id = 4, Name = "Active", Type = AttributeDataType.Boolean };
    private static readonly MetaverseAttribute Manager = new() { Id = 5, Name = Constants.BuiltInAttributes.Manager, Type = AttributeDataType.Reference };
    private static readonly MetaverseAttribute Members = new()
    {
        Id = 6, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };
    private static readonly MetaverseAttribute Sponsor = new() { Id = 7, Name = "Sponsor", Type = AttributeDataType.Reference };
    private static readonly MetaverseAttribute EmployeeType = new() { Id = 8, Name = "Employee Type", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute ObjectGuid = new() { Id = 9, Name = "Object GUID", Type = AttributeDataType.Guid };
    private static readonly MetaverseObjectType UserType = new() { Id = 1, Name = "User", PluralName = "Users" };
    private static readonly ExampleDataSet FirstNames = new() { Id = 21, Name = "Firstnames", Culture = "en-GB" };

    // -- which sections apply --------------------------------------------------------------------------------------

    [Test]
    public void Sections_EachDataType_ShowsOnlyItsOwnRules()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName }.ShowsTextSources, Is.True);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = EmployeeId }.ShowsTextSources, Is.False);

            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = EmployeeId }.ShowsNumberRange, Is.True);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName, TextSource = ExampleDataTextSource.Numbers }.ShowsNumberRange, Is.True);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName, TextSource = ExampleDataTextSource.Pattern }.ShowsNumberRange, Is.False);

            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = StartDate }.ShowsDateRange, Is.True);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName }.ShowsDateRange, Is.False);

            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Sponsor }.ShowsReferenceTargets, Is.True);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Manager }.ShowsReferenceTargets, Is.False,
                "Manager links Users to other Users by manager depth, not to chosen Object Types");
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Manager }.ShowsManagerDepth, Is.True);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Sponsor }.ShowsManagerDepth, Is.False);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Members }.ShowsReferenceCountRange, Is.True);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Sponsor }.ShowsReferenceCountRange, Is.False);

            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Manager }.ShowsPopulation, Is.False,
                "manager depth decides how many Users get a manager; the server refuses a population percentage beside it");
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Active }.ShowsPopulation, Is.True);
        }
    }

    [Test]
    public void IsGeneratable_BinaryAttribute_IsFalse()
    {
        // nothing generates Binary values, so the editor must not offer such an attribute at all.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExampleDataTemplateAttributeDraft.IsGeneratable(new MetaverseAttribute { Name = "Photo", Type = AttributeDataType.Binary }), Is.False);
            Assert.That(ExampleDataTemplateAttributeDraft.IsGeneratable(DisplayName), Is.True);
            Assert.That(ExampleDataTemplateAttributeDraft.IsGeneratable(ObjectGuid), Is.True);
        }
    }

    // -- reading an existing attribute -----------------------------------------------------------------------------

    [Test]
    public void FromEntity_TextAttributes_PickTheValueSourceGenerationUses()
    {
        var dataSetsOnly = Entity(DisplayName, a => a.ExampleDataSetInstances.Add(Instance(a, FirstNames)));
        var patternWithDataSets = Entity(DisplayName, a => { a.Pattern = "{Firstnames} [UniqueInt]"; a.ExampleDataSetInstances.Add(Instance(a, FirstNames)); });
        var weighted = Entity(DisplayName, a => a.WeightedStringValues = [new ExampleDataTemplateAttributeWeightedValue { Value = "Employee", Weight = 0.9f }]);
        var expression = Entity(DisplayName, a => a.Expression = "mv[\"First Name\"]");
        var numbers = Entity(DisplayName, a => { a.MinNumber = 1; a.MaxNumber = 9; a.RandomNumbers = true; });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExampleDataTemplateAttributeDraft.FromEntity(dataSetsOnly).TextSource, Is.EqualTo(ExampleDataTextSource.DataSets));
            var pattern = ExampleDataTemplateAttributeDraft.FromEntity(patternWithDataSets);
            Assert.That(pattern.TextSource, Is.EqualTo(ExampleDataTextSource.Pattern));
            Assert.That(pattern.DataSets, Is.EqualTo(new[] { FirstNames }), "a pattern keeps the data sets its placeholders read from");
            Assert.That(ExampleDataTemplateAttributeDraft.FromEntity(weighted).TextSource, Is.EqualTo(ExampleDataTextSource.WeightedValues));
            Assert.That(ExampleDataTemplateAttributeDraft.FromEntity(expression).TextSource, Is.EqualTo(ExampleDataTextSource.Expression));
            var numberDraft = ExampleDataTemplateAttributeDraft.FromEntity(numbers);
            Assert.That(numberDraft.TextSource, Is.EqualTo(ExampleDataTextSource.Numbers));
            Assert.That(numberDraft.NumberOrder, Is.EqualTo(ExampleDataNumberOrder.Random));
        }
    }

    [Test]
    public void FromEntity_RoundTrip_PreservesEveryRule()
    {
        var source = Entity(Members, a =>
        {
            a.PopulatedValuesPercentage = 80;
            a.ReferenceMetaverseObjectTypes = [UserType];
            a.MvaRefMinAssignments = 2;
            a.MvaRefMaxAssignments = 20;
            a.AttributeDependency = new ExampleDataTemplateAttributeDependency
            {
                MetaverseAttribute = EmployeeType, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee"
            };
        });

        var result = ExampleDataTemplateAttributeDraft.FromEntity(source).ToEntity();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseAttribute, Is.SameAs(Members));
            Assert.That(result.PopulatedValuesPercentage, Is.EqualTo(80));
            Assert.That(result.ReferenceMetaverseObjectTypes, Is.EqualTo(new[] { UserType }));
            Assert.That(result.MvaRefMinAssignments, Is.EqualTo(2));
            Assert.That(result.MvaRefMaxAssignments, Is.EqualTo(20));
            Assert.That(result.AttributeDependency!.MetaverseAttribute, Is.SameAs(EmployeeType));
            Assert.That(result.AttributeDependency!.ComparisonType, Is.EqualTo(SearchComparisonType.Equals));
            Assert.That(result.AttributeDependency!.StringValue, Is.EqualTo("Employee"));
        }
    }

    // -- writing only what applies ---------------------------------------------------------------------------------

    [Test]
    public void ToEntity_TextSourceSwitchedToPattern_DropsTheOtherSourcesRules()
    {
        var draft = new ExampleDataTemplateAttributeDraft
        {
            Attribute = DisplayName,
            TextSource = ExampleDataTextSource.Pattern,
            Pattern = "{Firstnames}.[UniqueInt]",
            Expression = "left over",
            MinNumber = 1,
            MaxNumber = 5,
            DataSets = [FirstNames],
            WeightedValues = [new ExampleDataWeightedValueDraft { Value = "x", Weight = 1 }]
        };

        var result = draft.ToEntity();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pattern, Is.EqualTo("{Firstnames}.[UniqueInt]"));
            Assert.That(result.ExampleDataSetInstances.Select(i => i.ExampleDataSet), Is.EqualTo(new[] { FirstNames }));
            Assert.That(result.ExampleDataSetInstances.Single().ExampleDataTemplateAttribute, Is.SameAs(result));
            Assert.That(result.Expression, Is.Null);
            Assert.That(result.MinNumber, Is.Null);
            Assert.That(result.MaxNumber, Is.Null);
            Assert.That(result.WeightedStringValues, Is.Null);
        }
        Assert.That(result.Validate, Throws.Nothing);
    }

    [Test]
    public void ToEntity_DataSetsSource_WritesDataSetsInOrderAndNoPattern()
    {
        var second = new ExampleDataSet { Id = 22, Name = "Firstnames (Female)", Culture = "en-GB" };
        var draft = new ExampleDataTemplateAttributeDraft
        {
            Attribute = DisplayName, TextSource = ExampleDataTextSource.DataSets, Pattern = "left over", DataSets = [FirstNames, second]
        };

        var result = draft.ToEntity();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pattern, Is.Null);
            Assert.That(result.ExampleDataSetInstances.Select(i => (i.ExampleDataSet, i.Order)), Is.EqualTo(new[] { (FirstNames, 0), (second, 1) }));
        }
    }

    [Test]
    public void ToEntity_NumberAttribute_WritesSequentialOrRandom()
    {
        var sequential = new ExampleDataTemplateAttributeDraft { Attribute = EmployeeId, MinNumber = 1000, NumberOrder = ExampleDataNumberOrder.Sequential }.ToEntity();
        var random = new ExampleDataTemplateAttributeDraft { Attribute = EmployeeId, MinNumber = 1, MaxNumber = 10, NumberOrder = ExampleDataNumberOrder.Random }.ToEntity();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sequential.SequentialNumbers, Is.True);
            Assert.That(sequential.RandomNumbers, Is.False);
            Assert.That(sequential.MinNumber, Is.EqualTo(1000));
            Assert.That(random.RandomNumbers, Is.True);
            Assert.That(random.SequentialNumbers, Is.False);
        }
        Assert.That(sequential.Validate, Throws.Nothing);
        Assert.That(random.Validate, Throws.Nothing);
    }

    [Test]
    public void ToEntity_AttributeChangedToAnotherType_CarriesNoRuleFromTheOldType()
    {
        // the administrator filled in a reference attribute, then picked a Boolean one instead.
        var draft = new ExampleDataTemplateAttributeDraft
        {
            Attribute = Members, ReferenceObjectTypes = [UserType], MvaMin = 1, MvaMax = 3, MinDate = DateTime.UtcNow
        };
        draft.Attribute = Active;

        var result = draft.ToEntity();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReferenceMetaverseObjectTypes, Is.Null);
            Assert.That(result.MvaRefMinAssignments, Is.Null);
            Assert.That(result.MvaRefMaxAssignments, Is.Null);
            Assert.That(result.MinDate, Is.Null);
        }
        Assert.That(result.Validate, Throws.Nothing);
    }

    [Test]
    public void ToEntity_Manager_WritesDepthAndNoPopulation()
    {
        var result = new ExampleDataTemplateAttributeDraft { Attribute = Manager, PopulatedValuesPercentage = 100, ManagerDepthPercentage = 80 }.ToEntity();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ManagerDepthPercentage, Is.EqualTo(80));
            Assert.That(result.PopulatedValuesPercentage, Is.Null);
        }
        Assert.That(result.Validate, Throws.Nothing);
    }

    [Test]
    public void ToEntity_ConditionOperatorTakingNoValue_StoresNoValue()
    {
        var result = new ExampleDataTemplateAttributeDraft
        {
            Attribute = Active,
            HasCondition = true,
            ConditionAttribute = EmployeeType,
            ConditionOperator = SearchComparisonType.HasValue,
            ConditionValue = "left over"
        }.ToEntity();

        Assert.That(result.AttributeDependency!.StringValue, Is.Empty);
    }

    [Test]
    public void ToEntity_ConditionTurnedOff_WritesNoDependency()
    {
        var result = new ExampleDataTemplateAttributeDraft
        {
            Attribute = Active, HasCondition = false, ConditionAttribute = EmployeeType, ConditionValue = "Employee"
        }.ToEntity();

        Assert.That(result.AttributeDependency, Is.Null);
    }

    // -- refusing what the server would refuse ---------------------------------------------------------------------

    [Test]
    public void Validate_CompleteDrafts_HaveNoErrors()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName, TextSource = ExampleDataTextSource.DataSets, DataSets = [FirstNames] }.Validate(), Is.Empty);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = EmployeeId, MinNumber = 1, MaxNumber = 2 }.Validate(), Is.Empty);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Active }.Validate(), Is.Empty);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = ObjectGuid }.Validate(), Is.Empty);
            Assert.That(new ExampleDataTemplateAttributeDraft { Attribute = Manager, ManagerDepthPercentage = 50 }.Validate(), Is.Empty);
        }
    }

    [TestCaseSource(nameof(InvalidDrafts))]
    public void Validate_IncompleteOrContradictoryDraft_ReportsAnError(ExampleDataTemplateAttributeDraft draft, string expectedFragment)
    {
        Assert.That(draft.Validate(), Has.Some.Contains(expectedFragment));
    }

    private static IEnumerable<TestCaseData> InvalidDrafts()
    {
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft(), "Choose a Metaverse Attribute").SetName("No attribute");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = Active, PopulatedValuesPercentage = 0 }, "between 1 and 100").SetName("Population 0");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName, TextSource = ExampleDataTextSource.DataSets }, "at least one Example Data Set").SetName("Data sets source, none chosen");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName, TextSource = ExampleDataTextSource.Pattern, Pattern = " " }, "pattern").SetName("Empty pattern");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName, TextSource = ExampleDataTextSource.Expression }, "expression").SetName("Empty expression");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = DisplayName, TextSource = ExampleDataTextSource.WeightedValues }, "at least one value").SetName("No weighted values");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft
        {
            Attribute = DisplayName, TextSource = ExampleDataTextSource.WeightedValues, WeightedValues = [new ExampleDataWeightedValueDraft { Value = "x", Weight = 0 }]
        }, "weight").SetName("Zero weight");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = EmployeeId, MinNumber = 5, MaxNumber = 5 }, "Maximum must be greater than Minimum").SetName("Number range empty");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft
        {
            Attribute = StartDate, MinDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), MaxDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }, "latest date must be after the earliest").SetName("Date range reversed");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = Sponsor }, "at least one Metaverse Object Type").SetName("Reference, no target");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = Manager }, "manager depth").SetName("Manager, no depth");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = Manager, ManagerDepthPercentage = 100 }, "between 1 and 99").SetName("Manager depth 100");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft
        {
            Attribute = Members, ReferenceObjectTypes = [UserType], MvaMin = 5, MvaMax = 5
        }, "most references must be more than the fewest").SetName("Reference count range empty");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft
        {
            Attribute = Members, ReferenceObjectTypes = [UserType], MvaMin = -1
        }, "fewest references cannot be negative").SetName("Negative reference count");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft { Attribute = Active, HasCondition = true }, "attribute for the condition").SetName("Condition, no attribute");
        yield return new TestCaseData(new ExampleDataTemplateAttributeDraft
        {
            Attribute = Active, HasCondition = true, ConditionAttribute = EmployeeId, ConditionOperator = SearchComparisonType.Equals, ConditionValue = "not a number"
        }, "").SetName("Condition value not of the attribute's type");
    }

    // -- helpers ---------------------------------------------------------------------------------------------------

    private static ExampleDataTemplateAttribute Entity(MetaverseAttribute attribute, Action<ExampleDataTemplateAttribute> configure)
    {
        var entity = new ExampleDataTemplateAttribute { MetaverseAttribute = attribute };
        configure(entity);
        return entity;
    }

    private static ExampleDataSetInstance Instance(ExampleDataTemplateAttribute attribute, ExampleDataSet dataSet) =>
        new() { ExampleDataTemplateAttribute = attribute, ExampleDataSet = dataSet, Order = attribute.ExampleDataSetInstances.Count };
}
