using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.DataGeneration
{
    public class DataGenerationTemplateAttribute
    {
        #region accessors
        public int Id { get; set; }
        
        public ConnectedSystemAttribute? ConnectedSystemAttribute { get; set; }
        
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
        /// The example data sets to use to populate the object value.
        /// Multiple can be supplied with no Pattern value and an even distribution will be used from both sets, i.e. male/female firstname data sets.
        /// One or more can be supplied with a Pattern value and index-based pattern variables can be used to say how the ExampleDataSets should be used, i.e. "{0} {1}" means use a random value from the first ExampleDataSet, a space and then a random value from the second ExampleDataSet.
        /// </summary>
        public List<ExampleDataSet> ExampleDataSets { get; set; }

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
        #endregion

        #region constructors
        public DataGenerationTemplateAttribute()
        {
            ExampleDataSets = new List<ExampleDataSet>();
        }
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
            return !string.IsNullOrEmpty(Pattern) || (ExampleDataSets != null && ExampleDataSets.Count > 0);
        }

        public bool IsValid()
        {
            var usingPattern = !string.IsNullOrEmpty(Pattern);
            var usingExampleData = ExampleDataSets != null && ExampleDataSets.Count > 0;
            var usingMvaRefMinMaxAttributes = MvaRefMinAssignments.HasValue || MvaRefMaxAssignments.HasValue;

            // need either a cs or mv attribute reference
            if (ConnectedSystemAttribute == null && MetaverseAttribute == null)
            {
                Log.Error("DataGenerationTemplateAttribute.IsValid: ConnectedSystemAttribute and MetaverseAttribute are null");
                return false;
            }

            // needs to be within a 1-100 range
            if (PopulatedValuesPercentage < 1 || PopulatedValuesPercentage > 100)
            {
                Log.Error("DataGenerationTemplateAttribute.IsValid: PopulatedValuesPercentage is less than 1 or greater than 100");
                return false;
            }

            // check for invalid use of type-specific properties
            AttributeDataType attributeDataType;
            AttributePlurality attributePlurality;
            string attributeName;

            if (ConnectedSystemAttribute != null)
            {
                attributeDataType = ConnectedSystemAttribute.Type;
                attributePlurality = ConnectedSystemAttribute.AttributePlurality;
                attributeName = ConnectedSystemAttribute.Name;
            }
            else if (MetaverseAttribute != null)
            {
                attributeDataType = MetaverseAttribute.Type;
                attributePlurality = MetaverseAttribute.AttributePlurality;
                attributeName = MetaverseAttribute.Name;
            }
            else
                throw new InvalidDataException("Either a MetaverseAttribute OR a ConnectedSystemAttribute reference is required. None was present.");


            if (attributeDataType != AttributeDataType.Bool && (BoolTrueDistribution != null || BoolShouldBeRandom != null))
            {
                Log.Error("DataGenerationTemplateAttribute.IsValid: Not Bool and BoolTrueDistribution is not null or BoolShouldBeRandom is not null");
                return false;
            }

            if (attributeDataType != AttributeDataType.DateTime && IsUsingDates())
            {
                Log.Error("DataGenerationTemplateAttribute.IsValid: Not DateTime and MinDate is not null or MaxDate is not null");
                return false;
            }

            if (attributeDataType != AttributeDataType.String)
            {
                // pattern can only be used with string attributes
                if (usingPattern)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: Not string but using pattern");
                    return false;
                }

                // Example Data can only be used with string attributes
                if (usingExampleData)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: Not string but using example data");
                    return false;
                }
            }

            if (attributeDataType != AttributeDataType.Reference)
            {
                if (ReferenceMetaverseObjectTypes != null && ReferenceMetaverseObjectTypes.Count > 0)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: ReferenceMetaverseObjectTypes can only be used with reference attribute data types");
                    return false;
                }

                if (ManagerDepthPercentage.HasValue)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: ManagerDepthPercentage can only be used with reference attribute data types");
                    return false;
                }
            }            

            if (attributeDataType != AttributeDataType.Reference && usingMvaRefMinMaxAttributes)
            {
                Log.Error("DataGenerationTemplateAttribute.IsValid: MvaRefMinAssignments or MvaRefMaxAssignments can only be used with reference attribute data types");
                return false;
            }

            if (attributeDataType == AttributeDataType.String && !usingPattern && !usingExampleData && !IsUsingNumbers())
            {
                Log.Error("DataGenerationTemplateAttribute.IsValid: String but not using pattern, example data or numbers");
                return false;
            }

            if (attributeDataType == AttributeDataType.Bool)
            {
                if (IsUsingNumbers())
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: Bool but using number properties. This is not supported");
                    return false;
                }

                if (usingExampleData)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: Bool but using number example data. This is not supported");
                    return false;
                }

                if (usingPattern)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: Bool but using a pattern. This is not supported");
                    return false;
                }
            }

            if (IsUsingNumbers())
            {
                if (MaxNumber <= MinNumber)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: Number and max number is less than or equal to min number");
                    return false;
                }

                if (MinNumber >= MaxNumber)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: Number and min number is equal or greater than max number");
                    return false;
                }

                if (SequentialNumbers == true && RandomNumbers == true)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: Number and sequential nubmers and random numbers");
                    return false;
                }
            }

            if (attributeDataType == AttributeDataType.DateTime)
            {
                if (MinDate != null && MaxDate != null)
                {
                    if (MinDate >= MaxDate)
                    {
                        Log.Error("DataGenerationTemplateAttribute.IsValid: DateTime and min date is equal or greater than max date");
                        return false;
                    }

                    if (MaxDate <= MinDate)
                    {
                        Log.Error("DataGenerationTemplateAttribute.IsValid: DateTime and max date is less than or equal to min date");
                        return false;
                    }
                }

                if (usingPattern || usingExampleData || IsUsingNumbers())
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: DateTime and non-DateTime properties used. This is not supported");
                    return false;
                }
            }

            if (attributeDataType == AttributeDataType.Reference)
            {
                if (attributeName != Constants.BuiltInAttributes.Manager && (ReferenceMetaverseObjectTypes == null || ReferenceMetaverseObjectTypes.Count == 0))
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: ReferenceMetaverseObjectTypes not populated");
                    return false;
                }

                if (ManagerDepthPercentage.HasValue)
                {
                    if (PopulatedValuesPercentage.HasValue)
                    {
                        Log.Error("DataGenerationTemplateAttribute.IsValid: ManagerDepthPercentage cannot be used with PopulatedValuesPercentage. Ensure it's set to null");
                        return false;
                    }

                    if (ManagerDepthPercentage < 1 || ManagerDepthPercentage > 99)
                    {
                        Log.Error("DataGenerationTemplateAttribute.IsValid: ManagerDepthPercentage must be between 1-99(%)");
                        return false;
                    }
                }

                if (usingMvaRefMinMaxAttributes)
                {
                    if (attributePlurality != AttributePlurality.MultiValued)
                    {
                        Log.Error("DataGenerationTemplateAttribute.IsValid: MvaRefMinAssignments and MvaRefMaxAssignments can only be used on multi-valued attributes.");
                        return false;
                    }

                    // min must be equal or greater than zero
                    if (MvaRefMinAssignments.HasValue && MvaRefMinAssignments < 0)
                    {
                        Log.Error("DataGenerationTemplateAttribute.IsValid: MvaRefMinAssignments must be equal or more than 0");
                        return false;
                    }

                    // min must be less than max
                    if (MvaRefMinAssignments.HasValue && MvaRefMaxAssignments.HasValue && MvaRefMinAssignments.Value >= MvaRefMaxAssignments.Value)
                    {
                        Log.Error("DataGenerationTemplateAttribute.IsValid: MvaRefMinAssignments must be less than MvaRefMaxAssignments");
                        return false;
                    }
                }
            }

            return true;
        }
        #endregion
    }
}
