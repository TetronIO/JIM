# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
    Persistent refresh-token storage for the JIM PowerShell module (issue #305).

    Only the OAuth refresh token is persisted, never the access token. The refresh
    token is stored in the operating system's native credential store so it is
    encrypted at rest and unlocked automatically as part of the user's OS logon:

      - Windows : Credential Manager (DPAPI, per-user) via advapi32 Cred* APIs
      - macOS   : login Keychain via the 'security' CLI
      - Linux   : libsecret via 'secret-tool' when present

    On a system with no usable store (typically headless/SSH Linux without a
    keyring), persistence is unavailable and callers fall back to in-memory
    storage. No bespoke encrypted file is ever written.

    To use a cached token in a new session the module re-fetches the OAuth
    configuration (/api/v1/auth/config + OIDC discovery) and performs a
    refresh_token grant, so nothing beyond the refresh token needs to be stored.
#>

# Service / target naming. The cache key (a normalised base URL) is appended so
# multiple JIM instances can be cached independently.
$script:JIMTokenStoreService = 'JIM'
$script:JIMTokenStoreTargetPrefix = 'JIM:'

function Get-JIMTokenCacheKey {
    <#
    .SYNOPSIS
        Derives a stable cache key from a JIM base URL.

    .DESCRIPTION
        Normalises the URL to "scheme://host:port" with a lower-cased scheme and
        host and an explicit port. This collapses incidental differences (trailing
        slash, default-port presence, host casing) so the same instance always maps
        to the same key, while keeping distinct schemes, hosts, and ports separate.

    .PARAMETER BaseUrl
        The JIM base URL, e.g. 'https://jim.company.com' or 'http://localhost:5200'.

    .OUTPUTS
        The normalised cache key string.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$BaseUrl
    )

    $uri = [Uri]$BaseUrl
    # [Uri].Port always returns the effective port (default if unspecified), so the
    # key is explicit regardless of whether the caller included the port.
    return "$($uri.Scheme.ToLowerInvariant())://$($uri.Host.ToLowerInvariant()):$($uri.Port)"
}

function Get-JIMTokenStoreProvider {
    <#
    .SYNOPSIS
        Determines which credential-store provider is available on this system.

    .OUTPUTS
        One of 'Windows', 'MacOS', 'LibSecret', or 'None'.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    if ($IsWindows -or $env:OS -match 'Windows') {
        return 'Windows'
    }

    if ($IsMacOS) {
        return 'MacOS'
    }

    # Linux (or any other platform): persistence requires libsecret's secret-tool.
    # When it is absent (typical on headless servers / SSH with no keyring), there
    # is no password-free store, so report None and let the caller fall back to
    # in-memory storage.
    if (Get-Command 'secret-tool' -ErrorAction SilentlyContinue) {
        return 'LibSecret'
    }

    return 'None'
}

function Test-JIMTokenPersistenceAvailable {
    <#
    .SYNOPSIS
        Returns $true when a usable credential store is available on this system.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param()

    return (Get-JIMTokenStoreProvider) -ne 'None'
}

function Initialize-JIMWindowsCredentialStore {
    <#
    .SYNOPSIS
        Compiles the Windows Credential Manager interop helper once per session.
    #>
    [CmdletBinding()]
    param()

    if ('JIMCredentialStore' -as [type]) {
        return
    }

    $source = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class JIMCredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    // CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512.
    private const int MaxBlobBytes = 2560;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPtr);

    [DllImport("advapi32", SetLastError = true, EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr cred);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredEnumerateW")]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr pCredentials);

    public static void Write(string target, string userName, string secret)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        if (blob.Length > MaxBlobBytes)
        {
            throw new ArgumentException("Secret exceeds the Windows Credential Manager blob size limit of " + MaxBlobBytes + " bytes.");
        }

        IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            CREDENTIAL cred = new CREDENTIAL();
            cred.Type = CRED_TYPE_GENERIC;
            cred.TargetName = target;
            cred.CredentialBlobSize = (uint)blob.Length;
            cred.CredentialBlob = blobPtr;
            cred.Persist = CRED_PERSIST_LOCAL_MACHINE;
            cred.UserName = userName;
            if (!CredWrite(ref cred, 0))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static string Read(string target)
    {
        IntPtr credPtr;
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out credPtr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                return null;
            }
            throw new System.ComponentModel.Win32Exception(err);
        }

        try
        {
            CREDENTIAL cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
            if (cred.CredentialBlobSize == 0)
            {
                return string.Empty;
            }
            byte[] blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, (int)cred.CredentialBlobSize);
            return Encoding.Unicode.GetString(blob);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static bool Delete(string target)
    {
        if (!CredDelete(target, CRED_TYPE_GENERIC, 0))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                return false;
            }
            throw new System.ComponentModel.Win32Exception(err);
        }
        return true;
    }

    public static string[] List(string filter)
    {
        uint count;
        IntPtr pCreds;
        if (!CredEnumerate(filter, 0, out count, out pCreds))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                return new string[0];
            }
            throw new System.ComponentModel.Win32Exception(err);
        }

        try
        {
            string[] names = new string[count];
            for (int i = 0; i < count; i++)
            {
                IntPtr credPtr = Marshal.ReadIntPtr(pCreds, i * IntPtr.Size);
                CREDENTIAL cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
                names[i] = cred.TargetName;
            }
            return names;
        }
        finally
        {
            CredFree(pCreds);
        }
    }
}
'@

    Add-Type -TypeDefinition $source -Language CSharp
}

function Invoke-JIMStoreProcess {
    <#
    .SYNOPSIS
        Runs an external credential-store binary, optionally piping a secret to its
        standard input without appending a newline.

    .DESCRIPTION
        Uses System.Diagnostics.Process with ArgumentList so arguments are passed
        without shell interpretation (no injection, no quoting issues). Writing the
        secret via stdin (rather than as an argument) keeps it off the process
        command line where the platform store supports it (libsecret does; the macOS
        'security' CLI requires the secret as an argument and is handled separately).

    .OUTPUTS
        A PSCustomObject with ExitCode, StdOut, and StdErr.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$Arguments = @(),

        [string]$StdinData
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    foreach ($argument in $Arguments) {
        $psi.ArgumentList.Add($argument)
    }
    $hasStdin = $PSBoundParameters.ContainsKey('StdinData')
    $psi.RedirectStandardInput = $hasStdin
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::Start($psi)
    if ($hasStdin) {
        # Write the exact secret bytes with no trailing newline so the stored value
        # matches the token precisely.
        $process.StandardInput.Write($StdinData)
        $process.StandardInput.Close()
    }
    $stdOut = $process.StandardOutput.ReadToEnd()
    $stdErr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    return [PSCustomObject]@{
        ExitCode = $process.ExitCode
        StdOut   = $stdOut
        StdErr   = $stdErr
    }
}

function Save-JIMToken {
    <#
    .SYNOPSIS
        Persists a refresh token for a JIM instance in the OS credential store.

    .DESCRIPTION
        No-op (returns $false) when no credential store is available. Returns $true
        when the token was stored successfully.

    .PARAMETER BaseUrl
        The JIM base URL the token belongs to.

    .PARAMETER RefreshToken
        The OAuth refresh token to persist.

    .OUTPUTS
        [bool] indicating whether the token was persisted.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$BaseUrl,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RefreshToken
    )

    $key = Get-JIMTokenCacheKey -BaseUrl $BaseUrl
    $provider = Get-JIMTokenStoreProvider

    switch ($provider) {
        'Windows' {
            Initialize-JIMWindowsCredentialStore
            [JIMCredentialStore]::Write("$script:JIMTokenStoreTargetPrefix$key", $key, $RefreshToken)
            Write-Verbose "Persisted refresh token to Windows Credential Manager for $key"
            return $true
        }
        'MacOS' {
            # The macOS 'security' CLI takes the secret as an argument (-w). This is
            # the documented interface; argv is visible only to the same user's
            # processes. -U updates the item if it already exists.
            $result = Invoke-JIMStoreProcess -FilePath 'security' -Arguments @(
                'add-generic-password',
                '-a', $key,
                '-s', $script:JIMTokenStoreService,
                '-U',
                '-w', $RefreshToken
            )
            if ($result.ExitCode -ne 0) {
                throw "Failed to store token in macOS Keychain (exit $($result.ExitCode)): $($result.StdErr.Trim())"
            }
            Write-Verbose "Persisted refresh token to macOS Keychain for $key"
            return $true
        }
        'LibSecret' {
            $result = Invoke-JIMStoreProcess -FilePath 'secret-tool' -Arguments @(
                'store',
                '--label', "JIM refresh token ($key)",
                'service', $script:JIMTokenStoreService,
                'url', $key
            ) -StdinData $RefreshToken
            if ($result.ExitCode -ne 0) {
                throw "Failed to store token via libsecret (exit $($result.ExitCode)): $($result.StdErr.Trim())"
            }
            Write-Verbose "Persisted refresh token to libsecret for $key"
            return $true
        }
        default {
            Write-Verbose "No credential store available; refresh token not persisted"
            return $false
        }
    }
}

function Get-JIMPersistedToken {
    <#
    .SYNOPSIS
        Retrieves a persisted refresh token for a JIM instance, or $null if none.

    .PARAMETER BaseUrl
        The JIM base URL whose token should be retrieved.

    .OUTPUTS
        The refresh token string, or $null when none is stored / no store exists.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$BaseUrl
    )

    $key = Get-JIMTokenCacheKey -BaseUrl $BaseUrl
    $provider = Get-JIMTokenStoreProvider

    switch ($provider) {
        'Windows' {
            Initialize-JIMWindowsCredentialStore
            $token = [JIMCredentialStore]::Read("$script:JIMTokenStoreTargetPrefix$key")
            if ([string]::IsNullOrEmpty($token)) { return $null }
            return $token
        }
        'MacOS' {
            $result = Invoke-JIMStoreProcess -FilePath 'security' -Arguments @(
                'find-generic-password',
                '-a', $key,
                '-s', $script:JIMTokenStoreService,
                '-w'
            )
            if ($result.ExitCode -ne 0) { return $null }
            # 'security -w' prints the secret followed by a newline.
            $token = $result.StdOut.TrimEnd("`r", "`n")
            if ([string]::IsNullOrEmpty($token)) { return $null }
            return $token
        }
        'LibSecret' {
            $result = Invoke-JIMStoreProcess -FilePath 'secret-tool' -Arguments @(
                'lookup',
                'service', $script:JIMTokenStoreService,
                'url', $key
            )
            if ($result.ExitCode -ne 0) { return $null }
            $token = $result.StdOut.TrimEnd("`r", "`n")
            if ([string]::IsNullOrEmpty($token)) { return $null }
            return $token
        }
        default {
            return $null
        }
    }
}

function Remove-JIMToken {
    <#
    .SYNOPSIS
        Removes persisted refresh token(s) from the OS credential store.

    .DESCRIPTION
        With -BaseUrl, removes the token for that instance. With -All, removes every
        JIM token. No-op when no credential store is available.

    .PARAMETER BaseUrl
        The JIM base URL whose token should be removed.

    .PARAMETER All
        Remove all persisted JIM tokens.

    .OUTPUTS
        [int] the number of tokens removed.
    #>
    # ShouldProcess is intentionally not implemented: this is a private helper, and
    # the user-facing destructive intent is expressed through Disconnect-JIM
    # (-ClearCache / -Url / -All), which is where any confirmation would belong.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '')]
    [CmdletBinding(DefaultParameterSetName = 'Single')]
    [OutputType([int])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Single')]
        [ValidateNotNullOrEmpty()]
        [string]$BaseUrl,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All
    )

    $provider = Get-JIMTokenStoreProvider
    if ($provider -eq 'None') {
        Write-Verbose "No credential store available; nothing to remove"
        return 0
    }

    if ($All) {
        return Remove-JIMAllPersistedToken -Provider $provider
    }

    $key = Get-JIMTokenCacheKey -BaseUrl $BaseUrl

    switch ($provider) {
        'Windows' {
            Initialize-JIMWindowsCredentialStore
            $removed = [JIMCredentialStore]::Delete("$script:JIMTokenStoreTargetPrefix$key")
            return [int]([bool]$removed)
        }
        'MacOS' {
            $result = Invoke-JIMStoreProcess -FilePath 'security' -Arguments @(
                'delete-generic-password',
                '-a', $key,
                '-s', $script:JIMTokenStoreService
            )
            return [int]($result.ExitCode -eq 0)
        }
        'LibSecret' {
            $result = Invoke-JIMStoreProcess -FilePath 'secret-tool' -Arguments @(
                'clear',
                'service', $script:JIMTokenStoreService,
                'url', $key
            )
            return [int]($result.ExitCode -eq 0)
        }
    }
}

function Remove-JIMAllPersistedToken {
    <#
    .SYNOPSIS
        Removes every persisted JIM token for the given provider.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '')]
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [string]$Provider
    )

    switch ($Provider) {
        'Windows' {
            Initialize-JIMWindowsCredentialStore
            $targets = [JIMCredentialStore]::List("$script:JIMTokenStoreTargetPrefix*")
            $count = 0
            foreach ($target in $targets) {
                if ([JIMCredentialStore]::Delete($target)) { $count++ }
            }
            return $count
        }
        'MacOS' {
            # 'security' has no wildcard delete; remove items for our service one at a
            # time until none remain (each delete removes a single matching item).
            $count = 0
            while ($true) {
                $result = Invoke-JIMStoreProcess -FilePath 'security' -Arguments @(
                    'delete-generic-password',
                    '-s', $script:JIMTokenStoreService
                )
                if ($result.ExitCode -ne 0) { break }
                $count++
            }
            return $count
        }
        'LibSecret' {
            # 'secret-tool clear' removes all items matching the given attributes, so
            # clearing by service alone removes every JIM token in one call.
            $result = Invoke-JIMStoreProcess -FilePath 'secret-tool' -Arguments @(
                'clear',
                'service', $script:JIMTokenStoreService
            )
            return [int]($result.ExitCode -eq 0)
        }
    }
}
