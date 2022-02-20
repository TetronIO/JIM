namespace JIM.Models.Core
{
    public abstract class BaseAttribute
    {
        public Guid Id { get; set; }
        public DateTime Created { set; get; }
        public string Name { get; set; }
        public AttributeDataType Type { get; set; }
        public AttributePlurality AttributePlurality { get; set; }
        public bool BuiltIn { get; set; }

        public BaseAttribute()
        {
            Created = DateTime.Now;
        }
    }
}
