namespace TIM.Models.Core
{
    public abstract class BaseAttributeValue
    {
        public Guid Id { get; set; }
        public BaseAttribute Attribute { get; set; }
        public string? StringValue { get; set; }
        public DateTime DateTimeValue { get; set; }
        public int IntValue { get; set; }
        public byte[] ByteValue { get; set; }

        protected BaseAttributeValue(BaseAttribute attribute)
        {
            Attribute = attribute;
            ByteValue = Array.Empty<byte>();
        }
    }
}
