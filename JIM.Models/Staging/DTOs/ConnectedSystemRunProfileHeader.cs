namespace JIM.Models.Staging.DTOs;

public class ConnectedSystemRunProfileHeader
{
    public int Id { get; set; }

    public string ConnectedSystemName { get; set; } = null!;

    public string ConnectedSystemRunProfileName { get; set; } = null!;

    public override string ToString() => $"{ConnectedSystemName} : {ConnectedSystemRunProfileName}";
}