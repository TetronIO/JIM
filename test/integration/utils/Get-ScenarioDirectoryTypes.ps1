# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Single source of truth for which directory types an integration scenario can run against, and
    whether it uses a directory at all.

.DESCRIPTION
    Dot-source this file. Several places in Run-IntegrationTests.ps1 need the answer to "which
    directory types can Scenario N run on" - the interactive menu, an explicit -DirectoryType, the
    -DirectoryType All expansion, and the -Scenario All sweep filter - and the rule used to be
    hand-copied number lists (14, 19, 22; -eq 17; -eq 23) at each call site. They drifted: the
    -Scenario All sweep never learned to skip Scenario 023 on the 389 Directory Server pass, so every
    Pre-Release run recorded a guaranteed failure there. Centralising the rule here means it only has
    to change in one place, and it is the ONLY place: the Active Directory lab is a column of the
    table like the others, not a special case beside it.

    Get-ScenarioSupportedDirectoryTypes answers that question, over all four directory types
    (SambaAD, OpenLDAP, DirectoryServer389 and ActiveDirectory). The lab runs whatever Samba AD runs,
    except the scenarios only a real domain controller can hold (024 and 025, which run there only).
    Get-ContainerDirectoryType lists the three container types, which is what -DirectoryType All and
    -PreRelease expand to (the lab needs its host, so they never include it).

    Test-ScenarioIsDirectoryAgnostic answers a different one: does the scenario use a directory at
    all. Scenarios 011, 015 and 016 accept -DirectoryConfig for runner compatibility but never read
    it (015 and 016 explicitly discard it with `$null = $DirectoryConfig`), so their results are
    identical regardless of directory type; a -DirectoryType All run should execute them once, not
    once per directory type with identical results each time.
#>

function Get-ScenarioSupportedDirectoryTypes {
    <#
    .SYNOPSIS
        The directory types Scenario $ScenarioNumber can run against, in the canonical order
        (SambaAD, OpenLDAP, DirectoryServer389, ActiveDirectory). Always returns an array.

    .DESCRIPTION
        The Active Directory lab runs whatever Samba AD runs, except the scenarios only a real domain
        controller can hold (024 and 025), which run on the lab only. $null (the "All" scenario) has no
        restriction.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [Nullable[int]]$ScenarioNumber
    )

    switch ($ScenarioNumber) {
        # Scenarios 014 (Attribute Priority) and 019 (Auxiliary Classes) depend on two LDAP suffixes
        # hosted on a single OpenLDAP container (docker/openldap/scripts/01-add-second-suffix.sh);
        # Samba AD (and so the Active Directory lab) has no equivalent multi-suffix mechanism. Scenario 022 (OpenLDAP Password Policy)
        # depends on the ppolicy overlay that same script loads, which is OpenLDAP's password policy
        # mechanism. The 389 Directory Server lab has two suffixes too, but these scenarios' fixtures
        # and assertions are written against OpenLDAP (cn=config, the ppolicy overlay, olc*
        # attributes), so it is treated like Samba AD here.
        { $_ -in 14, 19, 22 } { return @('OpenLDAP') }

        # Scenario 017 (Initial Password) is Active Directory family only (Samba AD and the Active
        # Directory lab): "must change at next sign-in" (its central assertion) is an Active Directory
        # behaviour with no portable equivalent, and JIM reports it as a downgrade on every other
        # directory.
        17 { return @('SambaAD', 'ActiveDirectory') }

        # Scenario 023 (Unique Value Generation) supports OpenLDAP, Samba AD and the Active Directory
        # lab only. Its Collision test step needs a directory-wide unique-value constraint a CSV
        # target cannot produce, and OpenLDAP already covers the RFC-directory shape, so 389 Directory
        # Server adds nothing this scenario needs.
        23 { return @('SambaAD', 'OpenLDAP', 'ActiveDirectory') }

        # Scenario 026 (Metaverse-Derived Attribute Flows) composes Scenario 023's substrate (Setup-Scenario-001.ps1
        # -GenerateAccountName) and refuses 389 Directory Server in its setup for the same reason: the derived pass
        # is directory-independent, so OpenLDAP already covers the RFC-directory shape.
        26 { return @('SambaAD', 'OpenLDAP', 'ActiveDirectory') }

        # Scenarios 024 (Active Directory Password Policy) and 025 (Active Directory Delta Import
        # Integrity) run on the Active Directory lab only. 024 asserts a Windows domain controller's
        # password policy and a Fine-Grained Password Policy; 025 asserts the Active Directory Recycle
        # Bin and the restore-from-backup behaviour of a Hyper-V checkpoint revert. A Samba AD
        # container and the OpenLDAP and 389 Directory Server labs have none of these to assert against.
        { $_ -in 24, 25 } { return @('ActiveDirectory') }

        # Every other scenario, including the directory-agnostic ones (see
        # Test-ScenarioIsDirectoryAgnostic below), is architecturally able to run against any of the
        # four directory types: the lab runs whatever Samba AD runs.
        default { return @('SambaAD', 'OpenLDAP', 'DirectoryServer389', 'ActiveDirectory') }
    }
}

function Get-ContainerDirectoryType {
    <#
    .SYNOPSIS
        The three container directory types (SambaAD, OpenLDAP, DirectoryServer389), in the canonical
        order. Always returns an array.

    .DESCRIPTION
        What -DirectoryType All and -PreRelease expand to. ActiveDirectory is not among them: it needs
        the lab host (Hyper-V domain controllers reached over SSH), so it is only ever asked for by
        name. Intersect this with Get-ScenarioSupportedDirectoryTypes to get the legs an "All" run of
        one scenario executes; an empty intersection (Scenarios 024 and 025) means the scenario runs on
        the lab only, which "All" never includes.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    return @('SambaAD', 'OpenLDAP', 'DirectoryServer389')
}

function Get-ScenarioDirectoryTypeMenuEntry {
    <#
    .SYNOPSIS
        The names the interactive directory type menu offers for Scenario $ScenarioNumber: its supported
        directory types in the canonical order, then "All" when at least one container directory type is
        among them. "All" expands to the container types only, so a scenario that runs on the Active
        Directory lab alone (024, 025) is not offered it. Always returns an array.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [Nullable[int]]$ScenarioNumber
    )

    $supported = @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $ScenarioNumber)
    $containerTypes = @(Get-ContainerDirectoryType)
    $hasContainerLeg = @($supported | Where-Object { $_ -in $containerTypes }).Count -gt 0
    if ($hasContainerLeg) {
        return @($supported + 'All')
    }
    return $supported
}

function Test-ScenarioIsDirectoryAgnostic {
    <#
    .SYNOPSIS
        $true when Scenario $ScenarioNumber does not use a directory at all, so running it once
        covers every directory type and a -DirectoryType All run should not repeat it.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [Nullable[int]]$ScenarioNumber
    )

    # Scenarios 011 (Scoping Criteria Matrix), 015 (SCIM Connector) and 016 (JIM SQL Connector
    # matrix) accept -DirectoryConfig for runner compatibility but never read it (015 and 016
    # explicitly discard it with `$null = $DirectoryConfig`); their results are identical on every
    # directory type.
    return $ScenarioNumber -in 11, 15, 16
}
