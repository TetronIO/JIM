namespace JIM.Models.DataGeneration.DTOs
{
    public class DataGenerationTemplateHeader
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public bool BuiltIn { get; set; }
        public DateTime Created { set; get; }
    }
}
