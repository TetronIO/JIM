using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
namespace JIM.Application;

public class JimApplication : IDisposable
{
    public IRepository Repository { get; }

    /// <summary>
    /// The credential protection service for encrypting/decrypting connector passwords.
    /// Set by the hosting application (JIM.Web) after construction.
    /// </summary>
    public ICredentialProtectionService? CredentialProtection { get; set; }

    /// <summary>
    /// Optional service-lifetime memory cache shared across JimApplication instances.
    /// Used by the Worker for CSO lookup indexing to eliminate N+1 import queries.
    /// Null when running in JIM.Web (which does not need CSO caching).
    /// </summary>
    public IMemoryCache? Cache { get; }

    private SeedingServer Seeding { get; }
    public ActivityServer Activities { get; }
    public CertificateServer Certificates { get; }
    public ChangeHistoryServer ChangeHistory { get; }
    public ConnectedSystemServer ConnectedSystems { get; }
    public DataGenerationServer DataGeneration { get; }
    public DriftDetectionService DriftDetection { get; }
    public ExportEvaluationServer ExportEvaluation { get; }
    public ExportExecutionServer ExportExecution { get; }
    public ScopingEvaluationServer ScopingEvaluation { get; }
    public FileSystemServer FileSystem { get; }
    public MetaverseServer Metaverse { get; }
    public ObjectMatchingServer ObjectMatching { get; }
    public SchedulerServer Scheduler { get; }
    public SearchServer Search { get; }
    public SecurityServer Security { get; }
    public ServiceSettingsServer ServiceSettings { get; }
    public TaskingServer Tasking { get; }

    public JimApplication(IRepository dataRepository, IMemoryCache? cache = null)
    {
        Activities = new ActivityServer(this);
        Certificates = new CertificateServer(this);
        ChangeHistory = new ChangeHistoryServer(this);
        ConnectedSystems = new ConnectedSystemServer(this);
        DataGeneration = new DataGenerationServer(this);
        DriftDetection = new DriftDetectionService(this);
        ExportEvaluation = new ExportEvaluationServer(this);
        ExportExecution = new ExportExecutionServer(this);
        ScopingEvaluation = new ScopingEvaluationServer();
        FileSystem = new FileSystemServer(this);
        Metaverse = new MetaverseServer(this);
        ObjectMatching = new ObjectMatchingServer(this);
        Repository = dataRepository;
        Cache = cache;
        Scheduler = new SchedulerServer(this);
        Search = new SearchServer(this);
        Security = new SecurityServer(this);
        Seeding = new SeedingServer(this);
        ServiceSettings = new ServiceSettingsServer(this);
        Tasking = new TaskingServer(this);
        Log.Verbose("The JIM Application has started.");
    }

    /// <summary>
    /// Ensures that JIM is fully deployed and seeded, i.e. database migrations have been performed
    /// and data needed to run the service has been created.
    /// Only the primary JIM application instance should run this task on startup. Secondary app instances
    /// must not run it, or conflicts are likely to occur.
    /// </summary>
    public async Task InitialiseDatabaseAsync()
    {
        await Repository.InitialiseDatabaseAsync();
        await Seeding.SeedAsync();
        await Seeding.SyncBuiltInConnectorDefinitionsAsync();
        await Seeding.SyncBuiltInAttributeRenderingHintsAsync();
        await Seeding.SyncServiceSettingsAsync();
        await Repository.InitialisationCompleteAsync();
    }

    /// <summary>
    /// Copies SSO information provided by Docker configuration to the database so the user can view it in the interface.
    /// </summary>
    public async Task InitialiseSsoAsync(
        string ssoAuthority,
        string ssoClientId,
        string ssoSecret,
        string uniqueIdentifierClaimType,
        string uniqueIdentifierMetaverseAttributeName)
    {
        Log.Debug($"InitialiseSSOAsync: uniqueIdentifierClaimType: {uniqueIdentifierClaimType}");

        var uniqueIdentifierMetaverseAttribute = await Repository.Metaverse.GetMetaverseAttributeAsync(uniqueIdentifierMetaverseAttributeName) ??
                                                 throw new Exception("Unsupported SSO unique identifier Metaverse Attribute Name. Please specify one that exists.");

        var serviceSettings = await ServiceSettings.GetServiceSettingsAsync() ??
                              throw new Exception("ServiceSettings do not exist. Application is not properly initialised. Are you sure the application is ready?");

        // SSO AUTHENTICATION PROPERTIES
        // The variables that enable authentication via SSO with our Identity Provider are mastered in the docker-compose configuration file.
        // We want to mirror these into the database via the ServiceSettings object to make life easy for administrators, so they don't have to access the hosting environment
        // to check the values and can do so through JIM Web.
        // We will need to update the database if the configuration file values change.
        if (string.IsNullOrEmpty(serviceSettings.SSOAuthority) || serviceSettings.SSOAuthority != ssoAuthority)
        {
            serviceSettings.SSOAuthority = ssoAuthority;
            await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
            Log.Information($"InitialiseSSOAsync: Updated ServiceSettings.SSOAuthority to: {ssoAuthority}");
        }

        if (string.IsNullOrEmpty(serviceSettings.SSOClientId) || serviceSettings.SSOClientId != ssoClientId)
        {
            serviceSettings.SSOClientId = ssoClientId;
            await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
            Log.Information($"InitialiseSSOAsync: Updated ServiceSettings.SSOClientId to: {ssoClientId}");
        }

        if (string.IsNullOrEmpty(serviceSettings.SSOSecret) || serviceSettings.SSOSecret != ssoSecret)
        {
            serviceSettings.SSOSecret = ssoSecret;
            await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);

            // don't print the secret to logs!
            Log.Information($"InitialiseSSOAsync: Updated ServiceSettings.SSOSecret");
        }

        // INBOUND CLAIM MAPPING:
        // We want to make it easy for IDP and JIM admin teams to enable SSO. We don't want them to have to add JIM-specific claims to the OIDC ID token if possible.
        // We want to allow an IDP admin team to set up the client for JIM using their standard integration approach, where possible.
        // This will provide the slickest integration experience.
        if (string.IsNullOrEmpty(serviceSettings.SSOUniqueIdentifierClaimType) || serviceSettings.SSOUniqueIdentifierClaimType != uniqueIdentifierClaimType)
        {
            serviceSettings.SSOUniqueIdentifierClaimType = uniqueIdentifierClaimType;
            Log.Information($"InitialiseSSOAsync: Updating ServiceSettings.SSOUniqueIdentifierClaimType to: {uniqueIdentifierClaimType}");
            await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
        }

        if (serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null || serviceSettings.SSOUniqueIdentifierMetaverseAttribute.Id != uniqueIdentifierMetaverseAttribute.Id)
        {
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute = uniqueIdentifierMetaverseAttribute;
            Log.Information($"InitialiseSSOAsync: Updating ServiceSettings.SSOUniqueIdentifierMetaverseAttribute to: {uniqueIdentifierMetaverseAttribute.Name}");
            await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
        }
    }

    /// <summary>
    /// Indicates to secondary JimApplication clients (i.e. not the master) if the application is ready to be used.
    /// Do not progress with client initialisation if JimApplication is not ready.
    /// </summary>
    public async Task<bool> IsApplicationReadyAsync()
    {
        Log.Verbose("JIM.Application: IsApplicationReadyAsync()");
        var serviceSettings = await ServiceSettings.GetServiceSettingsAsync();
        return serviceSettings is { IsServiceInMaintenanceMode: false };
    }

    #region IDisposable

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Dispose the repository to release database connections
            Repository.Dispose();
        }

        _disposed = true;
    }

    #endregion
}