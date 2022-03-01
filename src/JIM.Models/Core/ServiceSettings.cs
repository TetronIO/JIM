namespace JIM.Models.Core
{
    public class ServiceSettings
    {
        public Guid Id { get; set; }
        public DateTime Created { get; set; }
        public string? SSOAuthority { get; set; }
        public string? SSOClientId { get; set; }
        public string? SSOSecret { get; set; }
        public MetaverseAttribute? SSONameIDAttribute { get; set; }
        public bool SSOEnableLogOut { get; set; }
        /// <summary>
        /// When set to true, the JIM application is having maintenance performed on it and non-primary app instances
        /// are to stop processing requests or to not complete startup up if in initialisation phase.
        /// This is used to allow the primary app instance to perform database upgrades and seeding tasks ahead of the app
        /// being made ready for service.
        /// </summary>
        public bool IsServiceInMaintenanceMode { get; set; }

        public ServiceSettings()
        {
            Created = DateTime.Now;
            SSOEnableLogOut = true;
            IsServiceInMaintenanceMode = true;
        }
    }
}