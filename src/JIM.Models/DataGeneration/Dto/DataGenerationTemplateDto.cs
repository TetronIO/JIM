namespace JIM.Models.DataGeneration.Dto
{
    public class DataGenerationTemplateDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool BuiltIn { get; set; }
        public DateTime Created { set; get; }
    }
}
