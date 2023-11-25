namespace JIM.Models.Core
{
    public class ServiceSettings
    {
        /// <summary>
        /// Necessary for persisting the ServiceSettings record. other than that, irrelevant as there will only be one ServiceSettings record.
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// When the ServiceSettings object created
        /// </summary>
        public DateTime Created { get; set; }
        /// <summary>
        /// For display purposes only. Do not update. The value is pulled from the configuration file.
        /// </summary>
        public string? SSOAuthority { get; set; }
        /// <summary>
        /// For display purposes only. Do not update. The value is pulled from the configuration file.
        /// </summary>
        public string? SSOClientId { get; set; }
        /// <summary>
        /// For display purposes only. Do not update. The value is pulled from the configuration file.
        /// </summary>
        public string? SSOSecret { get; set; }
        /// <summary>
        /// The Claim Type to use from the ID token when uniquely identifying users. This will map to a Metaverse attribute and is used to map an IDP user to a JIM user for the purposes of authenticating a user with JIM.
        /// </summary>
        public string? SSOUniqueIdentifierClaimType { get; set; }
        /// <summary>
        /// The MetaverseAttribute that the SSOUniqueIdentifierClaimType will map to when mapping IDP users to JIM users when authenticating with JIM.
        /// </summary>
        public MetaverseAttribute? SSOUniqueIdentifierMetaverseAttribute { get; set; }
        /// <summary>
        /// Controls whether or not a log-out link should be shown to the user. 
        /// This is sometimes not desirable when people cannot actually log-out of their enterprise-managed computers, i.e. for AAD-joined devices.
        /// </summary>
        public bool SSOEnableLogOut { get; set; }
        /// <summary>
        /// When set to true, the JIM application is having maintenance performed on it and non-primary app instances
        /// are to stop processing requests or to not complete startup up if in initialisation phase.
        /// This is used to allow the primary app instance to perform database upgrades and seeding tasks ahead of the app
        /// being made ready for service.
        /// </summary>
        public bool IsServiceInMaintenanceMode { get; set; }
        /// <summary>
        /// Determines how long history will be retained for. By default this is 30 days.
        /// After this time, history entries older than this will automatically be deleted.
        /// Note: Longer periods negatively affect database size and system performance.
        /// </summary>
        public TimeSpan HistoryRetentionPeriod { get; set; }

        public ServiceSettings()
        {
            Created = DateTime.UtcNow;
            SSOEnableLogOut = true;
            IsServiceInMaintenanceMode = true;
            HistoryRetentionPeriod = TimeSpan.FromDays(30);
        }
    }
}