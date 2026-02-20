using JIM.Data.Repositories;
using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

public class TrustedCertificateRepository : ITrustedCertificateRepository
{
    private PostgresDataRepository Repository { get; }

    internal TrustedCertificateRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    public async Task<List<TrustedCertificate>> GetAllAsync()
    {
        return await Repository.Database.TrustedCertificates
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<List<TrustedCertificate>> GetEnabledAsync()
    {
        return await Repository.Database.TrustedCertificates
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<TrustedCertificate?> GetByIdAsync(Guid id)
    {
        return await Repository.Database.TrustedCertificates
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<TrustedCertificate?> GetByThumbprintAsync(string thumbprint)
    {
        return await Repository.Database.TrustedCertificates
            .FirstOrDefaultAsync(c => c.Thumbprint == thumbprint);
    }

    public async Task<TrustedCertificate> CreateAsync(TrustedCertificate certificate)
    {
        if (certificate.Id == Guid.Empty)
            certificate.Id = Guid.NewGuid();

        Repository.Database.TrustedCertificates.Add(certificate);
        await Repository.Database.SaveChangesAsync();
        return certificate;
    }

    public async Task UpdateAsync(TrustedCertificate certificate)
    {
        var existing = await Repository.Database.TrustedCertificates
            .FirstOrDefaultAsync(c => c.Id == certificate.Id);

        if (existing == null)
            throw new InvalidOperationException($"Certificate with ID {certificate.Id} not found.");

        // Update only editable fields
        existing.Name = certificate.Name;
        existing.Notes = certificate.Notes;
        existing.IsEnabled = certificate.IsEnabled;

        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var certificate = await Repository.Database.TrustedCertificates
            .FirstOrDefaultAsync(c => c.Id == id);

        if (certificate == null)
            throw new InvalidOperationException($"Certificate with ID {id} not found.");

        Repository.Database.TrustedCertificates.Remove(certificate);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task<bool> ExistsByThumbprintAsync(string thumbprint)
    {
        return await Repository.Database.TrustedCertificates
            .AnyAsync(c => c.Thumbprint == thumbprint);
    }
}
