using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.DataGeneration
{
    public class DataGenerationTemplateAttribute
    {
        public int Id { get; set; }
        public ConnectedSystemAttribute? ConnectedSystemAttribute { get; set; }
        public MetaverseAttribute? MetaverseAttribute { get; set; }

        /// <summary>
        /// How many values should be generated? 100% would mean every object has a value for this attribute.
        /// </summary>
        public int PopulatedValuesPercentage { get; set; }

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
        /// </summary>
        public string? Pattern { get; set; }

        /// <summary>
        /// The example data sets to use to populate the object value.
        /// Multiple can be supplied and an even distribution will be used from both sets, i.e. male/female firstname data sets.
        /// Shouldn't be supplied if you're using a pattern.
        /// </summary>
        public List<ExampleDataSet> ExampleDataSets { get; set; }

        public DataGenerationTemplateAttribute()
        {
            ExampleDataSets = new List<ExampleDataSet>();
        }

        public bool IsValid()
        {
            var usingPattern = !string.IsNullOrEmpty(Pattern);
            var usingExampleData = ExampleDataSets != null && ExampleDataSets.Count > 0;
            var usingNumbers = (SequentialNumbers.HasValue && SequentialNumbers.Value) || (RandomNumbers.HasValue && RandomNumbers.Value) || MinNumber.HasValue || MaxNumber.HasValue;
            var usingDates = MinDate.HasValue || MaxDate.HasValue;

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
            if (ConnectedSystemAttribute != null)
                attributeDataType = ConnectedSystemAttribute.Type;
            else if (MetaverseAttribute != null)
                attributeDataType = MetaverseAttribute.Type;
            else
                throw new InvalidDataException("Either a MetaverseAttribute OR a ConnectedSystemAttribute reference is required. None was present.");


            if (attributeDataType != AttributeDataType.Bool && (BoolTrueDistribution != null || BoolShouldBeRandom != null))
            {
                Log.Error("DataGenerationTemplateAttribute.IsValid: Not Bool and BoolTrueDistribution is not null or BoolShouldBeRandom is not null");
                return false;
            }

            if (attributeDataType != AttributeDataType.DateTime && usingDates)
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

            if (attributeDataType == AttributeDataType.String)
            {
                // either example data or a pattern needs to be used to populate string attribute values, and not both
                if (usingPattern && usingExampleData)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: String but using pattern and example data");
                    return false;
                }

                if (!usingPattern && !usingExampleData && !usingNumbers)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: String but not using pattern, example data or numbers");
                    return false;
                }
            }

            if (attributeDataType == AttributeDataType.Bool)
            {
                if (usingNumbers)
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

            if (usingNumbers)
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

                if (usingPattern || usingExampleData || usingNumbers)
                {
                    Log.Error("DataGenerationTemplateAttribute.IsValid: DateTime and non-DateTime properties used. This is not supported");
                    return false;
                }
            }

            return true;
        }
    }
}
