namespace JIM.Models.DataGeneration
{
    public class DataGeneratorTemplate
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { set; get; }
        public List<DataGeneratorObjectType> ObjectTypes { get; set; }

        public DataGeneratorTemplate()
        {
            Created = DateTime.Now;
            ObjectTypes = new List<DataGeneratorObjectType>();
        }
    }
}
