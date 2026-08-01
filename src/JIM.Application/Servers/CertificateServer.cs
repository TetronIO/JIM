// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Models.Activities;
using JIM.Models.Connectors;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Application.Utilities;
using JIM.Utilities;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Application.Servers;

/// <summary>
/// Provides services for managing trusted certificates in the JIM certificate store.
/// </summary>
public class CertificateServer : ICertificateProvider
{
    private JimApplication Application { get; }

    /// <summary>
    /// Opens the TLS handshake that looks at what a server presents. Settable so the trust path can be tested
    /// without standing up a TLS server.
    /// </summary>
    internal IServerCertificateReader ServerCertificateReader { get; set; } = new ServerCertificateReader();

    /// <summary>
    /// Creates the connector that knows where a Connected System connects. Settable for the same reason.
    /// </summary>
    internal IConnectorFactory ConnectorFactory { get; set; } = new ConnectorFactory();

    internal CertificateServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Gets all trusted certificates.
    /// </summary>
    public async Task<List<TrustedCertificate>> GetAllAsync()
    {
        return await Application.Repository.TrustedCertificates.GetAllAsync();
    }

    /// <summary>
    /// Gets all enabled trusted certificates.
    /// </summary>
    public async Task<List<TrustedCertificate>> GetEnabledAsync()
    {
        return await Application.Repository.TrustedCertificates.GetEnabledAsync();
    }

    /// <summary>
    /// Gets a trusted certificate by its ID.
    /// </summary>
    public async Task<TrustedCertificate?> GetByIdAsync(Guid id)
    {
        return await Application.Repository.TrustedCertificates.GetByIdAsync(id);
    }

    /// <summary>
    /// Adds a certificate from uploaded data (PEM or DER encoded).
    /// </summary>
    public async Task<TrustedCertificate> AddFromDataAsync(string name, byte[] certificateData, MetaverseObject? initiatedBy = null, string? notes = null, string? changeReason = null)
    {
        return await AddFromDataCoreAsync(name, certificateData, notes, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy),
            certificate => AuditHelper.SetCreated(certificate, initiatedBy));
    }

    /// <summary>
    /// Adds a certificate from uploaded data (PEM or DER encoded). API-key initiator overload.
    /// </summary>
    public async Task<TrustedCertificate> AddFromDataAsync(string name, byte[] certificateData, ApiKey initiatedByApiKey, string? notes = null, string? changeReason = null)
    {
        return await AddFromDataCoreAsync(name, certificateData, notes, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            certificate => AuditHelper.SetCreated(certificate, initiatedByApiKey));
    }

    private async Task<TrustedCertificate> AddFromDataCoreAsync(string name, byte[] certificateData, string? notes, string? changeReason,
        Func<Activity, Task> createActivityAsync, Action<TrustedCertificate> setCreated)
    {
        var activity = new Activity
        {
            TargetName = name,
            TargetType = ActivityTargetType.TrustedCertificate,
            TargetOperationType = ActivityTargetOperationType.Create,
            Message = "Adding trusted certificate from uploaded data"
        };
        await createActivityAsync(activity);

        try
        {
            var x509Cert = ParseCertificate(certificateData);
            var thumbprint = x509Cert.Thumbprint;

            if (await Application.Repository.TrustedCertificates.ExistsByThumbprintAsync(thumbprint))
                throw new InvalidOperationException($"A certificate with thumbprint {thumbprint} already exists in the store.");

            var certificate = new TrustedCertificate
            {
                Id = Guid.NewGuid(),
                Name = name,
                Thumbprint = thumbprint,
                Subject = x509Cert.Subject,
                Issuer = x509Cert.Issuer,
                SerialNumber = x509Cert.SerialNumber,
                ValidFrom = x509Cert.NotBefore.ToUniversalTime(),
                ValidTo = x509Cert.NotAfter.ToUniversalTime(),
                SourceType = CertificateSourceType.Uploaded,
                CertificateData = certificateData,
                FilePath = null,
                IsEnabled = true,
                Notes = notes
            };

            setCreated(certificate);
            Log.Information("Adding trusted certificate '{Name}' (Thumbprint: {Thumbprint}) from uploaded data", name, thumbprint);
            var result = await Application.Repository.TrustedCertificates.CreateAsync(certificate);

            await CaptureConfigurationChangeAsync(activity, result.Id, changeReason);
            activity.Message = $"Added trusted certificate '{name}' (Subject: {x509Cert.Subject})";
            await Application.Activities.CompleteActivityAsync(activity);

            return result;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Adds a certificate from a file path in the connector-files mount.
    /// </summary>
    public async Task<TrustedCertificate> AddFromFilePathAsync(string name, string filePath, MetaverseObject? initiatedBy = null, string? notes = null, string? changeReason = null)
    {
        return await AddFromFilePathCoreAsync(name, filePath, notes, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy),
            certificate => AuditHelper.SetCreated(certificate, initiatedBy));
    }

    /// <summary>
    /// Adds a certificate from a file path in the connector-files mount. API-key initiator overload.
    /// </summary>
    public async Task<TrustedCertificate> AddFromFilePathAsync(string name, string filePath, ApiKey initiatedByApiKey, string? notes = null, string? changeReason = null)
    {
        return await AddFromFilePathCoreAsync(name, filePath, notes, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            certificate => AuditHelper.SetCreated(certificate, initiatedByApiKey));
    }

    private async Task<TrustedCertificate> AddFromFilePathCoreAsync(string name, string filePath, string? notes, string? changeReason,
        Func<Activity, Task> createActivityAsync, Action<TrustedCertificate> setCreated)
    {
        var activity = new Activity
        {
            TargetName = name,
            TargetType = ActivityTargetType.TrustedCertificate,
            TargetOperationType = ActivityTargetOperationType.Create,
            Message = $"Adding trusted certificate from file path: {filePath}"
        };
        await createActivityAsync(activity);

        try
        {
            // Validate the file path exists and load the certificate
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Certificate file not found: {filePath}");

            var certificateData = await File.ReadAllBytesAsync(filePath);
            var x509Cert = ParseCertificate(certificateData);
            var thumbprint = x509Cert.Thumbprint;

            if (await Application.Repository.TrustedCertificates.ExistsByThumbprintAsync(thumbprint))
                throw new InvalidOperationException($"A certificate with thumbprint {thumbprint} already exists in the store.");

            var certificate = new TrustedCertificate
            {
                Id = Guid.NewGuid(),
                Name = name,
                Thumbprint = thumbprint,
                Subject = x509Cert.Subject,
                Issuer = x509Cert.Issuer,
                SerialNumber = x509Cert.SerialNumber,
                ValidFrom = x509Cert.NotBefore.ToUniversalTime(),
                ValidTo = x509Cert.NotAfter.ToUniversalTime(),
                SourceType = CertificateSourceType.FilePath,
                CertificateData = null,
                FilePath = filePath,
                IsEnabled = true,
                Notes = notes
            };

            setCreated(certificate);
            Log.Information("Adding trusted certificate '{Name}' (Thumbprint: {Thumbprint}) from file path: {FilePath}", name, thumbprint, filePath);
            var result = await Application.Repository.TrustedCertificates.CreateAsync(certificate);

            await CaptureConfigurationChangeAsync(activity, result.Id, changeReason);
            activity.Message = $"Added trusted certificate '{name}' from file (Subject: {x509Cert.Subject})";
            await Application.Activities.CompleteActivityAsync(activity);

            return result;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Updates a trusted certificate's editable properties (name, notes, enabled state).
    /// </summary>
    public async Task UpdateAsync(Guid id, MetaverseObject? initiatedBy = null, string? name = null, string? notes = null, bool? isEnabled = null, string? changeReason = null)
    {
        await UpdateCoreAsync(id, name, notes, isEnabled, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy),
            certificate => AuditHelper.SetUpdated(certificate, initiatedBy));
    }

    /// <summary>
    /// Updates a trusted certificate's editable properties (name, notes, enabled state). API-key initiator overload.
    /// </summary>
    public async Task UpdateAsync(Guid id, ApiKey initiatedByApiKey, string? name = null, string? notes = null, bool? isEnabled = null, string? changeReason = null)
    {
        await UpdateCoreAsync(id, name, notes, isEnabled, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey),
            certificate => AuditHelper.SetUpdated(certificate, initiatedByApiKey));
    }

    private async Task UpdateCoreAsync(Guid id, string? name, string? notes, bool? isEnabled, string? changeReason,
        Func<Activity, Task> createActivityAsync, Action<TrustedCertificate> setUpdated)
    {
        var certificate = await Application.Repository.TrustedCertificates.GetByIdAsync(id)
            ?? throw new InvalidOperationException($"Certificate with ID {id} not found.");

        var activity = new Activity
        {
            TargetName = certificate.Name,
            TargetType = ActivityTargetType.TrustedCertificate,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Updating trusted certificate '{certificate.Name}'"
        };
        await createActivityAsync(activity);

        try
        {
            var changes = new List<string>();

            if (name != null && name != certificate.Name)
            {
                changes.Add($"Name: '{certificate.Name}' → '{name}'");
                certificate.Name = name;
            }
            if (notes != null && notes != certificate.Notes)
            {
                changes.Add("Notes updated");
                certificate.Notes = notes;
            }
            if (isEnabled.HasValue && isEnabled.Value != certificate.IsEnabled)
            {
                changes.Add($"Enabled: {certificate.IsEnabled} → {isEnabled.Value}");
                certificate.IsEnabled = isEnabled.Value;
            }

            setUpdated(certificate);
            Log.Information("Updating trusted certificate '{Name}' (ID: {Id})", certificate.Name, id);
            await Application.Repository.TrustedCertificates.UpdateAsync(certificate);

            await CaptureConfigurationChangeAsync(activity, id, changeReason);
            activity.Message = changes.Count > 0
                ? $"Updated trusted certificate: {string.Join(", ", changes)}"
                : "No changes made to trusted certificate";
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Deletes a trusted certificate from the store.
    /// </summary>
    public async Task DeleteAsync(Guid id, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        await DeleteCoreAsync(id, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Deletes a trusted certificate from the store. API-key initiator overload.
    /// </summary>
    public async Task DeleteAsync(Guid id, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        await DeleteCoreAsync(id, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task DeleteCoreAsync(Guid id, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var certificate = await Application.Repository.TrustedCertificates.GetByIdAsync(id);
        var certificateName = certificate?.Name ?? $"Unknown (ID: {id})";

        var activity = new Activity
        {
            TargetName = certificateName,
            TargetType = ActivityTargetType.TrustedCertificate,
            TargetOperationType = ActivityTargetOperationType.Delete,
            Message = $"Deleting trusted certificate '{certificateName}'"
        };
        await createActivityAsync(activity);

        try
        {
            if (certificate != null)
            {
                Log.Information("Deleting trusted certificate '{Name}' (ID: {Id})", certificate.Name, id);
                await CaptureConfigurationDeletionAsync(activity, certificate, changeReason);
            }

            await Application.Repository.TrustedCertificates.DeleteAsync(id);

            activity.Message = $"Deleted trusted certificate '{certificateName}'";
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Validates a certificate and returns any issues found.
    /// </summary>
    public async Task<CertificateValidationResult> ValidateAsync(Guid id)
    {
        var certificate = await Application.Repository.TrustedCertificates.GetByIdAsync(id)
            ?? throw new InvalidOperationException($"Certificate with ID {id} not found.");

        var result = new CertificateValidationResult();

        // Check expiry
        if (certificate.IsExpired)
        {
            result.Errors.Add($"Certificate expired on {certificate.ValidTo:yyyy-MM-dd}");
        }
        else if (certificate.IsExpiringSoon)
        {
            result.Warnings.Add($"Certificate will expire in {certificate.DaysUntilExpiry} days ({certificate.ValidTo:yyyy-MM-dd})");
        }

        // Check not yet valid
        if (DateTime.UtcNow < certificate.ValidFrom)
        {
            result.Errors.Add($"Certificate is not yet valid (valid from {certificate.ValidFrom:yyyy-MM-dd})");
        }

        // For file path certificates, verify the file still exists
        if (certificate.SourceType == CertificateSourceType.FilePath)
        {
            if (string.IsNullOrEmpty(certificate.FilePath))
            {
                result.Errors.Add("File path is not set for file-based certificate");
            }
            else if (!File.Exists(certificate.FilePath))
            {
                result.Errors.Add($"Certificate file not found: {certificate.FilePath}");
            }
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    #region Trusting the certificate a Connected System's server presents

    /// <summary>
    /// Asks the server a Connected System connects to what certificate it presents, and stores nothing.
    /// </summary>
    /// <remarks>
    /// Reading and trusting are deliberately separate: this is what an administrator looks at, and trusting is a
    /// decision they then make explicitly, naming the thumbprint they were shown.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System whose endpoint is read.</param>
    /// <param name="draftSettingValues">Connectivity settings entered but not yet saved, applied over the saved ones. Supplied when an administrator is configuring a system whose settings cannot be saved yet, which is the usual case: a certificate JIM does not trust is a validation failure, and JIM does not save settings that fail validation.</param>
    public async Task<ServerCertificateReadResult> ReadServerCertificateAsync(int connectedSystemId, IReadOnlyCollection<ConnectedSystemSettingValueDraft>? draftSettingValues = null)
    {
        var (_, endpoint, outcome, message) = await ResolveSecureEndpointAsync(connectedSystemId, draftSettingValues);
        if (endpoint == null)
            return new ServerCertificateReadResult { Outcome = outcome, Message = message };

        var reading = await ReadEndpointAsync(endpoint);
        if (reading == null)
        {
            return new ServerCertificateReadResult
            {
                Outcome = ServerCertificateReadOutcome.ServerUnreachable,
                Message = $"{endpoint.Host}:{endpoint.Port} could not be reached, so JIM could not read its certificate. This is a connectivity problem rather than a certificate one."
            };
        }

        return new ServerCertificateReadResult
        {
            Outcome = ServerCertificateReadOutcome.Read,
            Diagnostic = reading.Diagnostic,
            ReadAt = reading.Chain?.ReadAt ?? DateTime.UtcNow
        };
    }

    /// <summary>
    /// Whether this Connected System's connector makes encrypted connections at all, and so whether looking at a
    /// server certificate is a thing that can be offered for it.
    /// </summary>
    /// <remarks>
    /// A property of the connector, not of the settings, so it is stable for the life of a Connected System and can
    /// be asked once. Whether the settings currently describe an encrypted connection is a separate question, and one
    /// the read itself answers.
    /// </remarks>
    public async Task<bool> SupportsServerCertificateReadAsync(int connectedSystemId)
    {
        var connectedSystem = await Application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        var connectorName = connectedSystem?.ConnectorDefinition?.Name;
        if (string.IsNullOrEmpty(connectorName))
            return false;

        IConnector connector;
        try
        {
            connector = ConnectorFactory.Create(connectorName);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        using var connectorDisposable = connector as IDisposable;
        return connector is IConnectorSecureEndpoint;
    }

    /// <summary>
    /// Adds the certificate a Connected System's server is presenting to the JIM certificate store, having first
    /// confirmed it is still the one the administrator was shown.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System whose endpoint is read.</param>
    /// <param name="expectedThumbprint">The thumbprint the administrator confirmed. Matched against the certificate the server presents now and against the authority that issued it; whichever matches is what gets trusted.</param>
    /// <param name="initiatedBy">Who is trusting it, recorded on the Activity.</param>
    /// <param name="changeReason">Why, recorded on the Activity. A sentence naming the Connected System is used when none is given.</param>
    /// <param name="draftSettingValues">Connectivity settings entered but not yet saved, applied over the saved ones.</param>
    public async Task<ServerCertificateTrustResult> TrustServerCertificateAsync(int connectedSystemId, string expectedThumbprint, MetaverseObject? initiatedBy = null, string? changeReason = null, IReadOnlyCollection<ConnectedSystemSettingValueDraft>? draftSettingValues = null)
    {
        return await TrustServerCertificateCoreAsync(connectedSystemId, expectedThumbprint, changeReason, draftSettingValues,
            (name, data, notes, reason) => AddFromDataAsync(name, data, initiatedBy, notes, reason));
    }

    /// <summary>
    /// Adds the certificate a Connected System's server is presenting to the JIM certificate store. API-key
    /// initiator overload.
    /// </summary>
    /// <inheritdoc cref="TrustServerCertificateAsync(int, string, MetaverseObject?, string?, IReadOnlyCollection{ConnectedSystemSettingValueDraft})" path="/param"/>
    public async Task<ServerCertificateTrustResult> TrustServerCertificateAsync(int connectedSystemId, string expectedThumbprint, ApiKey initiatedByApiKey, string? changeReason = null, IReadOnlyCollection<ConnectedSystemSettingValueDraft>? draftSettingValues = null)
    {
        return await TrustServerCertificateCoreAsync(connectedSystemId, expectedThumbprint, changeReason, draftSettingValues,
            (name, data, notes, reason) => AddFromDataAsync(name, data, initiatedByApiKey, notes, reason));
    }

    private async Task<ServerCertificateTrustResult> TrustServerCertificateCoreAsync(
        int connectedSystemId,
        string expectedThumbprint,
        string? changeReason,
        IReadOnlyCollection<ConnectedSystemSettingValueDraft>? draftSettingValues,
        Func<string, byte[], string?, string?, Task<TrustedCertificate>> addAsync)
    {
        var expected = NormaliseThumbprint(expectedThumbprint);
        if (string.IsNullOrEmpty(expected))
        {
            return new ServerCertificateTrustResult
            {
                Outcome = ServerCertificateTrustOutcome.ThumbprintMismatch,
                Message = "A thumbprint is required. JIM will not trust whatever a server happens to be presenting."
            };
        }

        var (connectedSystem, endpoint, readOutcome, message) = await ResolveSecureEndpointAsync(connectedSystemId, draftSettingValues);
        if (endpoint == null || connectedSystem == null)
            return new ServerCertificateTrustResult { Outcome = ToTrustOutcome(readOutcome), Message = message };

        var reading = await ReadEndpointAsync(endpoint);
        if (reading?.Chain == null)
        {
            return new ServerCertificateTrustResult
            {
                Outcome = ServerCertificateTrustOutcome.ServerUnreachable,
                ExpectedThumbprint = expected,
                Message = reading == null
                    ? $"{endpoint.Host}:{endpoint.Port} could not be reached, so nothing was trusted."
                    : $"{endpoint.Host}:{endpoint.Port} offered no certificate, so there is nothing to trust."
            };
        }

        // The thumbprint both selects and verifies: the administrator chose either the server's own certificate or
        // the authority that issued it, and JIM adds whichever the confirmed thumbprint identifies. Anything else
        // means the server is presenting something other than what was shown, and nothing is added.
        var chain = reading.Chain;
        var chosen = MatchesThumbprint(chain.Leaf, expected) ? chain.Leaf
            : chain.Issuer != null && MatchesThumbprint(chain.Issuer, expected) ? chain.Issuer
            : null;

        if (chosen == null)
        {
            Log.Warning("Refused to trust a certificate for Connected System {ConnectedSystemId}: {Host}:{Port} is presenting {Presented}, not the confirmed {Expected}",
                connectedSystemId, LogSanitiser.Sanitise(endpoint.Host), endpoint.Port, LogSanitiser.Sanitise(chain.Leaf.Thumbprint), LogSanitiser.Sanitise(expected));

            return new ServerCertificateTrustResult
            {
                Outcome = ServerCertificateTrustOutcome.ThumbprintMismatch,
                ExpectedThumbprint = expected,
                PresentedThumbprint = chain.Leaf.Thumbprint,
                Message = $"{endpoint.Host} is presenting a different certificate from the one you confirmed, so nothing has been trusted. This is expected if the certificate was renewed; investigate if it was not."
            };
        }

        if (await Application.Repository.TrustedCertificates.ExistsByThumbprintAsync(chosen.Thumbprint))
        {
            return new ServerCertificateTrustResult
            {
                Outcome = ServerCertificateTrustOutcome.AlreadyTrusted,
                ExpectedThumbprint = expected,
                PresentedThumbprint = chosen.Thumbprint,
                Message = $"'{chosen.CommonName}' is already in the JIM certificate store."
            };
        }

        var notes = $"Trusted from the '{connectedSystem.Name}' Connected System, which {endpoint.Host}:{endpoint.Port} presented it to.";
        var reason = string.IsNullOrWhiteSpace(changeReason)
            ? $"Trusted the certificate presented by {endpoint.Host}:{endpoint.Port} for the '{connectedSystem.Name}' Connected System."
            : changeReason;

        var certificate = await addAsync(chosen.CommonName, chosen.Data, notes, reason);

        return new ServerCertificateTrustResult
        {
            Outcome = ServerCertificateTrustOutcome.Trusted,
            Certificate = certificate,
            ExpectedThumbprint = expected,
            PresentedThumbprint = chosen.Thumbprint
        };
    }

    /// <summary>
    /// Works out which server a Connected System makes its encrypted connection to, by asking that system's own
    /// connector.
    /// </summary>
    /// <remarks>
    /// The endpoint is never named directly by the caller: it is always derived by the connector from a Connected
    /// System's settings, saved or draft. Draft settings exist because JIM refuses to save settings that fail
    /// validation, and a certificate JIM does not trust is a validation failure, so an administrator configuring a
    /// new system has the address on screen and nothing in the database. Accepting them is not a widening: saving a
    /// Connected System already opens a connection to whatever address its settings name, so the same role can
    /// already make JIM connect anywhere.
    /// </remarks>
    private async Task<(ConnectedSystem? System, SecureEndpoint? Endpoint, ServerCertificateReadOutcome Outcome, string? Message)> ResolveSecureEndpointAsync(
        int connectedSystemId,
        IReadOnlyCollection<ConnectedSystemSettingValueDraft>? draftSettingValues)
    {
        // Loaded without change tracking, so applying the drafts below cannot reach the database.
        var connectedSystem = await Application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return (null, null, ServerCertificateReadOutcome.ConnectedSystemNotFound, $"Connected System {connectedSystemId} was not found.");

        if (draftSettingValues is { Count: > 0 })
            ApplyDraftSettingValues(connectedSystem, draftSettingValues);

        var connectorName = connectedSystem.ConnectorDefinition?.Name;
        if (string.IsNullOrEmpty(connectorName))
            return (connectedSystem, null, ServerCertificateReadOutcome.NotConfiguredForSecureConnection, $"The '{connectedSystem.Name}' Connected System has no Connector Definition, so JIM cannot tell where it connects.");

        IConnector connector;
        try
        {
            connector = ConnectorFactory.Create(connectorName);
        }
        catch (NotSupportedException ex)
        {
            Log.Warning(ex, "Could not create the {ConnectorName} connector to resolve where Connected System {ConnectedSystemId} connects", LogSanitiser.Sanitise(connectorName), connectedSystemId);
            return (connectedSystem, null, ServerCertificateReadOutcome.NotConfiguredForSecureConnection, $"The '{connectorName}' connector is not available, so JIM cannot tell where this Connected System connects.");
        }

        using var connectorDisposable = connector as IDisposable;

        if (connector is not IConnectorSecureEndpoint secureEndpointConnector)
            return (connectedSystem, null, ServerCertificateReadOutcome.NotConfiguredForSecureConnection, $"The '{connectorName}' connector does not make encrypted connections, so there is no server certificate to look at.");

        var endpoint = secureEndpointConnector.ResolveSecureEndpoint(connectedSystem.SettingValues);
        if (endpoint == null)
            return (connectedSystem, null, ServerCertificateReadOutcome.NotConfiguredForSecureConnection, $"The '{connectedSystem.Name}' Connected System is not configured to make an encrypted connection, so there is no server certificate to look at.");

        return (connectedSystem, endpoint, ServerCertificateReadOutcome.Read, null);
    }

    /// <summary>
    /// Overlays settings an administrator has entered but not saved onto the loaded Connected System, so the
    /// certificate they are shown belongs to the endpoint on their screen rather than the one last saved.
    /// </summary>
    /// <remarks>
    /// Encrypted settings are deliberately skipped. Nothing needed to work out where a system connects is a secret,
    /// and applying a draft secret here would put a plaintext credential on an instance JIM has no reason to hold
    /// one on.
    /// </remarks>
    private static void ApplyDraftSettingValues(ConnectedSystem connectedSystem, IReadOnlyCollection<ConnectedSystemSettingValueDraft> draftSettingValues)
    {
        var draftsBySettingId = draftSettingValues.ToDictionary(d => d.SettingId);

        foreach (var settingValue in connectedSystem.SettingValues
            .Where(sv => sv.Setting?.Type != ConnectedSystemSettingType.StringEncrypted &&
                         sv.Setting != null && draftsBySettingId.ContainsKey(sv.Setting.Id)))
        {
            var draft = draftsBySettingId[settingValue.Setting.Id];

            if (draft.StringValue != null)
                settingValue.StringValue = draft.StringValue;
            if (draft.IntValue.HasValue)
                settingValue.IntValue = draft.IntValue.Value;
            if (draft.CheckboxValue.HasValue)
                settingValue.CheckboxValue = draft.CheckboxValue.Value;
        }
    }

    /// <summary>
    /// Reads what the server presents, with the JIM certificate store supplied as additional trust anchors so a
    /// certificate the store already vouches for is not misreported as untrusted.
    /// </summary>
    private async Task<ServerCertificateReading?> ReadEndpointAsync(SecureEndpoint endpoint)
    {
        var trustedCertificates = await GetTrustedCertificatesAsync();

        try
        {
            return ServerCertificateReader.Read(endpoint, trustedCertificates);
        }
        finally
        {
            foreach (var certificate in trustedCertificates)
                certificate.Dispose();
        }
    }

    private static bool MatchesThumbprint(PresentedServerCertificate certificate, string expected)
    {
        return string.Equals(NormaliseThumbprint(certificate.Thumbprint), expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// Thumbprints are quoted with spaces or colons between the pairs depending on where they were copied from, so
    /// compare them without.
    /// </summary>
    private static string NormaliseThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return string.Empty;

        return new string(thumbprint.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static ServerCertificateTrustOutcome ToTrustOutcome(ServerCertificateReadOutcome outcome) => outcome switch
    {
        ServerCertificateReadOutcome.ConnectedSystemNotFound => ServerCertificateTrustOutcome.ConnectedSystemNotFound,
        ServerCertificateReadOutcome.ServerUnreachable => ServerCertificateTrustOutcome.ServerUnreachable,
        _ => ServerCertificateTrustOutcome.NotConfiguredForSecureConnection
    };

    #endregion

    /// <summary>
    /// Gets all enabled trusted certificates as X509Certificate2 objects.
    /// Implements ICertificateProvider for use by connectors.
    /// </summary>
    public async Task<List<X509Certificate2>> GetTrustedCertificatesAsync()
    {
        var certificates = await GetEnabledAsync();
        var x509Certs = new List<X509Certificate2>();

        foreach (var cert in certificates)
        {
            try
            {
                var x509 = await LoadX509CertificateAsync(cert);
                if (x509 != null)
                    x509Certs.Add(x509);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load certificate '{Name}' (ID: {Id})", cert.Name, cert.Id);
            }
        }

        return x509Certs;
    }

    /// <summary>
    /// Loads an X509Certificate2 from a TrustedCertificate record.
    /// </summary>
    private async Task<X509Certificate2?> LoadX509CertificateAsync(TrustedCertificate certificate)
    {
        byte[] certData;

        if (certificate.SourceType == CertificateSourceType.Uploaded)
        {
            if (certificate.CertificateData == null)
                return null;
            certData = certificate.CertificateData;
        }
        else
        {
            if (string.IsNullOrEmpty(certificate.FilePath) || !File.Exists(certificate.FilePath))
                return null;
            certData = await File.ReadAllBytesAsync(certificate.FilePath);
        }

        return ParseCertificate(certData);
    }

    /// <summary>
    /// Captures a versioned, metadata-only configuration snapshot of a Trusted Certificate onto its audit Activity
    /// via the shared ConfigurationChangeCaptureService (which owns the toggle, dedupe-guard, versioning and
    /// best-effort behaviours). The certificate is reloaded so the snapshot reflects persisted truth; call it after
    /// the change has been persisted.
    /// </summary>
    private async Task CaptureConfigurationChangeAsync(Activity activity, Guid certificateId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.TrustedCertificate, certificateId,
            async hashKey =>
            {
                var persisted = await Application.Repository.TrustedCertificates.GetByIdAsync(certificateId);
                return persisted == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Trusted Certificate {certificateId}");
    }

    /// <summary>
    /// Captures a tombstone snapshot of a Trusted Certificate onto its delete Activity, before the certificate is
    /// removed. Matching the other configuration types' deletion behaviour, this does not set
    /// <see cref="Activity.TrustedCertificateId"/> or a version: the certificate is deleted before the Activity
    /// completes, so the Activity is left unlinked and the snapshot is surfaced via the Activity itself rather than
    /// the object's history.
    /// </summary>
    private async Task CaptureConfigurationDeletionAsync(Activity activity, TrustedCertificate certificate, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            hashKey => Task.FromResult<ConfigurationSnapshot?>(Application.ConfigurationSnapshots.CreateSnapshot(certificate, hashKey)),
            $"Trusted Certificate {certificate.Id}");
    }

    /// <summary>
    /// Parses certificate data (PEM or DER encoded) into an X509Certificate2.
    /// </summary>
    private static X509Certificate2 ParseCertificate(byte[] certificateData)
    {
        try
        {
            // Try DER format first using the new X509CertificateLoader (.NET 9+)
            return X509CertificateLoader.LoadCertificate(certificateData);
        }
        catch
        {
            // Try PEM format - X509Certificate2.CreateFromPem is still the correct API for PEM
            var pemString = System.Text.Encoding.UTF8.GetString(certificateData);
            return X509Certificate2.CreateFromPem(pemString);
        }
    }
}
