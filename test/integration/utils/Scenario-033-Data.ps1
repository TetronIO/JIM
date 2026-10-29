# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The names, files and functions Scenario 033 (initial administrator provisioning, #2615) shares between its Setup
    and scenario scripts.

.DESCRIPTION
    The scenario builds the same configuration twice, once with the unscoped provisioning export rule in place before the
    initial administrator signs in and once with it created after, so what the two orders share is defined once here.
    Dot-source after utils/Test-Helpers.ps1, which provides Write-FileToConnectorVolume.

    Two Connected Systems, both File Connector systems, and no directory container:

      - Scenario 033 HR: an import-only CSV holding no people, with an import Synchronisation Rule that would project any
        it held. It exists to be synchronised: a Delta Synchronisation of a Connected System other than the one the
        export rule provisions to, with nothing to import, is the run that has to review the initial administrator.
      - Scenario 033 Directory: an export-only CSV. The unscoped export rule provisions every User to it, so any row it
        holds can only have come from the review.
#>

$script:Scenario033HrSystemName = "Scenario 033 HR"
$script:Scenario033DirectorySystemName = "Scenario 033 Directory"
$script:Scenario033HrCsvPath = "/connector-files/scenario-033/hr-people.csv"
$script:Scenario033DirectoryCsvPath = "/connector-files/scenario-033/directory-users.csv"
$script:Scenario033HrRuleName = "Scenario 033 HR: import people"
$script:Scenario033ExportRuleName = "Scenario 033 Directory: provision every user"

# The HR file has a header and no rows; the directory file has a header and, until the export, no rows either.
$script:Scenario033HrCsvHeader = "employeeId,displayName"
$script:Scenario033DirectoryCsvHeader = "subjectIdentifier,displayName,givenName,familyName,userPrincipalName,email"

# What the export rule writes: Metaverse attribute -> directory column. Every one is an attribute the initial
# administrator's sign-in populates from Keycloak's claims (AuthServer.CreateInitialAdminUserAsync), so the object
# satisfies the mappings. Subject Identifier is the External ID: the claim JIM matches people on (JIM_SSO_CLAIM_TYPE).
$script:Scenario033ExportFlows = [ordered]@{
    "Subject Identifier"  = "subjectIdentifier"
    "Display Name"        = "displayName"
    "First Name"          = "givenName"
    "Last Name"           = "familyName"
    "User Principal Name" = "userPrincipalName"
    "Email"               = "email"
}

function Write-Scenario033Csvs {
    <#
    .SYNOPSIS
        Writes both CSVs, header only, to the connector-files volume, replacing whatever a previous order left.
    #>
    $folder = Join-Path ([System.IO.Path]::GetTempPath()) "scenario-033-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $folder | Out-Null
    try {
        Set-Content -Path (Join-Path $folder "hr-people.csv") -Value $script:Scenario033HrCsvHeader
        Set-Content -Path (Join-Path $folder "directory-users.csv") -Value $script:Scenario033DirectoryCsvHeader
        Write-FilesToConnectorVolume -SourceDir $folder -Files @(
            @{ SourceFile = "hr-people.csv";        DestinationPath = $script:Scenario033HrCsvPath }
            @{ SourceFile = "directory-users.csv";  DestinationPath = $script:Scenario033DirectoryCsvPath }
        )
    }
    finally {
        Remove-Item -Path $folder -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Add-Scenario033ExportRule {
    <#
    .SYNOPSIS
        Creates the unscoped provisioning export rule: every User in the Metaverse is provisioned to the directory file,
        with the attributes the initial administrator's sign-in populates. Returns the rule.

    .DESCRIPTION
        Unscoped is the point. The other scenarios' User export rules are scoped to the people their source projected, so
        that an administrator who signs in during a run is never provisioned; this one has no scoping criteria, so it is
        the rule the initial administrator, an internal Metaverse Object, is provisioned by.
    #>
    param(
        [Parameter(Mandatory)][int]$DirectorySystemId
    )

    $objectType = Get-JIMConnectedSystem -Id $DirectorySystemId -ObjectTypes | Where-Object { $_.name -eq "user" }
    if (-not $objectType) { throw "The directory file's schema has no 'user' Object Type." }
    $metaverseUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
    if (-not $metaverseUserType) { throw "The Metaverse has no 'User' Object Type." }
    $metaverseAttributes = @(Get-JIMMetaverseAttribute)

    $rule = New-JIMSyncRule -Name $script:Scenario033ExportRuleName -ConnectedSystemId $DirectorySystemId `
        -ConnectedSystemObjectTypeId $objectType.id -MetaverseObjectTypeId $metaverseUserType.id `
        -Direction Export -ProvisionToConnectedSystem -PassThru
    foreach ($metaverseName in $script:Scenario033ExportFlows.Keys) {
        $columnName = $script:Scenario033ExportFlows[$metaverseName]
        $metaverseAttribute = $metaverseAttributes | Where-Object { $_.name -eq $metaverseName }
        $column = $objectType.attributes | Where-Object { $_.name -eq $columnName }
        if (-not $metaverseAttribute) { throw "The Metaverse has no '$metaverseName' attribute." }
        if (-not $column) { throw "The directory file has no '$columnName' column." }
        New-JIMSyncRuleMapping -SyncRuleId $rule.id -SourceMetaverseAttributeId $metaverseAttribute.id `
            -TargetConnectedSystemAttributeId $column.id | Out-Null
    }
    return $rule
}

function Get-Scenario033InitialAdministrator {
    <#
    .SYNOPSIS
        The person the stack names as its initial administrator, and the bundled Keycloak realm's sign-in for them.

    .DESCRIPTION
        JIM_SSO_INITIAL_ADMIN is read from the running web container, which is what JIM itself consults, not from .env.
        It is the value of the claim JIM identifies people by (JIM_SSO_CLAIM_TYPE, sub); the development realm
        (.devcontainer/keycloak/jim-realm.json) gives its user the same id, so that user is the one who signs in as the
        initial administrator. Their password is the realm's own: a development realm's, published in that file.
    #>
    param(
        [Parameter(Mandatory)][string]$WebContainer,
        [Parameter(Mandatory)][string]$RealmFile
    )

    $initialAdministrator = (docker exec $WebContainer printenv JIM_SSO_INITIAL_ADMIN 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $initialAdministrator) {
        throw "Could not read JIM_SSO_INITIAL_ADMIN from the web container '$WebContainer': $initialAdministrator"
    }

    $realm = Get-Content -LiteralPath $RealmFile -Raw | ConvertFrom-Json
    $person = @($realm.users | Where-Object { $_.id -eq $initialAdministrator }) | Select-Object -First 1
    if (-not $person) {
        throw "JIM_SSO_INITIAL_ADMIN is '$initialAdministrator', but the development realm ($RealmFile) has no user with that id, so there is nobody to sign in as the initial administrator."
    }
    $credential = @($person.credentials | Where-Object { $_.type -eq "password" }) | Select-Object -First 1
    if (-not $credential -or -not $credential.value) { throw "The development realm's user '$($person.username)' has no password." }

    return [pscustomobject]@{
        Subject  = $initialAdministrator
        UserName = [string]$person.username
        Password = [string]$credential.value
    }
}

function Get-Scenario033ConnectorFileRows {
    <#
    .SYNOPSIS
        The data rows of a CSV in the connector-files volume, as objects, read through a throwaway container that mounts
        the volume. An empty array when the file holds a header only.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Volume
    )

    $content = docker run --rm -v "${Volume}:/connector-files:ro" $script:ConnectorVolumeHelperImage cat $Path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not read $Path from the connector files volume: $content" }
    $lines = @($content | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -le 1) { return @() }
    return @($lines | ConvertFrom-Csv)
}
