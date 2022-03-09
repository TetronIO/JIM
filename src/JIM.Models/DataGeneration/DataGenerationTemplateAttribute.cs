using JIM.Models.Core;
using JIM.Models.Staging;

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
        /// "{Firstname}.{Lastname}[uniqueid]@contoso.com" to construct an email address.
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
            var usingExampleData = ExampleDataSets.Count > 0;

            // need either a cs or mv attribute reference
            if (ConnectedSystemAttribute == null && MetaverseAttribute == null)
                return false;

            // needs to be within a 1-100 range
            if (PopulatedValuesPercentage < 1 || PopulatedValuesPercentage > 100)
                return false;

            // check for invalid use of type-specific properties
            var attributeDataType = ConnectedSystemAttribute != null ? ConnectedSystemAttribute.Type : MetaverseAttribute.Type;
            if (attributeDataType != AttributeDataType.Bool)
            {
                if (BoolTrueDistribution != null || BoolShouldBeRandom != null)
                    return false;
            }

            if (attributeDataType != AttributeDataType.DateTime)
            {
                if (MinDate != null || MaxDate != null)
                    return false;
            }

            if (attributeDataType != AttributeDataType.Number)
            {
                if (MinNumber != null || MaxNumber != null || SequentialNumbers != null || RandomNumbers != null)
                    return false;
            }

            if (attributeDataType != AttributeDataType.String)
            {
                // pattern can only be used with string attributes
                if (usingPattern)
                    return false;

                // Example Data can only be used with string attributes
                if (usingExampleData)
                    return false;
            }
            
            if (attributeDataType == AttributeDataType.String)
            {
                // either example data or a pattern needs to be used to populate string attribute values, and not both
                if (usingPattern && usingExampleData)
                    return false;

                if (!usingPattern && !usingExampleData)
                    return false;
            }

            if (attributeDataType == AttributeDataType.Number)
            {
                if (MaxNumber <= MinNumber)
                    return false;

                if (MinNumber >= MaxNumber)
                    return false;

                if (SequentialNumbers == true && RandomNumbers == true)
                    return false;

                if (SequentialNumbers == true && MaxNumber.HasValue)
                    return false;
            }

            if (attributeDataType == AttributeDataType.DateTime)
            {
                if (MinDate != null && MaxDate != null)
                {
                    if (MinDate >= MaxDate)
                        return false;

                    if (MaxDate <= MinDate)
                        return false;
                }
            }

            return true;
        }
    }
}
