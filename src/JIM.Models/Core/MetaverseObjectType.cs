namespace JIM.Models.Core
{
    public class MetaverseObjectType
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public List<MetaverseAttribute> Attributes { get; set; }

        public MetaverseObjectType()
        {
            Attributes = new List<MetaverseAttribute>();
        }
    }
}
