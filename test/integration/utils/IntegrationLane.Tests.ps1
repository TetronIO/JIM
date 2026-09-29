# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for IntegrationLane.ps1 (#636).

.DESCRIPTION
    Two properties matter most. A serial run (no JIM_INTEGRATION_LANE) must resolve every name, port and
    Compose argument to exactly what the harness used before lanes existed, so serial behaviour is
    unchanged. And no two lanes may share a container, port, volume, network or project name, or one
    lane's reset would take another lane's stack down mid-run.
#>

BeforeAll {
    . "$PSScriptRoot/IntegrationLane.ps1"
    $script:SavedLane = $env:JIM_INTEGRATION_LANE
}

AfterAll {
    $env:JIM_INTEGRATION_LANE = $script:SavedLane
}

Describe 'Get-IntegrationLane (serial)' {
    BeforeEach { $env:JIM_INTEGRATION_LANE = $null }

    It 'is not active' {
        (Get-IntegrationLane).Active | Should -BeFalse
    }

    It 'keeps the historic container names, port and volumes' {
        $lane = Get-IntegrationLane
        $lane.WebContainer | Should -Be 'jim.web'
        $lane.WorkerContainer | Should -Be 'jim.worker'
        $lane.SchedulerContainer | Should -Be 'jim.scheduler'
        $lane.DatabaseContainer | Should -Be 'jim.database'
        $lane.JimUrl | Should -Be 'http://localhost:5200'
        $lane.DbVolume | Should -Be 'jim-db-volume'
        $lane.ConnectorFilesVolume | Should -Be 'jim-connector-files-volume'
        $lane.WorkerLogDirectoryName | Should -Be 'worker'
        $lane.PublishesKeycloak | Should -BeTrue
    }

    It 'keeps the historic Compose arguments' {
        Get-JimComposeArgs | Should -Be @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml')
        Get-IntegrationComposeArgs | Should -Be @('-f', 'test/integration/docker/docker-compose.integration-tests.yml')
    }

    It 'force-removes every directory container the serial reset always removed' {
        (Get-IntegrationLane).DirectoryContainers | Should -Be @('samba-ad-primary', 'samba-ad-source', 'samba-ad-target', 'openldap-primary', 'dirsrv-primary', 'postgres-target', 'mysql-test')
    }

    It 'removes every jim-integration volume except the preserved Scenario 016 databases' {
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-openldap-primary-data' | Should -BeTrue
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-openldap-primary-data-openldap' | Should -BeTrue
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-oracle-data' | Should -BeFalse
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-db-volume' | Should -BeFalse
    }
}

Describe 'Get-IntegrationLane (lanes)' {
    It 'rejects an unknown lane' {
        { Get-IntegrationLane -Name 'NotADirectory' } | Should -Throw '*Unknown integration lane*'
    }

    It 'keeps the serial names for the Samba AD lane, so Scenario 016 and browser sign-in work unchanged' {
        $lane = Get-IntegrationLane -Name 'SambaAD'
        $lane.Active | Should -BeTrue
        $lane.Suffix | Should -Be ''
        $lane.WebContainer | Should -Be 'jim.web'
        $lane.JimUrl | Should -Be 'http://localhost:5200'
        $lane.PublishesKeycloak | Should -BeTrue
    }

    It 'gives the suffixed lanes their own names and ports, and no browser-facing Keycloak' {
        $openLdap = Get-IntegrationLane -Name 'OpenLDAP'
        $openLdap.WebContainer | Should -Be 'jim.web-openldap'
        $openLdap.JimUrl | Should -Be 'http://localhost:5300'
        $openLdap.DbPort | Should -Be 5433
        $openLdap.PublishesKeycloak | Should -BeFalse

        $dirsrv = Get-IntegrationLane -Name 'DirectoryServer389'
        $dirsrv.WebContainer | Should -Be 'jim.web-dirsrv'
        $dirsrv.JimUrl | Should -Be 'http://localhost:5400'
        $dirsrv.DbPort | Should -Be 5434
    }

    It 'shares no container, port, volume, network or project name between lanes' {
        $lanes = @(Get-IntegrationLaneNames | ForEach-Object { Get-IntegrationLane -Name $_ })
        foreach ($property in @('WebContainer', 'WorkerContainer', 'SchedulerContainer', 'DatabaseContainer', 'KeycloakContainer',
                'WebPort', 'DbPort', 'Network', 'DbVolume', 'ConnectorFilesVolume', 'JimProject', 'IntegrationProject', 'WorkerLogDirectoryName')) {
            $values = @($lanes | ForEach-Object { $_.$property })
            @($values | Sort-Object -Unique).Count | Should -Be $values.Count -Because "$property must differ between lanes"
        }
        $allDirectoryContainers = @($lanes | ForEach-Object { $_.DirectoryContainers })
        @($allDirectoryContainers | Sort-Object -Unique).Count | Should -Be $allDirectoryContainers.Count
    }

    It 'adds the lane override files and project names to the suffixed lanes only' {
        $env:JIM_INTEGRATION_LANE = 'OpenLDAP'
        try {
            Get-JimComposeArgs | Should -Be @('-p', 'jim-openldap', '-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml', '-f', 'test/integration/docker/jim-lane.override.yml')
            Get-IntegrationComposeArgs | Should -Be @('-p', 'jim-integration-openldap', '-f', 'test/integration/docker/docker-compose.integration-tests.yml', '-f', 'test/integration/docker/integration-lane.override.yml')
            $env:JIM_INTEGRATION_LANE = 'SambaAD'
            Get-JimComposeArgs | Should -Be @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml')
        }
        finally {
            $env:JIM_INTEGRATION_LANE = $null
        }
    }
}

Describe 'Test-VolumeBelongsToIntegrationLane (lanes)' {
    AfterEach { $env:JIM_INTEGRATION_LANE = $null }

    It 'lets a suffixed lane remove only its own volumes' {
        $env:JIM_INTEGRATION_LANE = 'OpenLDAP'
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-openldap-primary-data-openldap' | Should -BeTrue
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-openldap-primary-data' | Should -BeFalse
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-dirsrv-primary-data-dirsrv' | Should -BeFalse
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-samba-primary-var' | Should -BeFalse
    }

    It 'never lets the Samba AD lane remove another lane''s volumes or the preserved databases' {
        $env:JIM_INTEGRATION_LANE = 'SambaAD'
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-samba-primary-var' | Should -BeTrue
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-openldap-primary-data-openldap' | Should -BeFalse
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-dirsrv-primary-data-dirsrv' | Should -BeFalse
        Test-VolumeBelongsToIntegrationLane -VolumeName 'jim-integration-sqlserver-data' | Should -BeFalse
    }
}

Describe 'Get-IntegrationTempPath' {
    AfterEach { $env:JIM_INTEGRATION_LANE = $null }

    It 'keeps the serial name outside a lane' {
        Split-Path -Leaf (Get-IntegrationTempPath -Name 'scenario-012-hr-users.csv') | Should -Be 'scenario-012-hr-users.csv'
    }

    It 'gives each lane its own scratch directory and keeps the file name' {
        $env:JIM_INTEGRATION_LANE = 'OpenLDAP'
        $openLdapPath = Get-IntegrationTempPath -Name 'scenario-012-hr-users.csv'
        Split-Path -Leaf $openLdapPath | Should -Be 'scenario-012-hr-users.csv'
        $env:JIM_INTEGRATION_LANE = 'DirectoryServer389'
        $dirsrvPath = Get-IntegrationTempPath -Name 'scenario-012-hr-users.csv'
        $dirsrvPath | Should -Not -Be $openLdapPath
        Split-Path -Parent $dirsrvPath | Should -Not -Be (Split-Path -Parent $openLdapPath)
    }
}
