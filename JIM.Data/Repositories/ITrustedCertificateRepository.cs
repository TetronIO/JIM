using JIM.Models.Core;

namespace JIM.Data.Repositories;

public interface ITrustedCertificateRepository
{
    /// <summary>
    /// Gets all trusted certificates.
    /// </summary>
    Task<List<TrustedCertificate>> GetAllAsync();

    /// <summary>
    /// Gets all enabled trusted certificates.
    /// </summary>
    Task<List<TrustedCertificate>> GetEnabledAsync();

    /// <summary>
    /// Gets a trusted certificate by its ID.
    /// </summary>
    Task<TrustedCertificate?> GetByIdAsync(Guid id);

    /// <summary>
    /// Gets a trusted certificate by its thumbprint.
    /// </summary>
    Task<TrustedCertificate?> GetByThumbprintAsync(string thumbprint);

    /// <summary>
    /// Creates a new trusted certificate.
    /// </summary>
    Task<TrustedCertificate> CreateAsync(TrustedCertificate certificate);

    /// <summary>
    /// Updates an existing trusted certificate.
    /// </summary>
    Task UpdateAsync(TrustedCertificate certificate);

    /// <summary>
    /// Deletes a trusted certificate.
    /// </summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Checks if a certificate with the given thumbprint already exists.
    /// </summary>
    Task<bool> ExistsByThumbprintAsync(string thumbprint);
}
