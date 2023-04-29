namespace JIM.Models.Staging
{
    public class ConnectedSystemPaginationToken
    {
        public string Name { get; set; }
        public string? StringValue { get; set; }
        public byte[]? ByteValue { get; set; }

        public ConnectedSystemPaginationToken(string name, byte[] byteValue)
        {
            Name = name;
            ByteValue = byteValue;
        }

        public ConnectedSystemPaginationToken(string name, string stringValue)
        {
            Name = name;
            StringValue = stringValue;
        }
    }
}
