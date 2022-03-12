using System.Text.Json.Serialization;

namespace JIM.Models.DataGeneration
{
    public class ExampleDataSet
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public bool BuiltIn { get; set; }
        /// <summary>
        /// The .NET Culture, i.e. "en-GB" the example data set values are in.
        /// More info: https://www.venea.net/web/culture_code
        /// </summary>
        public string Culture { get; set; }
        public List<ExampleDataSetValue> Values { get; set; }

        [JsonIgnore]
        public List<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; }

        public ExampleDataSet()
        {
            Created = DateTime.Now;
            Values = new List<ExampleDataSetValue>();
        }
    }
}
