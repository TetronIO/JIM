using System.Text.Json.Serialization;

namespace JIM.Models.DataGeneration
{
    /// <summary>
    /// Used by DataGenerationTemplateAttributes to reference ExampleDataSets in an ordered manner.
    /// </summary>
    public class ExampleDataSetInstance
    {
        public int Id { get; set; }

        [JsonIgnore]
        public DataGenerationTemplateAttribute DataGenerationTemplateAttribute { get; set; }

        public ExampleDataSet ExampleDataSet { get; set; }

        /// <summary>
        /// Used to set an order compared to other ExampleDataSetInstances so that they can be referenced reliably via numeric attribute pattern variables (i.e. "{0} {1}")
        /// </summary>
        public int Order { get; set; }
    }
}
