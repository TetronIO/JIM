﻿using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
namespace JIM.Models.DataGeneration;

public class DataGenerationTemplateAttribute
{
    #region accessors
    public int Id { get; set; }

    public ConnectedSystemObjectTypeAttribute? ConnectedSystemObjectTypeAttribute { get; set; }

    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// How many values should be generated? 100% would mean every object has a value for this attribute.
    /// Not compatible with ManagerDepthPercentage.
    /// </summary>
    public int? PopulatedValuesPercentage { get; set; }

    /// <summary>
    /// Percentage of how many boolean values should be true
    /// </summary>
    public int? BoolTrueDistribution { get; set; }

    /// <summary>
    /// Should we randomly generate bool values?
    /// </summary>
    public bool? BoolShouldBeRandom { get; set; }

    public DateTime? MinDate { get; set; }

    public DateTime? MaxDate { get; set; }

    public int? MinNumber { get; set; }

    public int? MaxNumber { get; set; }

    public bool? SequentialNumbers { get; set; }

    public bool? RandomNumbers { get; set; }

    /// <summary>
    /// Use a variable replacement approach to constructing string values, i.e.
    /// "{Firstname}.{Lastname}[UniqueInt]@contoso.com" to construct an email address.
    /// Can also be used with ExampleDataSets to form their values in a specific way by using their index position, i.e. "{0} {1} (my extra info)"
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>
    /// The example data sets that can be used to populate a string value.
    /// Multiple example data sets can be supplied with no Pattern value and an even distribution will be used from both sets, i.e. male/female firstname data sets.
    /// One or more can be supplied with a Pattern value and index-based pattern variables can be used to say how the ExampleDataSets should be used, 
    /// i.e. "{0} {1}" means use a random value from the first ExampleDataSet, a space and then a random value from the second ExampleDataSet.
    /// </summary>
    public List<ExampleDataSetInstance> ExampleDataSetInstances { get; set; } = new();

    /// <summary>
    /// Instead of random selection from a dataset, you can specify specific string values to choose from, with weights to control, roughly, how many
    /// of each value should be selected, i.e. majority can be 'active', some can be 'retired'.
    /// </summary>
    public List<DataGenerationTemplateAttributeWeightedValue>? WeightedStringValues { get; set; }

    /// <summary>
    /// If you want Manager attributes to be assigned, specify how far into the organisational hierarchy managers should be present.
    /// i.e. if you want a heavy labour force, then you might specify 50%. If you want a heavily organised hierarchy then you might specify 95%.
    /// Leave as null if you don't want to assign Manager attribute values.
    /// </summary>
    public int? ManagerDepthPercentage { get; set; }

    /// <summary>
    /// When the Metaverse Attribute is a multi-valued reference attribute, this enables a minimum number of values to be assigned.
    /// i.e. must have more than x value assignments.
    /// </summary>
    public int? MvaRefMinAssignments { get; set; }

    /// <summary>
    /// When the Metaverse Attribute is a multi-valued reference attribute, this enables a maximum number of values to be assigned.
    /// i.e. must not have more than x value assignments.
    /// </summary>
    public int? MvaRefMaxAssignments { get; set; }

    /// <summary>
    /// When populating reference attributes, we need to specify what type of object we should use as the source.
    /// Note: does not apply to user Manager attributes, they are sourced automatically.
    /// </summary>
    public List<MetaverseObjectType>? ReferenceMetaverseObjectTypes { get; set; }

    /// <summary>
    /// If generation of this attribute depends on another, then specify it here.
    /// </summary>
    public DataGenerationTemplateAttributeDependency? AttributeDependency { get; set; }
    #endregion

    #region public methods
    public bool IsUsingNumbers()
    {
        return (SequentialNumbers.HasValue && SequentialNumbers.Value) || (RandomNumbers.HasValue && RandomNumbers.Value) || MinNumber.HasValue || MaxNumber.HasValue;
    }

    public bool IsUsingDates()
    {
        return MinDate.HasValue || MaxDate.HasValue;
    }

    public bool IsUsingStrings()
    {
        return !string.IsNullOrEmpty(Pattern) ||
               ExampleDataSetInstances.Count > 0 ||
               WeightedStringValues is { Count: > 0 };
    }

    public void Validate()
    {
        var usingPattern = !string.IsNullOrEmpty(Pattern);
        var usingExampleData = ExampleDataSetInstances.Count > 0;
        var usingMvaRefMinMaxAttributes = MvaRefMinAssignments.HasValue || MvaRefMaxAssignments.HasValue;
        var usingWeightedStringValues = WeightedStringValues is { Count: > 0 };

        // need either a cs or mv attribute reference
        if (ConnectedSystemObjectTypeAttribute == null && MetaverseAttribute == null)
            throw new DataGenerationTemplateAttributeException("ConnectedSystemAttribute and MetaverseAttribute are null");

        // needs to be within a 1-100 range
        if (PopulatedValuesPercentage < 1 || PopulatedValuesPercentage > 100)
            throw new DataGenerationTemplateAttributeException("PopulatedValuesPercentage is less than 1 or greater than 100");

        // check for invalid use of type-specific properties
        AttributeDataType attributeDataType;
        AttributePlurality attributePlurality;
        string attributeName;

        if (ConnectedSystemObjectTypeAttribute != null)
        {
            attributeDataType = ConnectedSystemObjectTypeAttribute.Type;
            attributePlurality = ConnectedSystemObjectTypeAttribute.AttributePlurality;
            attributeName = ConnectedSystemObjectTypeAttribute.Name;
        }
        else if (MetaverseAttribute != null)
        {
            attributeDataType = MetaverseAttribute.Type;
            attributePlurality = MetaverseAttribute.AttributePlurality;
            attributeName = MetaverseAttribute.Name;
        }
        else
            throw new DataGenerationTemplateAttributeException("Either a MetaverseAttribute OR a ConnectedSystemAttribute reference is required. None was present.");


        if (attributeDataType != AttributeDataType.Boolean && (BoolTrueDistribution != null || BoolShouldBeRandom != null))
            throw new DataGenerationTemplateAttributeException("Not Bool and BoolTrueDistribution is not null or BoolShouldBeRandom is not null");

        if (attributeDataType != AttributeDataType.DateTime && IsUsingDates())
            throw new DataGenerationTemplateAttributeException("Not DateTime and MinDate is not null or MaxDate is not null");

        if (attributeDataType != AttributeDataType.Text)
        {
            // pattern can only be used with string attributes
            if (usingPattern)
                throw new DataGenerationTemplateAttributeException("Not string but using pattern");

            // Example Data can only be used with string attributes
            if (usingExampleData)
                throw new DataGenerationTemplateAttributeException("Not string but using example data");
        }

        if (attributeDataType != AttributeDataType.Reference)
        {
            if (ReferenceMetaverseObjectTypes is { Count: > 0 })
                throw new DataGenerationTemplateAttributeException("ReferenceMetaverseObjectTypes can only be used with reference attribute data types");

            if (ManagerDepthPercentage.HasValue)
                throw new DataGenerationTemplateAttributeException("ManagerDepthPercentage can only be used with reference attribute data types");
        }            

        if (attributeDataType != AttributeDataType.Reference && usingMvaRefMinMaxAttributes)
            throw new DataGenerationTemplateAttributeException("MvaRefMinAssignments or MvaRefMaxAssignments can only be used with reference attribute data types");

        if (attributeDataType != AttributeDataType.Text && usingWeightedStringValues)
            throw new DataGenerationTemplateAttributeException("WeightedStringValues can only be used with text attribute data types");

        if (attributeDataType == AttributeDataType.Text && !usingPattern && !usingExampleData && !usingWeightedStringValues && !IsUsingNumbers())
            throw new DataGenerationTemplateAttributeException("String but not using pattern, example data, weighted string values or numbers");

        if (attributeDataType == AttributeDataType.Boolean)
        {
            if (IsUsingNumbers())
                throw new DataGenerationTemplateAttributeException("Bool but using number properties. This is not supported");

            if (usingExampleData)
                throw new DataGenerationTemplateAttributeException("Bool but using number example data. This is not supported");

            if (usingPattern)
                throw new DataGenerationTemplateAttributeException("Bool but using a pattern. This is not supported");
        }

        if (IsUsingNumbers())
        {
            if (MinNumber.HasValue && MaxNumber.HasValue && MinNumber.Value >= MaxNumber.Value)
                throw new DataGenerationTemplateAttributeException("Number and min number is equal or greater than max number");

            if (SequentialNumbers == true && RandomNumbers == true)
                throw new DataGenerationTemplateAttributeException("Number and sequential numbers and random numbers");
        }

        if (attributeDataType == AttributeDataType.DateTime)
        {
            if (MinDate != null && MaxDate != null)
            {
                if (MinDate >= MaxDate)
                    throw new DataGenerationTemplateAttributeException("DateTime and min date is equal or greater than max date");

                if (MaxDate <= MinDate)
                    throw new DataGenerationTemplateAttributeException("DateTime and max date is less than or equal to min date");
            }

            if (usingPattern || usingExampleData || IsUsingNumbers())
                throw new DataGenerationTemplateAttributeException("DateTime and non-DateTime properties used. This is not supported");
        }

        if (attributeDataType == AttributeDataType.Reference)
        {
            if (attributeName != Constants.BuiltInAttributes.Manager && (ReferenceMetaverseObjectTypes == null || ReferenceMetaverseObjectTypes.Count == 0))
                throw new DataGenerationTemplateAttributeException($"ReferenceMetaverseObjectTypes not populated. Attribute: {attributeName}");

            if (ManagerDepthPercentage.HasValue)
            {
                if (PopulatedValuesPercentage.HasValue)
                    throw new DataGenerationTemplateAttributeException("ManagerDepthPercentage cannot be used with PopulatedValuesPercentage. Ensure it's set to null");

                if (ManagerDepthPercentage is < 1 or > 99)
                    throw new DataGenerationTemplateAttributeException("ManagerDepthPercentage must be between 1-99(%)");
            }

            if (!usingMvaRefMinMaxAttributes) 
                return;
                
            if (attributePlurality != AttributePlurality.MultiValued)
                throw new DataGenerationTemplateAttributeException("MvaRefMinAssignments and MvaRefMaxAssignments can only be used on multi-valued attributes.");

            // min must be equal or greater than zero
            if (MvaRefMinAssignments is < 0)
                throw new DataGenerationTemplateAttributeException("MvaRefMinAssignments must be equal or more than 0");

            // min must be less than max
            if (MvaRefMinAssignments.HasValue && MvaRefMaxAssignments.HasValue && MvaRefMinAssignments.Value >= MvaRefMaxAssignments.Value)
                throw new DataGenerationTemplateAttributeException("MvaRefMinAssignments must be less than MvaRefMaxAssignments");
        }
    }
    #endregion
}