using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Interfaces;
using JIM.Application.Utilities;
using Serilog;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Application.Servers;

/// <summary>
/// Provides services for managing trusted certificates in the JIM certificate store.
/// </summary>
public class CertificateServer : ICertificateProvider
{
    private JimApplication Application { get; }

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
    public async Task<TrustedCertificate> AddFromDataAsync(string name, byte[] certificateData, MetaverseObject? initiatedBy = null, string? notes = null)
    {
        var activity = new Activity
        {
            TargetName = name,
            TargetType = ActivityTargetType.TrustedCertificate,
            TargetOperationType = ActivityTargetOperationType.Create,
            Message = "Adding trusted certificate from uploaded data"
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

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

            AuditHelper.SetCreated(certificate, initiatedBy);
            Log.Information("Adding trusted certificate '{Name}' (Thumbprint: {Thumbprint}) from uploaded data", name, thumbprint);
            var result = await Application.Repository.TrustedCertificates.CreateAsync(certificate);

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
    public async Task<TrustedCertificate> AddFromFilePathAsync(string name, string filePath, MetaverseObject? initiatedBy = null, string? notes = null)
    {
        var activity = new Activity
        {
            TargetName = name,
            TargetType = ActivityTargetType.TrustedCertificate,
            TargetOperationType = ActivityTargetOperationType.Create,
            Message = $"Adding trusted certificate from file path: {filePath}"
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

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

            AuditHelper.SetCreated(certificate, initiatedBy);
            Log.Information("Adding trusted certificate '{Name}' (Thumbprint: {Thumbprint}) from file path: {FilePath}", name, thumbprint, filePath);
            var result = await Application.Repository.TrustedCertificates.CreateAsync(certificate);

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
    public async Task UpdateAsync(Guid id, MetaverseObject? initiatedBy = null, string? name = null, string? notes = null, bool? isEnabled = null)
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
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

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

            AuditHelper.SetUpdated(certificate, initiatedBy);
            Log.Information("Updating trusted certificate '{Name}' (ID: {Id})", certificate.Name, id);
            await Application.Repository.TrustedCertificates.UpdateAsync(certificate);

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
    public async Task DeleteAsync(Guid id, MetaverseObject? initiatedBy = null)
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
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        try
        {
            if (certificate != null)
            {
                Log.Information("Deleting trusted certificate '{Name}' (ID: {Id})", certificate.Name, id);
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
