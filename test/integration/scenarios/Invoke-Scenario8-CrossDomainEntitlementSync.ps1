<#
.SYNOPSIS
    Test Scenario 8: Cross-domain Entitlement Synchronisation

.DESCRIPTION
    Validates synchronisation of entitlement groups (security groups, distribution groups)
    between two directory instances (source and target).
    Source AD is authoritative for groups. Tests initial sync, forward sync,
    drift detection, and state reassertion.

    This scenario is self-contained and will:
    1. Populate users and groups in Source AD
    2. Configure JIM with user and group sync rules
    3. Sync users between domains (prerequisite for group member resolution)
    4. Sync groups with membership preservation

.PARAMETER Step
    Which test step to execute (InitialSync, ForwardSync, DetectDrift, ReassertState, NewGroup, DeleteGroup, All)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 20)

.EXAMPLE
    ./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step All -Template Nano -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step InitialSync -Template Small -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("ImportToMV", "InitialSync", "ForwardSync", "DetectDrift", "ReassertState", "NewGroup", "DeleteGroup", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Test-GroupHelpers.ps1"

# Derive directory-specific configuration
if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Source
}

$isOpenLDAP = ($DirectoryConfig.UserObjectClass -eq "inetOrgPerson")

if ($isOpenLDAP) {
    $sourceConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
    $targetConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Target
}
else {
    $sourceConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Source
    $targetConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Target
    # Scenario 8 places groups in OU=Entitlements, not the default OU=Groups
    $sourceConfig.GroupContainer = "OU=Entitlements,OU=Corp,DC=resurgam,DC=local"
    $targetConfig.GroupContainer = "OU=Entitlements,OU=CorpManaged,DC=gentian,DC=local"
}

$sourceContainerName = $sourceConfig.ContainerName
$targetContainerName = $targetConfig.ContainerName
$sourceSystemName    = $sourceConfig.ConnectedSystemName
$targetSystemName    = $targetConfig.ConnectedSystemName

Write-TestSection "Scenario 8: Cross-domain Entitlement Synchronisation"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Cross-domain Entitlement Synchronisation"
    Template = $Template
    Steps = @()
    Success = $false
}

try {
    # Step 0: Setup JIM configuration
    Write-TestSection "Step 0: Setup and Verification"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    # Verify Scenario 8 containers are running
    Write-Host "Verifying Scenario 8 infrastructure..." -ForegroundColor Gray

    if ($isOpenLDAP) {
        # OpenLDAP uses a single container for both Source and Target suffixes
        $sourceStatus = docker inspect --format='{{.State.Health.Status}}' $sourceContainerName 2>&1
        if ($sourceStatus -ne "healthy") {
            throw "$sourceContainerName container is not healthy (status: $sourceStatus)"
        }
        Write-Host "  Source OpenLDAP ($sourceContainerName) healthy" -ForegroundColor Green
        Write-Host "  Target OpenLDAP (same container, different suffix) healthy" -ForegroundColor Green
    }
    else {
        $sourceStatus = docker inspect --format='{{.State.Health.Status}}' $sourceContainerName 2>&1
        $targetStatus = docker inspect --format='{{.State.Health.Status}}' $targetContainerName 2>&1
        if ($sourceStatus -ne "healthy") {
            throw "$sourceContainerName container is not healthy (status: $sourceStatus)"
        }
        if ($targetStatus -ne "healthy") {
            throw "$targetContainerName container is not healthy (status: $targetStatus)"
        }
        Write-Host "  Source AD ($sourceContainerName) healthy" -ForegroundColor Green
        Write-Host "  Target AD ($targetContainerName) healthy" -ForegroundColor Green
    }

    # Populate test data
    if (-not $SkipPopulate) {
        if ($isOpenLDAP) {
            Write-Host "Populating test data in Source OpenLDAP..." -ForegroundColor Gray
            & "$PSScriptRoot/../Populate-OpenLDAP-Scenario8.ps1" -Template $Template -Instance Source
            Write-Host "Creating OU structure in Target OpenLDAP..." -ForegroundColor Gray
            & "$PSScriptRoot/../Populate-OpenLDAP-Scenario8.ps1" -Template $Template -Instance Target
        }
        else {
            Write-Host "Populating test data in Source AD..." -ForegroundColor Gray
            & "$PSScriptRoot/../Populate-SambaAD-Scenario8.ps1" -Template $Template -Instance Source
            Write-Host "Creating OU structure in Target AD..." -ForegroundColor Gray
            & "$PSScriptRoot/../Populate-SambaAD-Scenario8.ps1" -Template $Template -Instance Target
        }
        Write-Host "Test data populated" -ForegroundColor Green
    } else {
        Write-Host "Using pre-populated snapshot - skipping population" -ForegroundColor Green
    }

    # Run Setup-Scenario8 to configure JIM
    Write-Host "Running Scenario 8 setup..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario8.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -ExportConcurrency $ExportConcurrency -MaxExportParallelism $MaxExportParallelism -DirectoryConfig $DirectoryConfig

    Write-Host "JIM configured for Scenario 8" -ForegroundColor Green

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Get connected system and run profile IDs
    $connectedSystems = Get-JIMConnectedSystem
    $sourceSystem = $connectedSystems | Where-Object { $_.name -eq $sourceSystemName }
    $targetSystem = $connectedSystems | Where-Object { $_.name -eq $targetSystemName }

    if (-not $sourceSystem -or -not $targetSystem) {
        throw "Connected systems not found. Ensure Setup-Scenario8.ps1 completed successfully."
    }

    $sourceProfiles = Get-JIMRunProfile -ConnectedSystemId $sourceSystem.id
    $targetProfiles = Get-JIMRunProfile -ConnectedSystemId $targetSystem.id

    # Full profiles (for initial sync)
    $sourceFullImportProfile = $sourceProfiles | Where-Object { $_.name -eq "Full Import" }
    $sourceScopedImportProfile = $sourceProfiles | Where-Object { $_.name -eq "Full Import (Scoped)" }
    $sourceFullSyncProfile = $sourceProfiles | Where-Object { $_.name -eq "Full Sync" }
    $sourceExportProfile = $sourceProfiles | Where-Object { $_.name -eq "Export" }

    $targetFullImportProfile = $targetProfiles | Where-Object { $_.name -eq "Full Import" }
    $targetScopedImportProfile = $targetProfiles | Where-Object { $_.name -eq "Full Import (Scoped)" }
    $targetFullSyncProfile = $targetProfiles | Where-Object { $_.name -eq "Full Sync" }
    $targetExportProfile = $targetProfiles | Where-Object { $_.name -eq "Export" }

    # Delta profiles (for forward sync after initial)
    $sourceDeltaImportProfile = $sourceProfiles | Where-Object { $_.name -eq "Delta Import" }
    $sourceDeltaSyncProfile = $sourceProfiles | Where-Object { $_.name -eq "Delta Sync" }
    $targetDeltaImportProfile = $targetProfiles | Where-Object { $_.name -eq "Delta Import" }
    $targetDeltaSyncProfile = $targetProfiles | Where-Object { $_.name -eq "Delta Sync" }

    # Helper function to check if a group exists in the directory with retry.
    # Directory servers can have brief consistency delays after LDAP writes.
    function Test-DirectoryGroupExists {
        param(
            [Parameter(Mandatory)][string]$GroupName,
            [Parameter(Mandatory)][hashtable]$Config,
            [int]$MaxRetries = 3,
            [int]$RetryDelaySeconds = 2
        )
        for ($attempt = 1; $attempt -le ($MaxRetries + 1); $attempt++) {
            $exists = Test-LDAPGroupExists -GroupName $GroupName -DirectoryConfig $Config
            if ($exists) {
                return $true
            }
            if ($attempt -le $MaxRetries) {
                Write-Host "    Group '$GroupName' not yet visible (attempt $attempt/$($MaxRetries + 1)), retrying in ${RetryDelaySeconds}s..." -ForegroundColor Yellow
                Start-Sleep -Seconds $RetryDelaySeconds
            }
        }
        return $false
    }

    # Helper functions for directory-specific group operations
    function Get-DirectoryGroupList {
        param([Parameter(Mandatory)][hashtable]$Config)
        return Get-LDAPGroupList -DirectoryConfig $Config
    }

    function Get-DirectoryGroupMembers {
        param(
            [Parameter(Mandatory)][string]$GroupName,
            [Parameter(Mandatory)][hashtable]$Config
        )
        if ($isOpenLDAP) {
            # OpenLDAP: return member DNs (uid=username format, matched by Test-MemberInList)
            return Get-LDAPGroupMembers -GroupName $GroupName -DirectoryConfig $Config
        }
        else {
            # Samba AD: use samba-tool which returns sAMAccountNames directly.
            # Get-LDAPGroupMembers returns DNs with CN=DisplayName which don't match
            # sAMAccountName values from Get-DirectoryUserList.
            $output = docker exec $Config.ContainerName samba-tool group listmembers $GroupName 2>&1
            if ($LASTEXITCODE -ne 0 -or -not $output) { return @() }
            return @($output -split "`n" | Where-Object { $_.Trim() -ne "" } | ForEach-Object { $_.Trim() })
        }
    }

    function Get-DirectoryUserList {
        param([Parameter(Mandatory)][hashtable]$Config)
        # Return user names (uid for OpenLDAP, sAMAccountName for AD)
        $userNameAttr = $Config.UserNameAttr
        $userObjectClass = $Config.UserObjectClass
        $filter = if ($userObjectClass -eq "user") {
            "(&(objectClass=user)(!(objectClass=computer)))"
        } else {
            "(objectClass=$userObjectClass)"
        }
        $result = Invoke-LDAPSearch `
            -ContainerName $Config.ContainerName `
            -Server "localhost" `
            -Port $Config.LdapSearchPort `
            -Scheme $Config.LdapSearchScheme `
            -BaseDN $Config.UserContainer `
            -BindDN $Config.BindDN `
            -BindPassword $Config.BindPassword `
            -Filter $filter `
            -Attributes @($userNameAttr)
        if ($null -eq $result) { return @() }
        $users = @()
        $lines = $result -split "`n"
        foreach ($line in $lines) {
            if ($line -match "^${userNameAttr}:\s*(.+)$") {
                $users += $matches[1].Trim()
            }
        }
        return $users
    }

    function Add-DirectoryGroupMember {
        param(
            [Parameter(Mandatory)][string]$GroupName,
            [Parameter(Mandatory)][string]$MemberName,
            [Parameter(Mandatory)][hashtable]$Config
        )
        if ($isOpenLDAP) {
            # If MemberName is already a full DN (contains = and ,), use it directly.
            # Otherwise look up the user's DN by username attribute.
            if ($MemberName -match '=.*,') {
                $memberDn = $MemberName
            } else {
                $user = Get-LDAPUser -UserIdentifier $MemberName -DirectoryConfig $Config
                if (-not $user) { throw "User '$MemberName' not found" }
                $memberDn = $user['dn']
            }
            $groupDn = "cn=$GroupName,$($Config.GroupContainer)"
            $ldif = "dn: $groupDn`nchangetype: modify`nadd: member`nmember: $memberDn`n"
            $ldifPath = [System.IO.Path]::GetTempFileName()
            Set-Content -Path $ldifPath -Value $ldif -NoNewline
            try {
                $result = bash -c "cat '$ldifPath' | docker exec -i $($Config.ContainerName) ldapmodify -x -H 'ldap://localhost:$($Config.LdapSearchPort)' -D '$($Config.BindDN)' -w '$($Config.BindPassword)' -c" 2>&1
                return $result
            }
            finally {
                Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
            }
        }
        else {
            return docker exec $Config.ContainerName samba-tool group addmembers $GroupName $MemberName 2>&1
        }
    }

    function Remove-DirectoryGroupMember {
        param(
            [Parameter(Mandatory)][string]$GroupName,
            [Parameter(Mandatory)][string]$MemberName,
            [Parameter(Mandatory)][hashtable]$Config
        )
        if ($isOpenLDAP) {
            # If MemberName is already a full DN (contains = and ,), use it directly.
            # Otherwise look up the user's DN by username attribute.
            if ($MemberName -match '=.*,') {
                $memberDn = $MemberName
            } else {
                $user = Get-LDAPUser -UserIdentifier $MemberName -DirectoryConfig $Config
                if (-not $user) { throw "User '$MemberName' not found" }
                $memberDn = $user['dn']
            }
            $groupDn = "cn=$GroupName,$($Config.GroupContainer)"
            $ldif = "dn: $groupDn`nchangetype: modify`ndelete: member`nmember: $memberDn`n"
            $ldifPath = [System.IO.Path]::GetTempFileName()
            Set-Content -Path $ldifPath -Value $ldif -NoNewline
            try {
                $result = bash -c "cat '$ldifPath' | docker exec -i $($Config.ContainerName) ldapmodify -x -H 'ldap://localhost:$($Config.LdapSearchPort)' -D '$($Config.BindDN)' -w '$($Config.BindPassword)' -c" 2>&1
                return $result
            }
            finally {
                Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
            }
        }
        else {
            return docker exec $Config.ContainerName samba-tool group removemembers $GroupName $MemberName 2>&1
        }
    }

    function New-DirectoryGroup {
        param(
            [Parameter(Mandatory)][string]$GroupName,
            [Parameter(Mandatory)][string]$Description,
            [Parameter(Mandatory)][hashtable]$Config,
            [string]$InitialMemberDn  # Required for groupOfNames
        )
        if ($isOpenLDAP) {
            $groupDn = "cn=$GroupName,$($Config.GroupContainer)"
            $memberDn = if ($InitialMemberDn) { $InitialMemberDn } else { "cn=placeholder" }
            $ldif = "dn: $groupDn`nobjectClass: groupOfNames`ncn: $GroupName`ndescription: $Description`nmember: $memberDn`n"
            $ldifPath = [System.IO.Path]::GetTempFileName()
            Set-Content -Path $ldifPath -Value $ldif -NoNewline
            try {
                $result = bash -c "cat '$ldifPath' | docker exec -i $($Config.ContainerName) ldapadd -x -H 'ldap://localhost:$($Config.LdapSearchPort)' -D '$($Config.BindDN)' -w '$($Config.BindPassword)' -c" 2>&1
                return $result
            }
            finally {
                Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
            }
        }
        else {
            return docker exec $Config.ContainerName samba-tool group add $GroupName `
                --groupou="OU=Entitlements,OU=Corp" `
                --description="$Description" 2>&1
        }
    }

    function Remove-DirectoryGroup {
        param(
            [Parameter(Mandatory)][string]$GroupName,
            [Parameter(Mandatory)][hashtable]$Config
        )
        if ($isOpenLDAP) {
            $groupDn = "cn=$GroupName,$($Config.GroupContainer)"
            $result = docker exec $Config.ContainerName ldapdelete -x -H "ldap://localhost:$($Config.LdapSearchPort)" -D $Config.BindDN -w $Config.BindPassword "$groupDn" 2>&1
            return $result
        }
        else {
            return docker exec $Config.ContainerName samba-tool group delete $GroupName 2>&1
        }
    }

    # Helper to check if a user name appears in a member list.
    # For Samba AD, members are returned as sAMAccountName (plain names).
    # For OpenLDAP, members are returned as full DNs (e.g. uid=alice.smith0,ou=People,...).
    # This function handles both formats.
    function Test-MemberInList {
        param(
            [Parameter(Mandatory)][string]$UserName,
            [Parameter(Mandatory)][array]$MemberList
        )
        foreach ($member in $MemberList) {
            # Exact match (Samba AD returns plain names)
            if ($member -eq $UserName) { return $true }
            # DN match: check if the DN starts with uid=<name> or CN=<name>
            if ($member -match "^(uid|cn|CN)=$([regex]::Escape($UserName)),") { return $true }
        }
        return $false
    }

    # Helper function to run FULL forward sync (Source -> Metaverse -> Target)
    # Used for initial synchronisation when objects already exist in both systems
    function Invoke-FullForwardSync {
        param(
            [string]$Context = "",
            [switch]$UseScopedImport
        )
        $contextSuffix = if ($Context) { " ($Context)" } else { "" }
        Write-Host "  Running FULL forward sync (Source → Metaverse → Target)..." -ForegroundColor Gray

        # INITIAL RECONCILIATION FLOW:
        # When objects already exist in both Source and Target AD, we need to:
        # 1. Import from BOTH systems first (create CSOs without sync)
        # 2. Sync Source (project to create MVOs)
        # 3. Sync Target (join Target CSOs to MVOs BEFORE export evaluation creates provisioning CSOs)
        # 4. Sync Source again (now exports will see existing Target CSOs and generate Updates, not Creates)

        # Step 1: Full Import from Source (scoped to partition when requested, #353)
        $sourceImportToUse = if ($UseScopedImport -and $sourceScopedImportProfile) { $sourceScopedImportProfile } else { $sourceFullImportProfile }
        $sourceImportLabel = if ($UseScopedImport -and $sourceScopedImportProfile) { "Source Full Import (Scoped)" } else { "Source Full Import" }
        Write-Host "    $sourceImportLabel from Source AD..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceImportToUse.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "$sourceImportLabel$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Fail-fast: check for unresolved references after source import.
        # All references MUST resolve in a single import run — JIM resolves references after
        # all pages are imported, so ordering within pages is not an issue.
        Assert-NoUnresolvedReferences -ConnectedSystemId $sourceSystem.id -Name "Source AD" -Context "after Full Import$contextSuffix"

        # Step 2: Full Import from Target (BEFORE any sync)
        # Import Target CSOs early so they can join to MVOs before export rules create provisioning CSOs
        $targetImportToUse = if ($UseScopedImport -and $targetScopedImportProfile) { $targetScopedImportProfile } else { $targetFullImportProfile }
        $targetImportLabel = if ($UseScopedImport -and $targetScopedImportProfile) { "Target Full Import (Scoped)" } else { "Target Full Import" }
        Write-Host "    $targetImportLabel from Target AD (discover existing objects)..." -ForegroundColor Gray
        $targetImportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetImportToUse.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $targetImportResult.activityId -Name "$targetImportLabel$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 3: Full Sync Source to Metaverse (projection)
        # Creates MVOs from Source CSOs. Export rules will evaluate, but Target CSOs exist
        # and will join in the next step, so any provisioning CSOs created here will be replaced.
        Write-Host "    Full syncing Source to metaverse (create MVOs)..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Source Full Sync (project)$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 4: Full Sync Target (join Target CSOs to MVOs)
        # Target CSOs join to MVOs via matching rules. This links real Target CSOs to MVOs.
        # If provisioning CSOs were created in step 3, they should be replaced/updated.
        Write-Host "    Full syncing Target (join Target CSOs to MVOs)..." -ForegroundColor Gray
        $targetSyncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $targetSyncResult.activityId -Name "Target Full Sync (join)$contextSuffix" -AllowWarnings
        Start-Sleep -Seconds $WaitSeconds

        # Step 5: Export to Target
        Write-Host "    Exporting to Target AD..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Target Export$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 6: Full Confirming Import from Target
        Write-Host "    Full confirming import in Target AD..." -ForegroundColor Gray
        $confirmImportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmImportResult.activityId -Name "Target Full Confirming Import$contextSuffix"
        Assert-NoUnresolvedReferences -ConnectedSystemId $targetSystem.id -Name "Target AD" -Context "after Confirming Import$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 7: Full Confirming Sync (informational only during initial sync)
        # Note: During initial sync with pre-existing objects, this may have CouldNotJoinDueToExistingJoin
        # errors because provisioning CSOs were created during Source Full Sync before real Target CSOs
        # could join. This is a known limitation of the provisioning model with pre-existing objects.
        # The important validation is that Export succeeded - confirming sync is informational only.
        Write-Host "    Full confirming sync..." -ForegroundColor Gray
        $confirmSyncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSyncProfile.id -Wait -PassThru
        $confirmSyncActivity = Get-JIMActivity -Id $confirmSyncResult.activityId
        if ($confirmSyncActivity.status -eq "Complete") {
            Write-Host "  ✓ Target Full Confirming Sync$contextSuffix completed successfully (Status: Complete)" -ForegroundColor Green
        }
        elseif ($confirmSyncActivity.status -eq "CompleteWithWarning") {
            Write-Host "  ⚠ Target Full Confirming Sync$contextSuffix completed with warnings (expected during initial sync with pre-existing objects)" -ForegroundColor Yellow
        }
        else {
            Write-Host "  ⚠ Target Full Confirming Sync$contextSuffix completed with status: $($confirmSyncActivity.status)" -ForegroundColor Yellow
            Write-Host "    (CouldNotJoinDueToExistingJoin errors are expected during initial sync with pre-existing objects)" -ForegroundColor Yellow
        }
        Start-Sleep -Seconds $WaitSeconds
    }

    # Helper function to run DELTA forward sync (Source -> Metaverse -> Target)
    # Used for incremental changes after initial sync
    function Invoke-DeltaForwardSync {
        param(
            [string]$Context = ""
        )
        $contextSuffix = if ($Context) { " ($Context)" } else { "" }
        Write-Host "  Running DELTA forward sync (Source → Metaverse → Target)..." -ForegroundColor Gray

        # Step 1: Delta Import from Source
        # OpenLDAP: allow DeltaImportFallbackToFullImport warning — the accesslog watermark may not
        # be available if the accesslog exceeded the server's size limit during the preceding full import.
        # The connector automatically falls back to a full import and establishes the watermark.
        Write-Host "    Delta importing from Source AD..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceDeltaImportProfile.id -Wait -PassThru
        if ($isOpenLDAP) {
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Source Delta Import$contextSuffix" `
                -AllowWarnings -AllowedWarningTypes @('DeltaImportFallbackToFullImport')
        } else {
            Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Source Delta Import$contextSuffix"
        }
        Assert-NoUnresolvedReferences -ConnectedSystemId $sourceSystem.id -Name "Source AD" -Context "after Delta Import$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 2: Delta Sync to Metaverse
        Write-Host "    Delta syncing to metaverse..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceDeltaSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Source Delta Sync$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 3: Export to Target
        Write-Host "    Exporting to Target AD..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Target Export$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 4: Delta Confirming Import from Target
        Write-Host "    Delta confirming import in Target AD..." -ForegroundColor Gray
        $confirmImportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaImportProfile.id -Wait -PassThru
        if ($isOpenLDAP) {
            Assert-ActivitySuccess -ActivityId $confirmImportResult.activityId -Name "Target Delta Confirming Import$contextSuffix" `
                -AllowWarnings -AllowedWarningTypes @('DeltaImportFallbackToFullImport')
        } else {
            Assert-ActivitySuccess -ActivityId $confirmImportResult.activityId -Name "Target Delta Confirming Import$contextSuffix"
        }
        Assert-NoUnresolvedReferences -ConnectedSystemId $targetSystem.id -Name "Target AD" -Context "after Delta Confirming Import$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds

        # Step 5: Delta Confirming Sync
        Write-Host "    Delta confirming sync..." -ForegroundColor Gray
        $confirmSyncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmSyncResult.activityId -Name "Target Delta Confirming Sync$contextSuffix"
        Start-Sleep -Seconds $WaitSeconds
    }

    # Backward-compatible alias for InitialSync (uses Full with scoped import)
    function Invoke-ForwardSync {
        param(
            [string]$Context = ""
        )
        Invoke-FullForwardSync -Context $Context -UseScopedImport
    }

    # Test 0: ImportToMV (Import from Source and sync to Metaverse ONLY - no export)
    if ($Step -eq "ImportToMV") {
        Write-TestSection "Test 0: ImportToMV (Source → Metaverse Only)"

        Write-Host "Importing from Source AD and projecting to Metaverse..." -ForegroundColor Gray
        Write-Host "  (Stopping before export to allow review)" -ForegroundColor Yellow

        # Step 1: Import from Source
        Write-Host "    Importing from Source AD..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Source Full Import"
        Start-Sleep -Seconds $WaitSeconds

        # Step 2: Sync to Metaverse
        Write-Host "    Syncing to metaverse..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Source Full Sync"

        Write-Host ""
        Write-Host "✓ ImportToMV complete - objects should now be in the Metaverse" -ForegroundColor Green
        Write-Host ""
        Write-Host "Next steps to review:" -ForegroundColor Cyan
        Write-Host "  1. Check Metaverse for projected users and groups" -ForegroundColor Gray
        Write-Host "  2. Verify attribute mappings are correct" -ForegroundColor Gray
        Write-Host "  3. Run -Step InitialSync to continue with export to Target AD" -ForegroundColor Gray
        Write-Host ""

        $testResults.Steps += "ImportToMV"
        $testResults.Success = $true
        return
    }

    # Test 1: InitialSync (Import groups from Source, sync to Target)
    if ($Step -eq "InitialSync" -or $Step -eq "All") {
        Write-TestSection "Test 1: InitialSync (Source → Target)"

        Write-Host "Syncing initial data..." -ForegroundColor Gray
        Invoke-ForwardSync -Context "InitialSync"

        Write-Host "Validating groups in Target AD..." -ForegroundColor Gray

        # Find groups with members in Source directory
        $testGroups = @(Get-DirectoryGroupList -Config $sourceConfig | Where-Object { $_ -match "^(Company-|Dept-|Location-|Project-)" })

        if ($testGroups.Count -eq 0) {
            throw "No test groups found in Source directory - ensure population script ran successfully"
        }
        else {
            # Find a group with members for validation
            $validationGroup = $null
            $sourceMemberCount = 0
            foreach ($grp in $testGroups) {
                $grpName = $grp.Trim()
                $memberList = @(Get-DirectoryGroupMembers -GroupName $grpName -Config $sourceConfig)
                if ($memberList.Count -gt 0) {
                    $validationGroup = $grpName
                    $sourceMemberCount = $memberList.Count
                    break
                }
            }

            if ($validationGroup) {
                Write-Host "  Validation group: $validationGroup (Source members: $sourceMemberCount)" -ForegroundColor Cyan

                # Check if group exists in Target (with retry for consistency)
                if (Test-DirectoryGroupExists -GroupName $validationGroup -Config $targetConfig) {
                    Write-Host "  Group '$validationGroup' exists in Target" -ForegroundColor Green

                    # Check if members were synced
                    $targetMemberList = @(Get-DirectoryGroupMembers -GroupName $validationGroup -Config $targetConfig)
                    $targetMemberCount = $targetMemberList.Count

                    if ($targetMemberCount -eq $sourceMemberCount) {
                        Write-Host "  ✓ Member count matches: Source=$sourceMemberCount, Target=$targetMemberCount" -ForegroundColor Green
                    }
                    elseif ($targetMemberCount -gt 0) {
                        $deficit = $sourceMemberCount - $targetMemberCount
                        Write-Host "  ✗ Member count mismatch: Source=$sourceMemberCount, Target=$targetMemberCount (deficit: $deficit)" -ForegroundColor Red
                        Write-Host "    Initial sync did not export all group members to Target AD." -ForegroundColor Red
                        Write-Host "    This may indicate unresolved references or LDAP modify request size limits." -ForegroundColor Red
                        throw "InitialSync validation failed: group '$validationGroup' has $targetMemberCount members in Target but expected $sourceMemberCount from Source (deficit: $deficit)"
                    }
                    else {
                        Write-Host "  ✗ No members in Target group (expected $sourceMemberCount)" -ForegroundColor Red
                        throw "InitialSync validation failed: group '$validationGroup' has 0 members in Target but expected $sourceMemberCount from Source"
                    }
                }
                else {
                    throw "Group '$validationGroup' not found in Target AD — expected it to be provisioned during initial sync"
                }
            }
            else {
                throw "No groups with members found for validation — Source AD should have groups with members after initial setup"
            }
        }

        Write-Host "✓ InitialSync test completed" -ForegroundColor Green
        $testResults.Steps += "InitialSync"
    }

    # Test 2: ForwardSync (Add/remove members in Source, sync to Target)
    if ($Step -eq "ForwardSync" -or $Step -eq "All") {
        Write-TestSection "Test 2: ForwardSync (Membership Changes)"

        Write-Host "This test validates that membership changes in Source AD flow to Target AD" -ForegroundColor Gray

        # Container names for Source and Target AD
        # Container names from config (set at script top)
        $sourceContainer = $sourceContainerName
        $targetContainer = $targetContainerName

        # Step 2.1: Find a group and users to test with
        Write-Host "  Finding test group and users..." -ForegroundColor Gray

        # Get a group from Source (use first group from Entitlements)
        $allGroups = @(Get-DirectoryGroupList -Config $sourceConfig | Where-Object { $_ -match "^(Company-|Dept-|Location-|Project-)" })

        if ($allGroups.Count -eq 0) {
            throw "No test groups found in Source. Ensure InitialSync has been run."
        }

        $testGroupName = $allGroups[0].Trim()
        Write-Host "    Test group: $testGroupName" -ForegroundColor Cyan

        # Get current members of the test group in Source
        $sourceMembers = @(Get-DirectoryGroupMembers -GroupName $testGroupName -Config $sourceConfig)
        $initialMemberCount = $sourceMembers.Count
        Write-Host "    Current members in Source: $initialMemberCount" -ForegroundColor Gray

        # Get current members of the test group in Target (before changes)
        $targetMembersBefore = @(Get-DirectoryGroupMembers -GroupName $testGroupName -Config $targetConfig)
        $targetMemberCountBefore = $targetMembersBefore.Count
        Write-Host "    Current members in Target: $targetMemberCountBefore" -ForegroundColor Gray

        # Baseline check: Source and Target should match before making changes
        if ($targetMemberCountBefore -ne $initialMemberCount) {
            throw "ForwardSync baseline failed: group '$testGroupName' has $targetMemberCountBefore members in Target but $initialMemberCount in Source before any changes were made. InitialSync did not produce a consistent state."
        }

        # Step 2.2: Find users to add and remove
        Write-Host "  Preparing membership changes..." -ForegroundColor Gray

        # Get all users from Source
        $allUsers = @(Get-DirectoryUserList -Config $sourceConfig | Where-Object {
            $_ -notmatch "^(Administrator|Guest|krbtgt)" -and
            $_ -notmatch "DNS"
        })

        if ($allUsers.Count -lt 2) {
            throw "Not enough users in Source for membership testing. Need at least 2 users."
        }

        # Build a HashSet of member usernames for O(1) lookups instead of O(N*M) linear scans.
        # sourceMembers contains DNs for OpenLDAP (uid=name,...) or plain names for Samba AD.
        $memberSet = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        foreach ($m in $sourceMembers) {
            if ($m -match "^(?:uid|cn|CN)=([^,]+),") {
                [void]$memberSet.Add($matches[1])
            } else {
                [void]$memberSet.Add($m)
            }
        }

        # Find 2 non-members (to add) and 1 member (to remove) — stop early
        $usersToAdd = @()
        $userToRemove = $null
        foreach ($user in $allUsers) {
            if ($memberSet.Contains($user)) {
                if ($null -eq $userToRemove) { $userToRemove = $user }
            } else {
                if ($usersToAdd.Count -lt 2) { $usersToAdd += $user }
            }
            if ($usersToAdd.Count -ge 2 -and $null -ne $userToRemove) { break }
        }

        Write-Host "    Users to add: $($usersToAdd -join ', ')" -ForegroundColor Yellow
        Write-Host "    User to remove: $userToRemove" -ForegroundColor Yellow

        # Step 2.3: Make membership changes in Source
        Write-Host "  Making membership changes in Source..." -ForegroundColor Gray

        $addedCount = 0
        foreach ($userToAdd in $usersToAdd) {
            $result = Add-DirectoryGroupMember -GroupName $testGroupName -MemberName $userToAdd -Config $sourceConfig
            Write-Host "    Added '$userToAdd' to '$testGroupName'" -ForegroundColor Green
            $addedCount++
        }

        $removedCount = 0
        if ($userToRemove) {
            $result = Remove-DirectoryGroupMember -GroupName $testGroupName -MemberName $userToRemove -Config $sourceConfig
            Write-Host "    Removed '$userToRemove' from '$testGroupName'" -ForegroundColor Green
            $removedCount++
        }

        # Verify changes in Source
        $sourceMembersAfterChange = @(Get-DirectoryGroupMembers -GroupName $testGroupName -Config $sourceConfig)
        $sourceMemberCountAfterChange = $sourceMembersAfterChange.Count
        $expectedSourceCount = $initialMemberCount + $addedCount - $removedCount
        Write-Host "    Source members after change: $sourceMemberCountAfterChange (expected: $expectedSourceCount)" -ForegroundColor Gray

        # Step 2.4: Run DELTA forward sync to propagate changes (more efficient than full)
        Write-Host "  Running delta forward sync to propagate changes..." -ForegroundColor Gray
        Invoke-DeltaForwardSync -Context "ForwardSync membership changes"

        # Step 2.5: Validate changes in Target AD
        Write-Host "  Validating changes in Target AD..." -ForegroundColor Gray

        # Check if the group exists in Target (with retry for consistency)
        if (-not (Test-DirectoryGroupExists -GroupName $testGroupName -Config $targetConfig)) {
            throw "Test group '$testGroupName' not found in Target after sync"
        }

        # Get members in Target after sync
        $targetMembersList = @(Get-DirectoryGroupMembers -GroupName $testGroupName -Config $targetConfig)
        $targetMemberCountAfter = $targetMembersList.Count

        Write-Host "    Target members after sync: $targetMemberCountAfter" -ForegroundColor Gray

        # Validate: Added users should be in Target
        $addValidationPassed = $true
        foreach ($addedUser in $usersToAdd) {
            if (Test-MemberInList -UserName $addedUser -MemberList $targetMembersList) {
                Write-Host "    Added user '$addedUser' found in Target group" -ForegroundColor Green
            }
            else {
                Write-Host "    Added user '$addedUser' NOT found in Target group" -ForegroundColor Red
                $addValidationPassed = $false
            }
        }

        # Validate: Removed user should NOT be in Target
        $removeValidationPassed = $true
        if ($userToRemove) {
            if (-not (Test-MemberInList -UserName $userToRemove -MemberList $targetMembersList)) {
                Write-Host "    Removed user '$userToRemove' is not in Target group" -ForegroundColor Green
            }
            else {
                Write-Host "    Removed user '$userToRemove' still in Target group" -ForegroundColor Red
                $removeValidationPassed = $false
            }
        }

        # Validate: Member count should match
        $countValidationPassed = ($targetMemberCountAfter -eq $sourceMemberCountAfterChange)
        if ($countValidationPassed) {
            Write-Host "    ✓ Member count matches between Source ($sourceMemberCountAfterChange) and Target ($targetMemberCountAfter)" -ForegroundColor Green
        }
        else {
            $deficit = $sourceMemberCountAfterChange - $targetMemberCountAfter
            Write-Host "    ✗ Member count mismatch: Source=$sourceMemberCountAfterChange, Target=$targetMemberCountAfter (deficit: $deficit)" -ForegroundColor Red
        }

        # Fail immediately with diagnostic detail - don't batch up multiple failures
        if (-not $addValidationPassed) {
            throw "ForwardSync failed: added user(s) not found in Target group '$testGroupName'"
        }
        if (-not $removeValidationPassed) {
            throw "ForwardSync failed: removed user '$userToRemove' still present in Target group '$testGroupName'"
        }
        if (-not $countValidationPassed) {
            $deficit = $sourceMemberCountAfterChange - $targetMemberCountAfter
            throw "ForwardSync failed: group '$testGroupName' has $targetMemberCountAfter members in Target but expected $sourceMemberCountAfterChange from Source (deficit: $deficit). This indicates the confirming sync incorrectly removed $deficit members via drift correction."
        }

        Write-Host "✓ ForwardSync test completed successfully" -ForegroundColor Green

        $testResults.Steps += "ForwardSync"
    }

    # Test 3: DetectDrift (Unauthorised changes in Target AD)
    if ($Step -eq "DetectDrift" -or $Step -eq "All") {
        Write-TestSection "Test 3: DetectDrift (Drift Detection)"

        Write-Host "This test validates that JIM detects unauthorised changes made directly in Target AD" -ForegroundColor Gray

        # Container names for Source and Target AD
        # Container names from config (set at script top)
        $sourceContainer = $sourceContainerName
        $targetContainer = $targetContainerName

        # Step 3.1: Find groups with members in Target AD for testing
        Write-Host "  Finding test groups in Target AD..." -ForegroundColor Gray

        $testGroups = @(Get-DirectoryGroupList -Config $targetConfig | Where-Object { $_ -match "^(Company-|Dept-|Location-|Project-)" })

        if ($testGroups.Count -lt 2) {
            throw "Not enough test groups in Target AD. Need at least 2 groups. Ensure InitialSync has been run."
        }

        # Find two groups with members for drift testing
        $driftGroup1 = $null  # Group to add an unauthorised member to
        $driftGroup2 = $null  # Group to remove an authorised member from
        $driftGroup1Members = @()
        $driftGroup2Members = @()

        foreach ($grp in $testGroups) {
            $grpName = $grp.Trim()
            $memberList = @(Get-DirectoryGroupMembers -GroupName $grpName -Config $targetConfig)
            if ($memberList.Count -gt 0) {
                if (-not $driftGroup1) {
                    $driftGroup1 = $grpName
                    $driftGroup1Members = $memberList
                }
                elseif (-not $driftGroup2 -and $grpName -ne $driftGroup1) {
                    $driftGroup2 = $grpName
                    $driftGroup2Members = $memberList
                    break
                }
            }
        }

        if (-not $driftGroup1 -or -not $driftGroup2) {
            throw "Could not find two groups with members in Target for drift testing."
        }

        Write-Host "    Drift test group 1: $driftGroup1 (members: $($driftGroup1Members.Count))" -ForegroundColor Cyan
        Write-Host "    Drift test group 2: $driftGroup2 (members: $($driftGroup2Members.Count))" -ForegroundColor Cyan

        # Step 3.2: Get all users in Target AD (to find a user NOT in driftGroup1)
        Write-Host "  Finding user to add to group (unauthorised addition)..." -ForegroundColor Gray

        $allUsers = @(Get-DirectoryUserList -Config $targetConfig | Where-Object {
            $_ -notmatch "^(Administrator|Guest|krbtgt)" -and
            $_ -notmatch "DNS"
        })

        # Find a user NOT in driftGroup1 to add (simulating unauthorised addition)
        $userToAddToDrift = $null
        foreach ($user in $allUsers) {
            if (-not (Test-MemberInList -UserName $user -MemberList $driftGroup1Members)) {
                $userToAddToDrift = $user.Trim()
                break
            }
        }

        if (-not $userToAddToDrift) {
            Write-Host "    ⚠ All users already in $driftGroup1, skipping unauthorised addition test" -ForegroundColor Yellow
        }
        else {
            Write-Host "    User to add (unauthorised): $userToAddToDrift" -ForegroundColor Yellow
        }

        # Find a user IN driftGroup2 to remove (simulating unauthorised removal)
        $userToRemoveFromDrift = $driftGroup2Members[0].Trim()
        Write-Host "    User to remove (unauthorised): $userToRemoveFromDrift from $driftGroup2" -ForegroundColor Yellow

        # Step 3.3: Record the EXPECTED state (from Source AD - the authoritative source)
        Write-Host "  Recording expected state from Source AD (authoritative)..." -ForegroundColor Gray

        $sourceGroup1MemberList = @(Get-DirectoryGroupMembers -GroupName $driftGroup1 -Config $sourceConfig)
        $sourceGroup1MemberCount = $sourceGroup1MemberList.Count

        $sourceGroup2MemberList = @(Get-DirectoryGroupMembers -GroupName $driftGroup2 -Config $sourceConfig)
        $sourceGroup2MemberCount = $sourceGroup2MemberList.Count

        Write-Host "    Source $driftGroup1 members (expected): $sourceGroup1MemberCount" -ForegroundColor Gray
        Write-Host "    Source $driftGroup2 members (expected): $sourceGroup2MemberCount" -ForegroundColor Gray

        # Step 3.4: Make UNAUTHORISED changes directly in Target (bypassing JIM)
        Write-Host "  Making unauthorised changes directly in Target..." -ForegroundColor Gray

        $driftAddSucceeded = $false
        $driftRemoveSucceeded = $false

        if ($userToAddToDrift) {
            try {
                Add-DirectoryGroupMember -GroupName $driftGroup1 -MemberName $userToAddToDrift -Config $targetConfig
                Write-Host "    Unauthorised addition: Added '$userToAddToDrift' to '$driftGroup1'" -ForegroundColor Yellow
                $driftAddSucceeded = $true
            }
            catch {
                Write-Host "    Failed to add user to group: $_" -ForegroundColor Yellow
            }
        }

        try {
            Remove-DirectoryGroupMember -GroupName $driftGroup2 -MemberName $userToRemoveFromDrift -Config $targetConfig
            Write-Host "    Unauthorised removal: Removed '$userToRemoveFromDrift' from '$driftGroup2'" -ForegroundColor Yellow
            $driftRemoveSucceeded = $true
        }
        catch {
            Write-Host "    Failed to remove user from group: $_" -ForegroundColor Yellow
        }

        if (-not $driftAddSucceeded -and -not $driftRemoveSucceeded) {
            throw "Could not make any unauthorised changes in Target AD for drift testing"
        }

        # Step 3.5: Verify the changes are visible in Target AD
        Write-Host "  Verifying unauthorised changes in Target AD..." -ForegroundColor Gray

        $targetGroup1MemberCountAfterDrift = @(Get-DirectoryGroupMembers -GroupName $driftGroup1 -Config $targetConfig).Count
        $targetGroup2MemberCountAfterDrift = @(Get-DirectoryGroupMembers -GroupName $driftGroup2 -Config $targetConfig).Count

        Write-Host "    Target $driftGroup1 members (after drift): $targetGroup1MemberCountAfterDrift (expected: $sourceGroup1MemberCount)" -ForegroundColor Gray
        Write-Host "    Target $driftGroup2 members (after drift): $targetGroup2MemberCountAfterDrift (expected: $sourceGroup2MemberCount)" -ForegroundColor Gray

        # Validate drift is visible in AD
        $driftDetectedInAD = $false
        if ($driftAddSucceeded -and $targetGroup1MemberCountAfterDrift -gt $sourceGroup1MemberCount) {
            Write-Host "    ✓ Drift confirmed: $driftGroup1 has extra member in Target" -ForegroundColor Green
            $driftDetectedInAD = $true
        }
        if ($driftRemoveSucceeded -and $targetGroup2MemberCountAfterDrift -lt $sourceGroup2MemberCount) {
            Write-Host "    ✓ Drift confirmed: $driftGroup2 is missing member in Target" -ForegroundColor Green
            $driftDetectedInAD = $true
        }

        if (-not $driftDetectedInAD) {
            throw "DetectDrift failed: drift changes not visible in Target AD after direct modification. Cannot proceed with drift detection test."
        }

        # Step 3.6: Delta Import from Target AD to detect the drift
        # Delta Import picks up the unauthorised changes made directly in Target AD
        Write-Host "  Running Delta Import on Target AD (to import drifted state)..." -ForegroundColor Gray

        $targetImportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaImportProfile.id -Wait -PassThru
        if ($isOpenLDAP) {
            Assert-ActivitySuccess -ActivityId $targetImportResult.activityId -Name "Target Delta Import (detect drift)" `
                -AllowWarnings -AllowedWarningTypes @('DeltaImportFallbackToFullImport')
        } else {
            Assert-ActivitySuccess -ActivityId $targetImportResult.activityId -Name "Target Delta Import (detect drift)"
        }
        Start-Sleep -Seconds $WaitSeconds

        # Step 3.7: Delta Sync on Target AD to evaluate the drift against sync rules
        # This is where JIM should:
        # 1. Compare the imported CSO attribute values against what the sync rules say they should be
        # 2. Determine that the Target AD group memberships don't match the authoritative Source state
        # 3. Stage pending exports to correct the drift (re-assert the desired state)
        Write-Host "  Running Delta Sync on Target AD (to evaluate drift against sync rules)..." -ForegroundColor Gray

        $targetSyncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $targetSyncResult.activityId -Name "Target Delta Sync (evaluate drift)" -AllowWarnings
        Start-Sleep -Seconds $WaitSeconds

        # Step 3.8: Validate drift detection and pending exports
        Write-Host "  Validating drift detection and pending exports..." -ForegroundColor Gray

        # Store drift context for ReassertState step
        $script:driftContext = @{
            DriftGroup1 = $driftGroup1
            DriftGroup2 = $driftGroup2
            UserAddedToDriftGroup1 = $userToAddToDrift
            UserRemovedFromDriftGroup2 = $userToRemoveFromDrift
            SourceGroup1MemberCount = $sourceGroup1MemberCount
            SourceGroup2MemberCount = $sourceGroup2MemberCount
            DriftAddSucceeded = $driftAddSucceeded
            DriftRemoveSucceeded = $driftRemoveSucceeded
        }

        # The key validation: After Delta Import and Delta Sync on Target AD:
        # 1. Delta Import updated the Target CSO attribute values to reflect the drifted state
        # 2. Delta Sync evaluated these CSO changes against sync rules
        # 3. Delta Sync should have determined that the Target state doesn't match the
        #    authoritative Source state and staged pending exports to correct the drift
        #
        # This is how MIM 2016 works - the sync engine evaluates inbound changes and
        # determines if corrective exports are needed based on the configured sync rules.

        $validations = @()

        if ($driftDetectedInAD) {
            $validations += @{ Name = "Drift visible in Target AD"; Success = $true }
            Write-Host "    ✓ Drift is visible in Target AD" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Drift visible in Target AD"; Success = $false }
            Write-Host "    ✗ Drift not visible in Target AD" -ForegroundColor Red
        }

        # Delta Import completed - JIM has imported the drifted state
        $importActivity = Get-JIMActivity -Id $targetImportResult.activityId

        if ($importActivity.status -eq "Complete" -or $importActivity.status -eq "CompleteWithWarning") {
            $validations += @{ Name = "Target Delta Import completed"; Success = $true }
            Write-Host "    ✓ Target Delta Import completed" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Target Delta Import completed"; Success = $false }
            Write-Host "    ✗ Target Delta Import failed: $($importActivity.status)" -ForegroundColor Red
        }

        # Delta Sync completed - JIM has evaluated the drift against sync rules
        $syncActivity = Get-JIMActivity -Id $targetSyncResult.activityId

        if ($syncActivity.status -eq "Complete" -or $syncActivity.status -eq "CompleteWithWarning") {
            $validations += @{ Name = "Target Delta Sync completed"; Success = $true }
            Write-Host "    ✓ Target Delta Sync completed" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Target Delta Sync completed"; Success = $false }
            Write-Host "    ✗ Target Delta Sync failed: $($syncActivity.status)" -ForegroundColor Red
        }

        # KEY VALIDATION: Check that JIM has staged pending exports to correct the drift
        # After Delta Sync evaluates the drifted CSOs against sync rules, it should
        # determine that corrective exports are needed and stage them as pending exports.
        Write-Host "  Checking for pending exports to correct drift..." -ForegroundColor Gray

        # Refresh the connected system to get current pending export count
        $connectedSystems = Get-JIMConnectedSystem
        $targetSystemRefreshed = $connectedSystems | Where-Object { $_.name -eq $targetSystemName }
        $pendingExportCount = $targetSystemRefreshed.pendingExportObjectsCount

        Write-Host "    Pending exports for Target AD: $pendingExportCount" -ForegroundColor Cyan

        # We expect pending exports to correct the drift (at minimum, exports for the groups we modified)
        $expectedMinPendingExports = 0
        if ($driftAddSucceeded) { $expectedMinPendingExports++ }
        if ($driftRemoveSucceeded) { $expectedMinPendingExports++ }

        if ($pendingExportCount -ge $expectedMinPendingExports -and $expectedMinPendingExports -gt 0) {
            $validations += @{ Name = "Pending exports staged to correct drift"; Success = $true }
            Write-Host "    ✓ JIM has staged $pendingExportCount pending export(s) to correct drift" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Pending exports staged to correct drift"; Success = $false }
            Write-Host "    ✗ Expected at least $expectedMinPendingExports pending export(s) to correct drift, found $pendingExportCount" -ForegroundColor Red
        }

        # Overall success if all validations passed
        $allValidationsPassed = @($validations | Where-Object { -not $_.Success }).Count -eq 0

        if ($allValidationsPassed) {
            Write-Host ""
            Write-Host "✓ DetectDrift test completed successfully" -ForegroundColor Green
            Write-Host "  Drift has been detected and JIM has staged corrective exports" -ForegroundColor Gray
            Write-Host "  Run ReassertState to execute the exports and verify correction" -ForegroundColor Gray
        }
        else {
            throw "DetectDrift validation failed - drift was not detected or corrective exports were not staged"
        }

        $testResults.Steps += "DetectDrift"
    }

    # Test 4: ReassertState (Force state reassertion)
    if ($Step -eq "ReassertState" -or $Step -eq "All") {
        Write-TestSection "Test 4: ReassertState (State Reassertion)"

        Write-Host "This test validates that JIM reasserts Source AD membership to Target AD after drift" -ForegroundColor Gray

        # Container names for Source and Target AD
        # Container names from config (set at script top)
        $sourceContainer = $sourceContainerName
        $targetContainer = $targetContainerName

        # Step 4.1: Get drift context from DetectDrift step (or discover groups if run independently)
        if (-not $script:driftContext) {
            Write-Host "  DetectDrift context not available, discovering groups..." -ForegroundColor Yellow

            # Find groups to validate (same logic as DetectDrift)
            $testGroups = @(Get-DirectoryGroupList -Config $targetConfig | Where-Object { $_ -match "^(Company-|Dept-|Location-|Project-)" })

            if ($testGroups.Count -lt 2) {
                throw "Not enough test groups in Target. Ensure InitialSync has been run."
            }

            # Use first two groups with members
            $driftGroup1 = $null
            $driftGroup2 = $null

            foreach ($grp in $testGroups) {
                $grpName = $grp.Trim()
                $memberList = @(Get-DirectoryGroupMembers -GroupName $grpName -Config $targetConfig)
                if ($memberList.Count -gt 0) {
                    if (-not $driftGroup1) {
                        $driftGroup1 = $grpName
                    }
                    elseif (-not $driftGroup2 -and $grpName -ne $driftGroup1) {
                        $driftGroup2 = $grpName
                        break
                    }
                }
            }

            if (-not $driftGroup1 -or -not $driftGroup2) {
                throw "Could not find two groups with members in Target."
            }

            # Get expected member counts from Source
            $sourceGroup1MemberCount = @(Get-DirectoryGroupMembers -GroupName $driftGroup1 -Config $sourceConfig).Count
            $sourceGroup2MemberCount = @(Get-DirectoryGroupMembers -GroupName $driftGroup2 -Config $sourceConfig).Count

            $script:driftContext = @{
                DriftGroup1 = $driftGroup1
                DriftGroup2 = $driftGroup2
                SourceGroup1MemberCount = $sourceGroup1MemberCount
                SourceGroup2MemberCount = $sourceGroup2MemberCount
            }
        }

        $driftGroup1 = $script:driftContext.DriftGroup1
        $driftGroup2 = $script:driftContext.DriftGroup2
        $expectedGroup1MemberCount = $script:driftContext.SourceGroup1MemberCount
        $expectedGroup2MemberCount = $script:driftContext.SourceGroup2MemberCount

        Write-Host "  Drift context:" -ForegroundColor Gray
        Write-Host "    Group 1: $driftGroup1 (expected members: $expectedGroup1MemberCount)" -ForegroundColor Cyan
        Write-Host "    Group 2: $driftGroup2 (expected members: $expectedGroup2MemberCount)" -ForegroundColor Cyan

        # Step 4.2: Record Target AD state BEFORE reassertion
        Write-Host "  Recording Target AD state before reassertion..." -ForegroundColor Gray

        $targetGroup1MemberCountBefore = @(Get-DirectoryGroupMembers -GroupName $driftGroup1 -Config $targetConfig).Count
        $targetGroup2MemberCountBefore = @(Get-DirectoryGroupMembers -GroupName $driftGroup2 -Config $targetConfig).Count

        Write-Host "    Target $driftGroup1 members (before): $targetGroup1MemberCountBefore" -ForegroundColor Gray
        Write-Host "    Target $driftGroup2 members (before): $targetGroup2MemberCountBefore" -ForegroundColor Gray

        # Step 4.3: Execute the corrective exports to reassert state
        # Drift detection has already created the pending exports - we just need to:
        # 1. Export to Target (executes the corrective pending exports)
        # 2. Confirming Import (confirms the exports were applied)
        # 3. Delta Sync (clears pending export state)
        # Note: No need to re-import/sync from Source - the MVO state is already correct
        Write-Host "  Executing corrective exports to reassert state..." -ForegroundColor Gray

        # Export to Target
        Write-Host "    Exporting corrections to Target AD..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Target Export (reassert state)"
        Start-Sleep -Seconds $WaitSeconds

        # Confirming Import
        Write-Host "    Running confirming import on Target AD..." -ForegroundColor Gray
        $confirmResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaImportProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $confirmResult.activityId -Name "Target Confirming Import (reassert state)"
        Start-Sleep -Seconds $WaitSeconds

        # Delta Sync to confirm exports
        Write-Host "    Running delta sync on Target AD (confirm exports)..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetDeltaSyncProfile.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Target Delta Sync (confirm exports)"
        Start-Sleep -Seconds $WaitSeconds

        # Step 4.4: Validate state reassertion
        Write-Host "  Validating state reassertion..." -ForegroundColor Gray

        $targetGroup1MemberList = @(Get-DirectoryGroupMembers -GroupName $driftGroup1 -Config $targetConfig)
        $targetGroup1MemberCountAfter = $targetGroup1MemberList.Count

        $targetGroup2MemberList = @(Get-DirectoryGroupMembers -GroupName $driftGroup2 -Config $targetConfig)
        $targetGroup2MemberCountAfter = $targetGroup2MemberList.Count

        Write-Host "    Target $driftGroup1 members (after): $targetGroup1MemberCountAfter (expected: $expectedGroup1MemberCount)" -ForegroundColor Gray
        Write-Host "    Target $driftGroup2 members (after): $targetGroup2MemberCountAfter (expected: $expectedGroup2MemberCount)" -ForegroundColor Gray

        # Validate: Target groups should now match Source groups
        $validations = @()

        if ($targetGroup1MemberCountAfter -eq $expectedGroup1MemberCount) {
            $validations += @{ Name = "$driftGroup1 member count matches Source"; Success = $true }
            Write-Host "    ✓ $driftGroup1 members corrected: $targetGroup1MemberCountAfter (matches Source)" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "$driftGroup1 member count matches Source"; Success = $false }
            Write-Host "    ✗ $driftGroup1 member count mismatch: Target=$targetGroup1MemberCountAfter, Expected=$expectedGroup1MemberCount" -ForegroundColor Red
        }

        if ($targetGroup2MemberCountAfter -eq $expectedGroup2MemberCount) {
            $validations += @{ Name = "$driftGroup2 member count matches Source"; Success = $true }
            Write-Host "    ✓ $driftGroup2 members corrected: $targetGroup2MemberCountAfter (matches Source)" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "$driftGroup2 member count matches Source"; Success = $false }
            Write-Host "    ✗ $driftGroup2 member count mismatch: Target=$targetGroup2MemberCountAfter, Expected=$expectedGroup2MemberCount" -ForegroundColor Red
        }

        # Validate that unauthorised additions were removed and removals were restored
        if ($script:driftContext.UserAddedToDriftGroup1) {
            $userAddedToDrift = $script:driftContext.UserAddedToDriftGroup1
            if (-not (Test-MemberInList -UserName $userAddedToDrift -MemberList $targetGroup1MemberList)) {
                $validations += @{ Name = "Unauthorised addition removed from $driftGroup1"; Success = $true }
                Write-Host "    ✓ Unauthorised member '$userAddedToDrift' removed from $driftGroup1" -ForegroundColor Green
            }
            else {
                $validations += @{ Name = "Unauthorised addition removed from $driftGroup1"; Success = $false }
                Write-Host "    ✗ Unauthorised member '$userAddedToDrift' still in $driftGroup1" -ForegroundColor Red
            }
        }

        if ($script:driftContext.UserRemovedFromDriftGroup2) {
            $userRemovedFromDrift = $script:driftContext.UserRemovedFromDriftGroup2
            if ($userRemovedFromDrift -in $targetGroup2MemberList) {
                $validations += @{ Name = "Unauthorised removal restored in $driftGroup2"; Success = $true }
                Write-Host "    ✓ Member '$userRemovedFromDrift' restored to $driftGroup2" -ForegroundColor Green
            }
            else {
                $validations += @{ Name = "Unauthorised removal restored in $driftGroup2"; Success = $false }
                Write-Host "    ✗ Member '$userRemovedFromDrift' not restored to $driftGroup2" -ForegroundColor Red
            }
        }

        # Overall success if all validations passed
        $allValidationsPassed = @($validations | Where-Object { -not $_.Success }).Count -eq 0

        if ($allValidationsPassed) {
            Write-Host ""
            Write-Host "✓ ReassertState test completed successfully" -ForegroundColor Green
            Write-Host "  JIM has corrected the drift and restored authoritative Source state" -ForegroundColor Gray
        }
        else {
            throw "ReassertState validation failed - drift was not corrected"
        }

        $testResults.Steps += "ReassertState"
    }

    # Test 5: NewGroup (Create new group in Source, sync to Target)
    if ($Step -eq "NewGroup" -or $Step -eq "All") {
        Write-TestSection "Test 5: NewGroup (New Group Provisioning)"

        Write-Host "This test validates that new groups created in Source AD are provisioned to Target AD" -ForegroundColor Gray

        # Container names for Source and Target AD
        # Container names from config (set at script top)
        $sourceContainer = $sourceContainerName
        $targetContainer = $targetContainerName

        # Test group details
        $newGroupName = "Project-Scenario8Test"
        $newGroupDescription = "Test group created by Scenario 8 NewGroup test"

        # Step 5.1: Create new group in Source AD
        Write-Host "  Creating new group '$newGroupName' in Source AD..." -ForegroundColor Gray

        # First, delete the group if it exists from a previous run
        Remove-DirectoryGroup -GroupName $newGroupName -Config $sourceConfig 2>$null | Out-Null
        Remove-DirectoryGroup -GroupName $newGroupName -Config $targetConfig 2>$null | Out-Null

        # Create the group in Source
        # For OpenLDAP: need an initial member DN for groupOfNames MUST constraint
        $allUsers = @(Get-DirectoryUserList -Config $sourceConfig | Where-Object {
            $_ -notmatch "^(Administrator|Guest|krbtgt)" -and $_ -notmatch "DNS"
        })
        $initialMemberDn = $null
        if ($isOpenLDAP -and $allUsers.Count -gt 0) {
            $firstUser = Get-LDAPUser -UserIdentifier $allUsers[0] -DirectoryConfig $sourceConfig
            if ($firstUser) { $initialMemberDn = $firstUser['dn'] }
        }

        $createResult = New-DirectoryGroup -GroupName $newGroupName -Description $newGroupDescription -Config $sourceConfig -InitialMemberDn $initialMemberDn
        Write-Host "    Created group '$newGroupName' in Source" -ForegroundColor Green

        # Step 5.2: Add members to the new group
        Write-Host "  Adding members to new group..." -ForegroundColor Gray

        # Add up to 3 members to the new group
        $membersToAdd = @()
        $addedCount = 0
        foreach ($user in $allUsers) {
            if ($addedCount -ge 3) { break }
            $userName = $user.Trim()
            # For OpenLDAP, skip the initial member (already in the group)
            if ($isOpenLDAP -and $addedCount -eq 0 -and $userName -eq $allUsers[0]) {
                $membersToAdd += $userName
                $addedCount++
                continue
            }
            try {
                Add-DirectoryGroupMember -GroupName $newGroupName -MemberName $userName -Config $sourceConfig
                $membersToAdd += $userName
                $addedCount++
            }
            catch {
                Write-Verbose "    Could not add $userName`: $_"
            }
        }

        Write-Host "    Added $addedCount members: $($membersToAdd -join ', ')" -ForegroundColor Cyan

        # Step 5.3: Run forward sync to provision the new group to Target
        Write-Host "  Running delta forward sync to provision new group..." -ForegroundColor Gray
        Invoke-DeltaForwardSync -Context "NewGroup"

        # Step 5.4: Validate new group in Target AD
        Write-Host "  Validating new group in Target AD..." -ForegroundColor Gray

        $validations = @()

        # Check if group exists in Target (with retry for consistency)
        if (Test-DirectoryGroupExists -GroupName $newGroupName -Config $targetConfig) {
            $targetGroupInfo = Get-LDAPGroup -GroupName $newGroupName -DirectoryConfig $targetConfig
        }
        if ($targetGroupInfo) {
            $validations += @{ Name = "Group exists in Target"; Success = $true }
            Write-Host "    Group '$newGroupName' exists in Target" -ForegroundColor Green

            # Verify description attribute
            $targetDesc = if ($targetGroupInfo.ContainsKey('description')) { $targetGroupInfo['description'] } else { "" }
            if ($targetDesc -eq $newGroupDescription) {
                $validations += @{ Name = "Group description correct"; Success = $true }
                Write-Host "    Group description is correct" -ForegroundColor Green
            }
            else {
                $validations += @{ Name = "Group description correct"; Success = $false }
                Write-Host "    Group description not found or incorrect in Target" -ForegroundColor Red
                Write-Host "      Expected: '$newGroupDescription'" -ForegroundColor Red
                Write-Host "      Actual: '$targetDesc'" -ForegroundColor Red
            }
        }
        else {
            $validations += @{ Name = "Group exists in Target"; Success = $false }
            Write-Host "    Group '$newGroupName' NOT found in Target" -ForegroundColor Red
        }

        # Check members in Target
        $targetMemberList = @(Get-DirectoryGroupMembers -GroupName $newGroupName -Config $targetConfig)
        $targetMemberCount = $targetMemberList.Count

        if ($targetMemberCount -eq $addedCount) {
            $validations += @{ Name = "Group member count matches"; Success = $true }
            Write-Host "    ✓ Group has $targetMemberCount members (matches Source)" -ForegroundColor Green
        }
        elseif ($targetMemberCount -gt 0) {
            $deficit = $addedCount - $targetMemberCount
            $validations += @{ Name = "Group member count matches"; Success = $false }
            Write-Host "    ✗ Group has $targetMemberCount members (expected $addedCount, deficit: $deficit)" -ForegroundColor Red
        }
        else {
            $validations += @{ Name = "Group member count matches"; Success = $false }
            Write-Host "    ✗ Group has no members in Target AD" -ForegroundColor Red
        }

        # Store the new group name for DeleteGroup test
        $script:newGroupContext = @{
            GroupName = $newGroupName
            MembersAdded = $membersToAdd
        }

        # Overall success if group exists (members may take additional sync cycles)
        $allValidationsPassed = @($validations | Where-Object { -not $_.Success }).Count -eq 0

        if ($allValidationsPassed) {
            Write-Host ""
            Write-Host "✓ NewGroup test completed successfully" -ForegroundColor Green
            Write-Host "  New group '$newGroupName' provisioned to Target AD" -ForegroundColor Gray
        }
        else {
            throw "NewGroup validation failed"
        }

        $testResults.Steps += "NewGroup"
    }

    # Test 6: DeleteGroup (Delete group from Source, remove from Target)
    if ($Step -eq "DeleteGroup" -or $Step -eq "All") {
        Write-TestSection "Test 6: DeleteGroup (Group Deletion with WhenAuthoritativeSourceDisconnected)"

        Write-Host "This test validates the WhenAuthoritativeSourceDisconnected deletion rule:" -ForegroundColor Gray
        Write-Host "  - When a group is deleted from Source AD (authoritative source)" -ForegroundColor Gray
        Write-Host "  - The MVO is marked for deletion (LastConnectorDisconnectedDate is set)" -ForegroundColor Gray
        Write-Host "  - Housekeeping triggers deprovisioning from Target AD" -ForegroundColor Gray
        Write-Host ""

        # Container names for Source and Target AD
        # Container names from config (set at script top)
        $sourceContainer = $sourceContainerName
        $targetContainer = $targetContainerName

        # Step 6.1: Determine which group to delete
        $groupToDelete = $null

        # Prefer the group created in NewGroup test if available
        if ($script:newGroupContext -and $script:newGroupContext.GroupName) {
            $groupToDelete = $script:newGroupContext.GroupName
            Write-Host "  Using group from NewGroup test: $groupToDelete" -ForegroundColor Cyan
        }
        else {
            # Find a project group to delete (least impactful)
            Write-Host "  NewGroup context not available, finding a project group to delete..." -ForegroundColor Yellow

            $allSourceGroups = @(Get-DirectoryGroupList -Config $sourceConfig)
            $projectGroups = @($allSourceGroups | Where-Object { $_ -match "^Project-" })

            if ($projectGroups.Count -gt 0) {
                $groupToDelete = $projectGroups[0].Trim()
            }
            else {
                # If no project groups, find any test group
                $testGroups = @($allSourceGroups | Where-Object { $_ -match "^(Company-|Dept-|Location-)" })
                if ($testGroups.Count -gt 0) {
                    $groupToDelete = $testGroups[0].Trim()
                }
            }
        }

        if (-not $groupToDelete) {
            throw "Could not find a group to delete. Ensure InitialSync or NewGroup has been run."
        }

        Write-Host "  Group to delete: $groupToDelete" -ForegroundColor Yellow

        # Step 6.2: Verify group exists in both Source and Target before deletion
        Write-Host "  Verifying group exists in both Source and Target AD..." -ForegroundColor Gray

        if (-not (Test-LDAPGroupExists -GroupName $groupToDelete -DirectoryConfig $sourceConfig)) {
            throw "Group '$groupToDelete' does not exist in Source"
        }
        Write-Host "    Group exists in Source" -ForegroundColor Green

        $groupExistsInTarget = Test-LDAPGroupExists -GroupName $groupToDelete -DirectoryConfig $targetConfig
        if ($groupExistsInTarget) {
            Write-Host "    Group exists in Target" -ForegroundColor Green
        }
        else {
            Write-Host "    Group does not exist in Target (may not have synced yet)" -ForegroundColor Yellow
        }

        # Step 6.3: Delete the group from Source
        Write-Host "  Deleting group '$groupToDelete' from Source..." -ForegroundColor Gray

        Remove-DirectoryGroup -GroupName $groupToDelete -Config $sourceConfig
        Write-Host "    Group deleted from Source" -ForegroundColor Green

        # Step 6.4: Get MVO info BEFORE deletion sync (to verify it exists and get its ID)
        Write-Host "  Looking up Group MVO before deletion..." -ForegroundColor Gray

        # Search for the group MVO by Account Name (sAMAccountName) using the new attribute filter
        # NOTE: We can't search by Display Name because groups created via samba-tool/ldapadd
        # don't have the displayName LDAP attribute set - only sAMAccountName and cn are populated.
        # We also request Account Name to be included in the response for display purposes
        $groupMvo = Get-JIMMetaverseObject -ObjectTypeName "Group" -AttributeName "Account Name" -AttributeValue $groupToDelete -Attributes "Account Name" | Select-Object -First 1
        $groupMvoId = $null

        if ($groupMvo) {
            $groupMvoId = $groupMvo.id
            Write-Host "    ✓ Found Group MVO: $groupMvoId" -ForegroundColor Green
            # Note: Account Name is in the Attributes property (PSCustomObject from JSON)
            # Access using dot notation with quotes for property names containing spaces
            $accountName = if ($groupMvo.attributes -and $groupMvo.attributes.'Account Name') { $groupMvo.attributes.'Account Name' } else { $groupMvo.displayName }
            Write-Host "      Account Name: $accountName" -ForegroundColor Gray

            # Re-fetch the full MVO by ID to check lastConnectorDisconnectedDate
            # (the list/filter endpoint returns headers without this property)
            $groupMvoFull = Get-JIMMetaverseObject -Id $groupMvoId
            if ($groupMvoFull -and $groupMvoFull.lastConnectorDisconnectedDate) {
                Write-Host "    ⚠ MVO already has LastConnectorDisconnectedDate set (unexpected)" -ForegroundColor Yellow
            }
        }
        else {
            Write-Host "    ⚠ Could not find Group MVO with Account Name '$groupToDelete'" -ForegroundColor Yellow
            # List available groups for debugging (include Account Name in response)
            $allGroups = Get-JIMMetaverseObject -ObjectTypeName "Group" -Attributes "Account Name"
            if ($allGroups) {
                Write-Host "      Available groups:" -ForegroundColor Gray
                $allGroups | Select-Object -First 5 | ForEach-Object {
                    $name = if ($_.attributes -and $_.attributes.'Account Name') { $_.attributes.'Account Name' } else { $_.displayName }
                    Write-Host "        - $name" -ForegroundColor Gray
                }
            }
        }

        # Step 6.5: Run forward sync to propagate the deletion
        # With DeletionGracePeriod = Zero, the MVO will be deleted SYNCHRONOUSLY during sync
        # (not deferred to housekeeping). Delete pending exports are created for target CSOs.
        Write-Host "  Running delta forward sync to propagate deletion..." -ForegroundColor Gray
        Invoke-DeltaForwardSync -Context "DeleteGroup"

        # Step 6.6: Validate MVO is deleted synchronously (0-grace-period immediate deletion)
        Write-Host "  Validating MVO deletion state..." -ForegroundColor Gray

        $validations = @()
        $mvoDeleted = $false
        $targetGroupDeleted = $false

        if ($groupMvoId) {
            # Re-fetch the MVO to check its state after sync
            # Use try/catch because with 0-grace-period, the MVO is deleted synchronously
            # and the API will return 404 (which throws an exception in PowerShell)
            try {
                $mvoAfterSync = Get-JIMMetaverseObject -Id $groupMvoId -ErrorAction Stop
            }
            catch {
                # MVO not found = deleted synchronously (expected with grace period = 0)
                $mvoAfterSync = $null
            }

            if ($mvoAfterSync) {
                # MVO still exists - unexpected with 0-grace-period (should be deleted synchronously)
                # This could happen if:
                # 1. The sync didn't process the obsolete CSO yet
                # 2. The deletion rule wasn't triggered
                if ($mvoAfterSync.lastConnectorDisconnectedDate) {
                    # MVO was marked but not deleted - this indicates grace period > 0 behaviour
                    $validations += @{ Name = "MVO deleted synchronously (0-grace-period)"; Success = $false }
                    Write-Host "    ⚠ MVO marked for deletion but not deleted (unexpected with grace period = 0)" -ForegroundColor Yellow
                    Write-Host "      LastConnectorDisconnectedDate = $($mvoAfterSync.lastConnectorDisconnectedDate)" -ForegroundColor Yellow
                }
                else {
                    $validations += @{ Name = "MVO deleted synchronously (0-grace-period)"; Success = $false }
                    Write-Host "    ✗ MVO still exists and NOT marked for deletion" -ForegroundColor Red
                    Write-Host "      Expected: MVO to be deleted synchronously with grace period = 0" -ForegroundColor Yellow
                }
            }
            else {
                # MVO was deleted synchronously during sync (expected with grace period = 0)
                $mvoDeleted = $true
                $validations += @{ Name = "MVO deleted synchronously (0-grace-period)"; Success = $true }
                Write-Host "    ✓ MVO deleted synchronously during sync (immediate deletion)" -ForegroundColor Green
                Write-Host "      This validates synchronous deletion for 0-grace-period MVOs" -ForegroundColor Cyan
            }
        }
        else {
            Write-Host "    ⚠ Skipping MVO validation (MVO ID not found)" -ForegroundColor Yellow
        }

        # NOTE: Export to Target AD is already included in Invoke-DeltaForwardSync (step 3)
        # so we don't need to run it again here. The delete pending export created during
        # synchronous MVO deletion is executed as part of the forward sync cycle.

        # Check if group is deleted from Target AD
        $groupStillExists = Test-LDAPGroupExists -GroupName $groupToDelete -DirectoryConfig $targetConfig
        if (-not $groupStillExists) {
            $targetGroupDeleted = $true
            Write-Host "    ✓ Group '$groupToDelete' deleted from Target AD" -ForegroundColor Green
        }
        else {
            # Group still exists - try one more export cycle
            Write-Host "    Group still exists in Target AD, trying confirming import + export..." -ForegroundColor Yellow
            if ($targetImportProfile -and $targetExportProfile) {
                Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetImportProfile.id -Wait
                Start-Sleep -Seconds 1
                Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExportProfile.id -Wait
                Start-Sleep -Seconds 2

                $retryGroupExists = Test-LDAPGroupExists -GroupName $groupToDelete -DirectoryConfig $targetConfig
                if (-not $retryGroupExists) {
                    $targetGroupDeleted = $true
                    Write-Host "    Group '$groupToDelete' deleted from Target after retry" -ForegroundColor Green
                }
            }
        }

        # Final validation of Target AD deletion
        if ($targetGroupDeleted) {
            $validations += @{ Name = "Group deleted from Target AD"; Success = $true }
        }
        else {
            # One more sync cycle might be needed - run export to Target
            Write-Host "    Group still exists in Target AD after waiting. Running Target export..." -ForegroundColor Yellow
            if ($targetExportProfile) {
                Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExportProfile.id -Wait
                Start-Sleep -Seconds 2

                # Check one more time
                $finalCheckExists = Test-LDAPGroupExists -GroupName $groupToDelete -DirectoryConfig $targetConfig
                if (-not $finalCheckExists) {
                    $targetGroupDeleted = $true
                    $validations += @{ Name = "Group deleted from Target AD"; Success = $true }
                    Write-Host "    ✓ Group '$groupToDelete' deleted from Target AD after export" -ForegroundColor Green
                }
                else {
                    $validations += @{ Name = "Group deleted from Target AD"; Success = $false }
                    Write-Host "    ✗ Group '$groupToDelete' still exists in Target AD after export" -ForegroundColor Red
                }
            }
            else {
                $validations += @{ Name = "Group deleted from Target AD"; Success = $false }
                Write-Host "    ✗ Group '$groupToDelete' still exists in Target AD (Target Export profile not available)" -ForegroundColor Red
            }
        }

        # Verify group is no longer in Source AD (double-check)
        $sourceGroupStillExists = Test-LDAPGroupExists -GroupName $groupToDelete -DirectoryConfig $sourceConfig
        if (-not $sourceGroupStillExists) {
            $validations += @{ Name = "Group confirmed deleted from Source AD"; Success = $true }
            Write-Host "    ✓ Group confirmed deleted from Source AD" -ForegroundColor Green
        }
        else {
            $validations += @{ Name = "Group confirmed deleted from Source AD"; Success = $false }
            Write-Host "    ✗ Group still exists in Source AD (deletion failed)" -ForegroundColor Red
        }

        # Overall success if group is deleted from both Source and Target
        $allValidationsPassed = @($validations | Where-Object { -not $_.Success }).Count -eq 0

        if ($allValidationsPassed) {
            Write-Host ""
            Write-Host "✓ DeleteGroup test completed successfully" -ForegroundColor Green
            Write-Host "  Group '$groupToDelete' deleted from both Source and Target AD" -ForegroundColor Gray
            Write-Host ""
            Write-Host "  Key validations:" -ForegroundColor Cyan
            Write-Host "    - WhenAuthoritativeSourceDisconnected rule triggered MVO deletion" -ForegroundColor Gray
            Write-Host "    - MVO deleted synchronously (0-grace-period immediate deletion)" -ForegroundColor Gray
            Write-Host "    - Delete pending exports deprovisioned the group from Target AD" -ForegroundColor Gray
        }
        else {
            # Check what failed
            $sourceDeleted = @($validations | Where-Object { $_.Name -eq "Group confirmed deleted from Source AD" -and $_.Success }).Count -gt 0
            $targetDeleted = @($validations | Where-Object { $_.Name -eq "Group deleted from Target AD" -and $_.Success }).Count -gt 0

            if ($sourceDeleted -and $mvoDeleted -and -not $targetDeleted) {
                Write-Host ""
                Write-Host "✗ DeleteGroup test failed" -ForegroundColor Red
                Write-Host "  Group deleted from Source AD" -ForegroundColor Gray
                Write-Host "  MVO deleted synchronously (WhenAuthoritativeSourceDisconnected working)" -ForegroundColor Green
                Write-Host "  Group NOT deleted from Target AD (deprovisioning not working)" -ForegroundColor Red
                throw "DeleteGroup test failed: Target AD deprovisioning did not complete"
            }
            elseif (-not $mvoDeleted) {
                Write-Host ""
                Write-Host "✗ DeleteGroup test failed" -ForegroundColor Red
                Write-Host "  MVO was NOT deleted after authoritative source disconnect" -ForegroundColor Red
                Write-Host "  With DeletionGracePeriod = Zero, MVO should be deleted synchronously during sync" -ForegroundColor Yellow
                throw "DeleteGroup test failed: MVO was not deleted synchronously"
            }
            else {
                throw "DeleteGroup validation failed"
            }
        }

        $testResults.Steps += "DeleteGroup"
    }

    Write-TestSection "Test Execution Summary"
    Write-Host "Tests executed: $($testResults.Steps.Count)" -ForegroundColor Gray
    Write-Host "Steps: $($testResults.Steps -join ', ')" -ForegroundColor Gray

    $testResults.Success = $true
    Write-Host ""
    Write-Host "✓ Scenario 8 test execution completed successfully" -ForegroundColor Green
    exit 0

}
catch {
    Write-Host ""
    Write-Failure "Scenario 8 test failed: $($_.Exception.Message)"
    Write-Host ""
    Write-Host "Stack trace:" -ForegroundColor Gray
    Write-Host $_.ScriptStackTrace -ForegroundColor Gray

    exit 1
}
