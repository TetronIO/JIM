namespace JIM.Models.Core
{
    public class ServiceSettings
    {
        public Guid Id { get; set; }
        public string? SSOAuthority { get; set; }
        public string? SSOClientId { get; set; }
        public string? SSOSecret { get; set; }
        public MetaverseAttribute SSONameIDAttribute { get; set; }
        public bool SSOEnableLogOut { get; set; }

        public ServiceSettings()
        {
            SSOEnableLogOut = true;
        }
    }
}