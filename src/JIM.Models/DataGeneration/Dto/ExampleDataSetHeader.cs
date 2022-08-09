namespace JIM.Models.DataGeneration.Dto
{
    public class ExampleDataSetHeader
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool BuiltIn { get; set; }
        public DateTime Created { set; get; }
        public string Culture { get; set; }
        public int Values { get; set; }
    }
}
