namespace JIM.Models.Core
{
    public class MetaverseObjectType
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public List<MetaverseAttribute> Attributes { get; set; }
        public bool BuiltIn { get; set; }

        public MetaverseObjectType()
        {
            Created = DateTime.Now;
            Attributes = new List<MetaverseAttribute>();
        }
    }
}
