// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Search;
using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// The Template Attribute editor's working state (#1466). It holds every field the dialog can show, whichever data
/// type is chosen, so switching attribute or value source loses nothing while the dialog is open; <see cref="ToEntity"/>
/// then writes only the rules that apply to the chosen attribute, so a stale field can never reach the server, and
/// <see cref="Validate"/> refuses, in the administrator's words, what the server would refuse.
/// </summary>
public class ExampleDataTemplateAttributeDraft
{
    public MetaverseAttribute? Attribute { get; set; }

    public int PopulatedValuesPercentage { get; set; } = 100;

    public ExampleDataTextSource TextSource { get; set; } = ExampleDataTextSource.DataSets;

    public string? Pattern { get; set; }

    public string? Expression { get; set; }

    /// <summary>The data sets a value is picked from, or that a pattern's placeholders read from, in order.</summary>
    public List<ExampleDataSet> DataSets { get; set; } = [];

    public List<ExampleDataWeightedValueDraft> WeightedValues { get; set; } = [];

    public int? MinNumber { get; set; }

    public int? MaxNumber { get; set; }

    public ExampleDataNumberOrder NumberOrder { get; set; } = ExampleDataNumberOrder.Sequential;

    public DateTime? MinDate { get; set; }

    public DateTime? MaxDate { get; set; }

    public List<MetaverseObjectType> ReferenceObjectTypes { get; set; } = [];

    public int? ManagerDepthPercentage { get; set; }

    /// <summary>The percentage of the objects given a Boolean value that are given true; null for true or false at random.</summary>
    public int? BoolTrueDistribution { get; set; }

    public int? MvaMin { get; set; }

    public int? MvaMax { get; set; }

    public bool HasCondition { get; set; }

    public MetaverseAttribute? ConditionAttribute { get; set; }

    public SearchComparisonType ConditionOperator { get; set; } = SearchComparisonType.Equals;

    public string? ConditionValue { get; set; }

    private AttributeDataType? Type => Attribute?.Type;

    private bool IsManager => Type == AttributeDataType.Reference && Attribute!.Name == Constants.BuiltInAttributes.Manager;

    private bool IsNumberType => Type is AttributeDataType.Number or AttributeDataType.LongNumber or AttributeDataType.Decimal;

    public bool ShowsTextSources => Type == AttributeDataType.Text;

    public bool ShowsNumberRange => IsNumberType || (ShowsTextSources && TextSource == ExampleDataTextSource.Numbers);

    public bool ShowsDateRange => Type == AttributeDataType.DateTime;

    public bool ShowsReferenceTargets => Type == AttributeDataType.Reference && !IsManager;

    public bool ShowsManagerDepth => IsManager;

    public bool ShowsBooleanDistribution => Type == AttributeDataType.Boolean;

    public bool ShowsReferenceCountRange => ShowsReferenceTargets && Attribute!.AttributePlurality == AttributePlurality.MultiValued;

    /// <summary>Manager depth decides how many Users get a manager, so the Manager attribute takes no population percentage.</summary>
    public bool ShowsPopulation => Attribute != null && !IsManager;

    /// <summary>Whether generation can produce values for the attribute at all; Binary values are never generated.</summary>
    public static bool IsGeneratable(MetaverseAttribute attribute) =>
        attribute.Type is not (AttributeDataType.Binary or AttributeDataType.NotSet);

    /// <summary>Reads an existing Template Attribute into the editor, choosing the Text value source generation would use.</summary>
    public static ExampleDataTemplateAttributeDraft FromEntity(ExampleDataTemplateAttribute entity)
    {
        var draft = new ExampleDataTemplateAttributeDraft
        {
            Attribute = entity.MetaverseAttribute,
            PopulatedValuesPercentage = entity.PopulatedValuesPercentage ?? 100,
            Pattern = entity.Pattern,
            Expression = entity.Expression,
            DataSets = entity.ExampleDataSetInstances.OrderBy(i => i.Order).Select(i => i.ExampleDataSet).ToList(),
            WeightedValues = entity.WeightedStringValues?.Select(v => new ExampleDataWeightedValueDraft { Value = v.Value, Weight = v.Weight }).ToList() ?? [],
            MinNumber = entity.MinNumber,
            MaxNumber = entity.MaxNumber,
            NumberOrder = entity.RandomNumbers == true ? ExampleDataNumberOrder.Random : ExampleDataNumberOrder.Sequential,
            MinDate = entity.MinDate,
            MaxDate = entity.MaxDate,
            ReferenceObjectTypes = entity.ReferenceMetaverseObjectTypes?.ToList() ?? [],
            ManagerDepthPercentage = entity.ManagerDepthPercentage,
            BoolTrueDistribution = entity.BoolTrueDistribution,
            MvaMin = entity.MvaRefMinAssignments,
            MvaMax = entity.MvaRefMaxAssignments
        };

        // the same order of precedence generation applies, so the editor shows the source that actually produces values.
        if (!string.IsNullOrEmpty(entity.Expression))
            draft.TextSource = ExampleDataTextSource.Expression;
        else if (!string.IsNullOrEmpty(entity.Pattern))
            draft.TextSource = ExampleDataTextSource.Pattern;
        else if (entity.ExampleDataSetInstances.Count > 0)
            draft.TextSource = ExampleDataTextSource.DataSets;
        else if (entity.WeightedStringValues is { Count: > 0 })
            draft.TextSource = ExampleDataTextSource.WeightedValues;
        else if (entity.IsUsingNumbers())
            draft.TextSource = ExampleDataTextSource.Numbers;

        if (entity.AttributeDependency is { } dependency)
        {
            draft.HasCondition = true;
            draft.ConditionAttribute = dependency.MetaverseAttribute;
            draft.ConditionOperator = dependency.ComparisonType;
            draft.ConditionValue = dependency.StringValue;
        }

        return draft;
    }

    /// <summary>
    /// Builds a new Template Attribute carrying only the rules that apply to the chosen attribute and value source.
    /// Assumes <see cref="Validate"/> found nothing.
    /// </summary>
    public ExampleDataTemplateAttribute ToEntity()
    {
        var entity = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = Attribute,
            PopulatedValuesPercentage = ShowsPopulation ? PopulatedValuesPercentage : null
        };

        if (ShowsTextSources)
        {
            switch (TextSource)
            {
                case ExampleDataTextSource.DataSets:
                    AddDataSets(entity);
                    break;
                case ExampleDataTextSource.Pattern:
                    entity.Pattern = Pattern?.Trim();
                    AddDataSets(entity);
                    break;
                case ExampleDataTextSource.WeightedValues:
                    entity.WeightedStringValues = WeightedValues
                        .Select(v => new ExampleDataTemplateAttributeWeightedValue { Value = v.Value, Weight = v.Weight })
                        .ToList();
                    break;
                case ExampleDataTextSource.Expression:
                    entity.Expression = Expression?.Trim();
                    break;
            }
        }

        if (ShowsNumberRange)
        {
            entity.MinNumber = MinNumber;
            entity.MaxNumber = MaxNumber;
            entity.SequentialNumbers = NumberOrder == ExampleDataNumberOrder.Sequential;
            entity.RandomNumbers = NumberOrder == ExampleDataNumberOrder.Random;
        }

        if (ShowsDateRange)
        {
            entity.MinDate = MinDate;
            entity.MaxDate = MaxDate;
        }

        if (ShowsReferenceTargets)
            entity.ReferenceMetaverseObjectTypes = ReferenceObjectTypes.ToList();

        if (ShowsReferenceCountRange)
        {
            entity.MvaRefMinAssignments = MvaMin;
            entity.MvaRefMaxAssignments = MvaMax;
        }

        if (ShowsManagerDepth)
            entity.ManagerDepthPercentage = ManagerDepthPercentage;

        if (ShowsBooleanDistribution)
            entity.BoolTrueDistribution = BoolTrueDistribution;

        if (HasCondition && ConditionAttribute != null)
        {
            entity.AttributeDependency = new ExampleDataTemplateAttributeDependency
            {
                MetaverseAttribute = ConditionAttribute,
                ComparisonType = ConditionOperator,
                // an operator that takes no value stores none, as the REST API does.
                StringValue = SearchComparisonOperators.TakesValue(ConditionOperator) ? ConditionValue ?? string.Empty : string.Empty
            };
        }

        return entity;
    }

    /// <summary>What stops the attribute being saved, one sentence each; empty when it can be saved.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Attribute == null)
        {
            errors.Add("Choose a Metaverse Attribute to generate values for.");
            return errors;
        }

        if (ShowsPopulation && PopulatedValuesPercentage is < 1 or > 100)
            errors.Add("The percentage of objects given a value must be between 1 and 100.");

        if (ShowsTextSources)
            ValidateTextSource(errors);

        if (ShowsNumberRange && MinNumber.HasValue && MaxNumber.HasValue && MinNumber.Value >= MaxNumber.Value)
            errors.Add("Maximum must be greater than Minimum.");

        if (ShowsDateRange && MinDate.HasValue && MaxDate.HasValue && MinDate.Value >= MaxDate.Value)
            errors.Add("The latest date must be after the earliest date.");

        if (ShowsReferenceTargets && ReferenceObjectTypes.Count == 0)
            errors.Add("Choose at least one Metaverse Object Type for the references to point at.");

        if (ShowsReferenceCountRange)
        {
            if (MvaMin is < 0)
                errors.Add("The fewest references cannot be negative.");
            if (MvaMin.HasValue && MvaMax.HasValue && MvaMin.Value >= MvaMax.Value)
                errors.Add("The most references must be more than the fewest.");
        }

        if (ShowsManagerDepth)
        {
            if (!ManagerDepthPercentage.HasValue)
                errors.Add("Set the manager depth: the percentage of Users who manage other Users.");
            else if (ManagerDepthPercentage.Value is < 1 or > 99)
                errors.Add("The manager depth must be between 1 and 99.");
        }

        if (ShowsBooleanDistribution && BoolTrueDistribution is < 0 or > 100)
            errors.Add("The percentage of objects given true must be between 0 and 100.");

        if (HasCondition)
            ValidateCondition(errors);

        return errors;
    }

    private void ValidateTextSource(List<string> errors)
    {
        switch (TextSource)
        {
            case ExampleDataTextSource.DataSets when DataSets.Count == 0:
                errors.Add("Choose at least one Example Data Set to pick values from.");
                break;
            case ExampleDataTextSource.Pattern when string.IsNullOrWhiteSpace(Pattern):
                errors.Add("Enter the pattern values are built from.");
                break;
            case ExampleDataTextSource.Expression when string.IsNullOrWhiteSpace(Expression):
                errors.Add("Enter the expression that builds each value.");
                break;
            case ExampleDataTextSource.WeightedValues when WeightedValues.Count == 0:
                errors.Add("Add at least one value to pick from.");
                break;
            case ExampleDataTextSource.WeightedValues:
                if (WeightedValues.Any(v => string.IsNullOrEmpty(v.Value)))
                    errors.Add("Every weighted value needs some text.");
                if (WeightedValues.Any(v => v.Weight <= 0))
                    errors.Add("Every weighted value needs a weight greater than 0.");
                break;
        }
    }

    private void ValidateCondition(List<string> errors)
    {
        if (ConditionAttribute == null)
        {
            errors.Add("Choose the attribute for the condition, or turn the condition off.");
            return;
        }

        // the same check generation runs before it starts, so a condition saved here can always be evaluated.
        var error = CriterionValidation.ValidateText(ConditionOperator, ConditionValue, ConditionAttribute.Type,
            CriterionContext.ExampleData, ConditionAttribute.Name, caseSensitive: true, out _);
        if (error != null)
            errors.Add(error);
    }

    private void AddDataSets(ExampleDataTemplateAttribute entity)
    {
        entity.ExampleDataSetInstances = DataSets
            .Select((dataSet, index) => new ExampleDataSetInstance { ExampleDataSet = dataSet, ExampleDataTemplateAttribute = entity, Order = index })
            .ToList();
    }
}
