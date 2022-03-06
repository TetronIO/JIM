namespace JIM.Models.DataGeneration
{
    public class ExampleDataSet
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public bool BuiltIn { get; set; }
        public List<ExampleDataValue> Values { get; set; }

        public ExampleDataSet()
        {
            Created = DateTime.Now;
            Values = new List<ExampleDataValue>();
        }
    }
}
