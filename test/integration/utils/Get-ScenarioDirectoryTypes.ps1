# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Single source of truth for which directory types an integration scenario can run against, and
    whether it uses a directory at all.

.DESCRIPTION
    Dot-source this file. Several places in Run-IntegrationTests.ps1 need the answer to "which
    directory types can Scenario N run on" - the interactive menu, the -DirectoryType All coercion,
    and the -Scenario All sweep filter - and the rule used to be hand-copied number lists
    (14, 19, 22; -eq 17; -eq 23) at each call site. They drifted: the -Scenario All sweep never
    learned to skip Scenario 023 on the 389 Directory Server pass, so every Pre-Release run recorded
    a guaranteed failure there. Centralising the rule here means it only has to change in one place.

    Get-ScenarioSupportedDirectoryTypes answers that question. Test-ScenarioIsDirectoryAgnostic
    answers a different one: does the scenario use a directory at all. Scenarios 011, 015 and 016
    accept -DirectoryConfig for runner compatibility but never read it (015 and 016 explicitly
    discard it with `$null = $DirectoryConfig`), so their results are identical regardless of
    directory type; a -DirectoryType All run should execute them once, not once per directory type
    with identical results each time.
#>

function Get-ScenarioSupportedDirectoryTypes {
    <#
    .SYNOPSIS
        The directory types Scenario $ScenarioNumber can run against, in the canonical order
        (SambaAD, OpenLDAP, DirectoryServer389). Always returns an array.
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
        # Samba AD has no equivalent multi-suffix mechanism. Scenario 022 (OpenLDAP Password Policy)
        # depends on the ppolicy overlay that same script loads, which is OpenLDAP's password policy
        # mechanism. The 389 Directory Server lab has two suffixes too, but these scenarios' fixtures
        # and assertions are written against OpenLDAP (cn=config, the ppolicy overlay, olc*
        # attributes), so it is treated like Samba AD here.
        { $_ -in 14, 19, 22 } { return @('OpenLDAP') }

        # Scenario 017 (Initial Password) is Samba AD only: "must change at next sign-in" (its
        # central assertion) is an Active Directory behaviour with no portable equivalent, and JIM
        # reports it as a downgrade on every other directory.
        17 { return @('SambaAD') }

        # Scenario 023 (Unique Value Generation) supports OpenLDAP and Samba AD only. Its Collision
        # test step needs a directory-wide unique-value constraint a CSV target cannot produce, and
        # OpenLDAP already covers the RFC-directory shape, so 389 Directory Server adds nothing this
        # scenario needs.
        23 { return @('SambaAD', 'OpenLDAP') }

        # Every other scenario, including the directory-agnostic ones (see
        # Test-ScenarioIsDirectoryAgnostic below), is architecturally able to run against any of the
        # three directory types.
        default { return @('SambaAD', 'OpenLDAP', 'DirectoryServer389') }
    }
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
