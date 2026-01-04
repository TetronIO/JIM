using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Provides shared Data Protection configuration for JIM components.
/// Ensures consistent key storage across JIM.Web and JIM.Worker.
/// </summary>
public static class DataProtectionHelper
{
    private const string ApplicationName = "JIM";

    /// <summary>
    /// Creates a configured DataProtectionProvider that uses the shared JIM key storage.
    /// This ensures both JIM.Web and JIM.Worker can encrypt/decrypt credentials consistently.
    /// </summary>
    public static IDataProtectionProvider CreateProvider()
    {
        var keysPath = GetDataProtectionKeysPath();

        return DataProtectionProvider.Create(
            new DirectoryInfo(keysPath),
            configuration =>
            {
                configuration.SetApplicationName(ApplicationName);
                configuration.UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration
                {
                    EncryptionAlgorithm = EncryptionAlgorithm.AES_256_GCM,
                    ValidationAlgorithm = ValidationAlgorithm.HMACSHA256
                });
            });
    }

    /// <summary>
    /// Gets the path for storing Data Protection encryption keys.
    /// Priority: 1) JIM_ENCRYPTION_KEY_PATH env var, 2) /data/keys (Docker), 3) app data directory
    /// </summary>
    public static string GetDataProtectionKeysPath()
    {
        // 1. Check for explicit environment variable
        var envPath = Environment.GetEnvironmentVariable(JIM.Models.Core.Constants.Config.EncryptionKeyPath);
        if (!string.IsNullOrEmpty(envPath))
        {
            Log.Verbose("DataProtectionHelper: Using encryption key path from environment: {Path}", envPath);
            EnsureDirectoryExists(envPath);
            return envPath;
        }

        // 2. Check for Docker volume mount (common in containerised deployments)
        const string dockerPath = "/data/keys";
        if (Directory.Exists("/data"))
        {
            Log.Verbose("DataProtectionHelper: Using Docker volume for encryption keys: {Path}", dockerPath);
            EnsureDirectoryExists(dockerPath);
            return dockerPath;
        }

        // 3. Fallback to application data directory (platform-specific)
        // Linux: ~/.local/share/JIM/keys
        // Windows: %LOCALAPPDATA%\JIM\keys
        // Container: JIM/keys (relative to app directory if LocalApplicationData is empty)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataPath = Path.Combine(
            localAppData,
            "JIM",
            "keys");

        // If LocalApplicationData is empty (common in containers), this will be a relative path
        // which resolves to the application's working directory
        Log.Verbose("DataProtectionHelper: Using application data directory for encryption keys: {Path}", appDataPath);
        EnsureDirectoryExists(appDataPath);
        return appDataPath;
    }

    /// <summary>
    /// Ensures a directory exists, creating it with restricted permissions if necessary.
    /// </summary>
    private static void EnsureDirectoryExists(string path)
    {
        if (Directory.Exists(path))
            return;

        try
        {
            var directoryInfo = Directory.CreateDirectory(path);

            // On Unix-like systems, set restrictive permissions (700 = owner only)
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    directoryInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                    Log.Verbose("DataProtectionHelper: Created key directory with restricted permissions: {Path}", path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "DataProtectionHelper: Could not set restrictive permissions on key directory: {Path}. Please secure manually.", path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DataProtectionHelper: Failed to create key directory: {Path}", path);
            throw;
        }
    }
}
