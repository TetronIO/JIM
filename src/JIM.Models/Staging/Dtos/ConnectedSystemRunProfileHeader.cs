namespace JIM.Models.Staging.Dtos
{
    public class ConnectedSystemRunProfileHeader
    {
        public int Id { get; set; }
        public string ConnectedSystemName { get; set; }
        public string ConnectedSystemRunProfileName { get; set; }

        public override string ToString() => $"{ConnectedSystemName} : {ConnectedSystemRunProfileName}";
    }
}
