namespace JIM.Models.DataGeneration
{
    public class DataGenerationTemplate
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { set; get; }
        public List<DataGenerationObjectType> ObjectTypes { get; set; }

        public DataGenerationTemplate()
        {
            Created = DateTime.Now;
            ObjectTypes = new List<DataGenerationObjectType>();
        }
    }
}
