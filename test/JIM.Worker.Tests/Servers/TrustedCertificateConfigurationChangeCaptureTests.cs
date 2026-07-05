// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for Trusted Certificates: every store mutation records a versioned,
/// metadata-only snapshot on its audit Activity, keyed by <see cref="Activity.TrustedCertificateId"/>; the raw
/// certificate material (DER/PEM bytes) never appears in a snapshot in any encoding; a deletion records an
/// unversioned, unlinked tombstone; and the shared toggle and semantic-dedupe behaviours apply.
/// </summary>
[TestFixture]
public class TrustedCertificateConfigurationChangeCaptureTests
{
    private static readonly Guid CertificateId = Guid.Parse("6f9a1f3e-2b4c-4d5e-8f7a-9b0c1d2e3f4a");

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<ITrustedCertificateRepository> _certificateRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private Activity? _completedActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _certificateRepo = new Mock<ITrustedCertificateRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.TrustedCertificates).Returns(_certificateRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _certificateRepo.Setup(r => r.ExistsByThumbprintAsync(It.IsAny<string>())).ReturnsAsync(false);
        _certificateRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>()))
            .ReturnsAsync((TrustedCertificate c) => c);
        _certificateRepo.Setup(r => r.UpdateAsync(It.IsAny<TrustedCertificate>())).Returns(Task.CompletedTask);
        _certificateRepo.Setup(r => r.DeleteAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task AddFromDataAsync_CapturesVersionOneMetadataSnapshotWithoutCertificateMaterialAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupMaxVersion(0);
        var certificateData = SelfSignedCertificateBytes();
        TrustedCertificate? persisted = null;
        _certificateRepo.Setup(r => r.CreateAsync(It.IsAny<TrustedCertificate>()))
            .Callback<TrustedCertificate>(c => persisted = c)
            .ReturnsAsync((TrustedCertificate c) => c);
        _certificateRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(() => persisted);

        await _jim.Certificates.AddFromDataAsync("Corp Root CA", certificateData, NewUser(), notes: "primary chain", changeReason: "new corp PKI");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.TrustedCertificate));
        Assert.That(_completedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(persisted, Is.Not.Null);
        Assert.That(_completedActivity.TrustedCertificateId, Is.EqualTo(persisted!.Id), "the activity must carry the certificate id so history is queryable");
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("new corp PKI"));
        var snapshot = _completedActivity.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("\"objectType\":\"TrustedCertificate\""));
        Assert.That(snapshot, Does.Contain(persisted.Thumbprint), "the thumbprint is public metadata and identifies the certificate");
        Assert.That(snapshot, Does.Contain("primary chain"));
        Assert.That(snapshot, Does.Not.Contain(Convert.ToBase64String(certificateData)),
            "the raw certificate material must never be stored in a snapshot");
    }

    [Test]
    public async Task UpdateAsync_CapturesVersionedSnapshotOfEditablePropertiesAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var certificate = SetupCertificate(BuildCertificate());
        SetupMaxVersion(2);

        await _jim.Certificates.UpdateAsync(CertificateId, NewUser(), name: "Corp Root CA (renewed)", isEnabled: false, changeReason: "rotation");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity.TrustedCertificateId, Is.EqualTo(CertificateId));
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(3), "version is the existing maximum (2) + 1");
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("rotation"));
        Assert.That(certificate.Name, Is.EqualTo("Corp Root CA (renewed)"));
        var snapshot = _completedActivity.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("Corp Root CA (renewed)"));
        Assert.That(snapshot, Does.Contain("\"enabled\""));
    }

    [Test]
    public async Task UpdateAsync_ApiKeyInitiated_CapturesTooAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupCertificate(BuildCertificate());
        SetupMaxVersion(0);

        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "prov-api" };
        await _jim.Certificates.UpdateAsync(CertificateId, apiKey, isEnabled: false, changeReason: "via API");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TrustedCertificateId, Is.EqualTo(CertificateId));
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("via API"));
    }

    [Test]
    public async Task UpdateAsync_WhenTrackingDisabled_RecordsActivityButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);
        SetupCertificate(BuildCertificate());

        await _jim.Certificates.UpdateAsync(CertificateId, NewUser(), name: "Renamed", changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("no tracking"));
    }

    [Test]
    public async Task UpdateAsync_WhenUnchanged_SkipsVersionAndSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupCertificate(BuildCertificate());
        SetupMaxVersion(4);

        await _jim.Certificates.UpdateAsync(CertificateId, NewUser(), name: "Corp Root CA (renewed)");
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(5));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.TrustedCertificate, CertificateId))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.Certificates.UpdateAsync(CertificateId, NewUser(), name: "Corp Root CA (renewed)");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged Trusted Certificate must not consume a version");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity.TrustedCertificateId, Is.EqualTo(CertificateId), "the activity still deep-links to the certificate when the capture is skipped");
    }

    [Test]
    public async Task DeleteAsync_RecordsUnversionedUnlinkedTombstoneAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupCertificate(BuildCertificate());

        await _jim.Certificates.DeleteAsync(CertificateId, NewUser(), changeReason: "superseded");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Not.Null, "the tombstone preserves the deleted certificate's metadata");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectName\":\"Corp Root CA\""));
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null, "deletion tombstones are unversioned");
        Assert.That(_completedActivity.TrustedCertificateId, Is.Null, "deletion tombstones are unlinked; the certificate no longer exists");
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("superseded"));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static readonly byte[] HashKeyBytes = new byte[32];

    private void SetupTrackingSetting(bool enabled) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(HashKeyBytes))
            });

    private TrustedCertificate SetupCertificate(TrustedCertificate certificate)
    {
        _certificateRepo.Setup(r => r.GetByIdAsync(certificate.Id)).ReturnsAsync(certificate);
        return certificate;
    }

    private void SetupMaxVersion(int max) =>
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.TrustedCertificate, It.IsAny<Guid>()))
            .ReturnsAsync(max);

    private static TrustedCertificate BuildCertificate() => new()
    {
        Id = CertificateId,
        Name = "Corp Root CA",
        Thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD",
        Subject = "CN=Corp Root CA",
        Issuer = "CN=Corp Root CA",
        SerialNumber = "01FF",
        ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ValidTo = new DateTime(2036, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        SourceType = CertificateSourceType.Uploaded,
        CertificateData = new byte[] { 0x30, 0x82, 0x01, 0x0A },
        IsEnabled = true,
        Notes = "primary chain"
    };

    // A real self-signed certificate so AddFromDataAsync's parse path runs for real.
    private static byte[] SelfSignedCertificateBytes()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Corp Root CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return certificate.Export(X509ContentType.Cert);
    }

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

    /// <summary>A round-trip credential-protection test double using a recognisable encrypted-value prefix.</summary>
    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
