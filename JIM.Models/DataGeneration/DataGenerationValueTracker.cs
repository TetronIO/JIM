namespace JIM.Models.DataGeneration
{
    /// <summary>
    /// Helps to keep a track of generated values 
    /// </summary>
    public class DataGenerationValueTracker
    {
        public int ObjectTypeId { get; set; }

        public int AttributeId { get; set; }

        /// <summary>
        /// The non-unique string value for when a string needs to be made unique using a UniqueInt system variable, i.e.
        /// "joe.bloggs@demo.tetron.io"
        /// </summary>
        public string? StringValue { get; set; }

        /// <summary>
        /// The last integer assigned for either a sequential integer attribute, or a UniqueInt system variable.
        /// </summary>
        public int? LastIntAssigned { get; set; }
    }
}
