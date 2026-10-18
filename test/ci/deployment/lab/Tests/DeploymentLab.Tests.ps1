# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the pure functions of test/ci/deployment/lab/DeploymentLab.psm1.

.DESCRIPTION
    The deployment lab itself needs an LXD host, so CI cannot run it; these cover what decides its results without
    one: which cells the matrix holds and whether it is valid, which cells a run selects, how many run at once in the
    host's memory, how a browser's sign-in is followed through the identity provider's pages, and the report.
#>

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..' 'DeploymentLab.psm1') -Force

    function New-TestMatrix {
        @{
            Images = @{
                'u24-docker' = @{ Image = 'jimlab/u24-docker-ctrd'; Os = 'Ubuntu 24.04' }
                'alma9-podman' = @{ Image = 'jimlab/alma9-podman'; Os = 'AlmaLinux 9' }
            }
            Cells = @(
                @{ Name = 'u24-docker-ag'; Image = 'u24-docker'; Runtime = 'docker'; Network = 'airgap'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('docker', 'install') }
                @{ Name = 'alma-podrl-ag'; Image = 'alma9-podman'; Runtime = 'podman'; Rootless = $true; Network = 'airgap'; Scenario = 'Install'; MemoryGiB = 6; Tags = @('podman', 'install', 'rhel') }
            )
        }
    }
}

Describe 'Test-LabMatrix' {
    It 'returns nothing for a valid matrix' {
        Test-LabMatrix -Matrix (New-TestMatrix) | Should -BeNullOrEmpty
    }

    It 'names a cell whose image the matrix does not define' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].Image = 'u22-docker'

        Test-LabMatrix -Matrix $matrix | Should -Be @("u24-docker-ag: image 'u22-docker' is not one of the matrix's images (alma9-podman, u24-docker)")
    }

    It 'refuses two cells of the same name' {
        $matrix = New-TestMatrix
        $matrix.Cells[1].Name = 'u24-docker-ag'

        Test-LabMatrix -Matrix $matrix | Should -Contain "u24-docker-ag: more than one cell has this name"
    }

    It 'refuses a name LXD would not take as an instance name' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].Name = 'U24_docker'

        Test-LabMatrix -Matrix $matrix | Should -Contain "U24_docker: a cell's name must be lower-case letters, digits and hyphens, starting with a letter, and at most 40 characters"
    }

    It 'refuses rootless on Docker' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].Rootless = $true

        Test-LabMatrix -Matrix $matrix | Should -Be @('u24-docker-ag: rootless is for Podman only')
    }

    It 'refuses an unknown runtime, network and scenario' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].Runtime = 'containerd'
        $matrix.Cells[0].Network = 'lan'
        $matrix.Cells[0].Scenario = 'Upgrade-ish'

        $errors = Test-LabMatrix -Matrix $matrix

        $errors | Should -Contain "u24-docker-ag: runtime 'containerd' is not docker or podman"
        $errors | Should -Contain "u24-docker-ag: network 'lan' is not airgap or online"
        $errors | Should -Contain "u24-docker-ag: scenario 'Upgrade-ish' is not one of Install, Upgrade, InstallerCases, ExternalDatabase, Soak"
    }

    It 'refuses a cell given less memory than JIM needs' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].MemoryGiB = 2

        Test-LabMatrix -Matrix $matrix | Should -Be @('u24-docker-ag: MemoryGiB must be at least 4')
    }
}

Describe 'Test-LabMatrix for upgrade cells' {
    It 'accepts an upgrade cell' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].Scenario = 'Upgrade'
        $matrix.Cells[0].MemoryGiB = 12

        Test-LabMatrix -Matrix $matrix | Should -BeNullOrEmpty
    }
}

Describe 'Test-LabMatrix for the installer''s cases and an external database' {
    It 'accepts an installer cases cell' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].Scenario = 'InstallerCases'

        Test-LabMatrix -Matrix $matrix | Should -BeNullOrEmpty
    }

    It 'accepts an external database cell with memory for its database server' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].Scenario = 'ExternalDatabase'
        $matrix.Cells[0].DatabaseMemoryGiB = 2

        Test-LabMatrix -Matrix $matrix | Should -BeNullOrEmpty
    }

    It 'refuses an external database cell with no memory for its database server' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].Scenario = 'ExternalDatabase'

        Test-LabMatrix -Matrix $matrix | Should -Be @('u24-docker-ag: an ExternalDatabase cell needs DatabaseMemoryGiB, at least 1, for its database server')
    }

    It 'refuses memory for a database server in a cell that has none' {
        $matrix = New-TestMatrix
        $matrix.Cells[0].DatabaseMemoryGiB = 2

        Test-LabMatrix -Matrix $matrix | Should -Be @('u24-docker-ag: DatabaseMemoryGiB is for ExternalDatabase cells only')
    }
}

Describe 'Get-LabCellMemory' {
    It 'is the cell''s VM memory' {
        Get-LabCellMemory -Cell @{ Name = 'a'; MemoryGiB = 6 } | Should -Be 6
    }

    It 'adds its database server''s memory' {
        Get-LabCellMemory -Cell @{ Name = 'a'; MemoryGiB = 6; DatabaseMemoryGiB = 2 } | Should -Be 8
    }

    It 'reads a cell read back from JSON' {
        Get-LabCellMemory -Cell ('{"Name":"a","MemoryGiB":6,"DatabaseMemoryGiB":2}' | ConvertFrom-Json) | Should -Be 8
    }
}

Describe 'Select-LabCell' {
    BeforeAll {
        $script:cells = (New-TestMatrix).Cells
    }

    It 'returns every cell when given no filter' {
        (Select-LabCell -Cell $cells).Name | Should -Be @('u24-docker-ag', 'alma-podrl-ag')
    }

    It 'selects by name, with wildcards' {
        (Select-LabCell -Cell $cells -Name 'alma-*').Name | Should -Be @('alma-podrl-ag')
    }

    It 'selects cells carrying every tag given' {
        (Select-LabCell -Cell $cells -Tag 'install', 'rhel').Name | Should -Be @('alma-podrl-ag')
        Select-LabCell -Cell $cells -Tag 'docker', 'rhel' | Should -BeNullOrEmpty
    }

    It 'applies a name and a tag together' {
        Select-LabCell -Cell $cells -Name 'u24-*' -Tag 'rhel' | Should -BeNullOrEmpty
    }
}

Describe 'Get-LabMemoryBudget' {
    It 'is the host''s memory less the reserve, in whole GiB' {
        $memInfo = @('MemTotal:       65756196 kB', 'MemFree:        12000000 kB')

        Get-LabMemoryBudget -MemInfo $memInfo -ReserveGiB 8 | Should -Be 54
    }

    It 'is never below zero' {
        Get-LabMemoryBudget -MemInfo @('MemTotal:        4000000 kB') -ReserveGiB 8 | Should -Be 0
    }

    It 'fails on text without MemTotal' {
        { Get-LabMemoryBudget -MemInfo @('MemFree: 1 kB') -ReserveGiB 8 } | Should -Throw '*MemTotal*'
    }
}

Describe 'Select-LabCellToStart' {
    BeforeAll {
        function New-Cell([string]$Name, [int]$MemoryGiB) { @{ Name = $Name; MemoryGiB = $MemoryGiB } }
    }

    It 'starts as many as the budget holds, largest first' {
        $pending = @((New-Cell 'a' 6), (New-Cell 'b' 12), (New-Cell 'c' 6), (New-Cell 'd' 6))

        (Select-LabCellToStart -Pending $pending -RunningGiB 0 -BudgetGiB 24).Name | Should -Be @('b', 'a', 'c')
    }

    It 'fills what is left beside the cells already running' {
        $pending = @((New-Cell 'a' 12), (New-Cell 'b' 6))

        (Select-LabCellToStart -Pending $pending -RunningGiB 12 -BudgetGiB 20).Name | Should -Be @('b')
    }

    It 'starts nothing when nothing fits beside the running cells' {
        Select-LabCellToStart -Pending @(New-Cell 'a' 12) -RunningGiB 12 -BudgetGiB 20 | Should -BeNullOrEmpty
    }

    It 'starts a cell larger than the whole budget on its own, rather than never' {
        (Select-LabCellToStart -Pending @((New-Cell 'big' 32), (New-Cell 'small' 6)) -RunningGiB 0 -BudgetGiB 24).Name |
            Should -Be @('big')
    }

    It 'honours a limit on how many run at once' {
        $pending = @((New-Cell 'a' 6), (New-Cell 'b' 6), (New-Cell 'c' 6))

        (Select-LabCellToStart -Pending $pending -RunningGiB 6 -RunningCount 1 -BudgetGiB 100 -MaxParallel 2).Name |
            Should -Be @('a')
    }

    It 'counts a cell''s database server with it' {
        $pending = @(@{ Name = 'ext'; MemoryGiB = 6; DatabaseMemoryGiB = 2 }, (New-Cell 'a' 7))

        (Select-LabCellToStart -Pending $pending -RunningGiB 0 -BudgetGiB 14).Name | Should -Be @('ext')
    }

    It 'keeps the matrix''s order among cells of the same size' {
        $pending = @((New-Cell 'x' 6), (New-Cell 'y' 6), (New-Cell 'z' 6))

        (Select-LabCellToStart -Pending $pending -RunningGiB 0 -BudgetGiB 100).Name | Should -Be @('x', 'y', 'z')
    }
}

Describe 'Get-LabLoginForm' {
    It 'returns the action of Keycloak''s login form, with its entities decoded' {
        $html = '<form id="kc-form-login" onsubmit="login.disabled = true; return true;" action="http://10.78.0.1:8180/realms/jim/login-actions/authenticate?session_code=abc&amp;execution=e1&amp;client_id=jim-web&amp;tab_id=t1" method="post">'

        Get-LabLoginForm -Html $html | Should -Be 'http://10.78.0.1:8180/realms/jim/login-actions/authenticate?session_code=abc&execution=e1&client_id=jim-web&tab_id=t1'
    }

    It 'returns nothing for a page without the login form' {
        Get-LabLoginForm -Html '<html><body>JIM</body></html>' | Should -BeNullOrEmpty
    }
}

Describe 'Get-LabFormPost' {
    It 'reads the form Keycloak posts back to JIM, whatever the case of its tags' {
        # Keycloak's form_post page writes its tags in upper case.
        $html = @'
<HTML><HEAD><TITLE>Submit This Form</TITLE></HEAD>
<BODY onload="javascript:document.forms[0].submit()">
<FORM METHOD="POST" ACTION="https://jim.jimlab.test/signin-oidc">
<INPUT TYPE="HIDDEN" NAME="code" VALUE="c0de.with.dots"/>
<INPUT TYPE="HIDDEN" NAME="state" VALUE="CfDJ8&#43;abc&#x3D;"/>
<INPUT TYPE="HIDDEN" NAME="session_state" VALUE="s1"/>
<INPUT TYPE="HIDDEN" NAME="iss" VALUE="http://10.78.0.1:8180/realms/jim"/>
</FORM></BODY></HTML>
'@

        $post = Get-LabFormPost -Html $html

        $post.Action | Should -Be 'https://jim.jimlab.test/signin-oidc'
        $post.Fields.Keys | Should -Be @('code', 'state', 'session_state', 'iss')
        $post.Fields['state'] | Should -Be 'CfDJ8+abc='
        $post.Fields['iss'] | Should -Be 'http://10.78.0.1:8180/realms/jim'
    }

    It 'returns nothing for a page that is not a post back to JIM' {
        Get-LabFormPost -Html '<form id="kc-form-login" action="x" method="post"><input name="username"></form>' |
            Should -BeNullOrEmpty
    }
}

Describe 'Format-LabReport' {
    BeforeAll {
        $script:results = @(
            [pscustomobject]@{
                Cell = @{ Name = 'u24-docker-ag'; Os = 'Ubuntu 24.04'; Runtime = 'docker'; Rootless = $false; Network = 'airgap'; Scenario = 'Install' }
                Checks = @(
                    [pscustomobject]@{ Name = 'no route out'; Passed = $true; Detail = '' }
                    [pscustomobject]@{ Name = 'signs in'; Passed = $true; Detail = '' }
                )
                Minutes = 14.2
            }
            [pscustomobject]@{
                Cell = @{ Name = 'alma-podrl-ag'; Os = 'AlmaLinux 9'; Runtime = 'podman'; Rootless = $true; Network = 'airgap'; Scenario = 'Install' }
                Checks = @(
                    [pscustomobject]@{ Name = 'no route out'; Passed = $true; Detail = '' }
                    [pscustomobject]@{ Name = 'reboot: ready over HTTPS'; Passed = $false; Detail = 'not within 300 s' }
                )
                Minutes = 18.6
            }
        )
    }

    It 'gives one row per cell, with its result and the checks it failed' {
        $report = Format-LabReport -Result $results -Title 'Bundle 0.17.0'

        $report | Should -Match '(?m)^# Bundle 0\.17\.0$'
        $report | Should -Match '(?m)^\| u24-docker-ag \| Ubuntu 24\.04 \| Docker \| air-gapped \| Install \| ✅ 2/2 \| 14 min \|  \|$'
        $report | Should -Match '(?m)^\| alma-podrl-ag \| AlmaLinux 9 \| Podman rootless \| air-gapped \| Install \| ❌ 1/2 \| 19 min \| reboot: ready over HTTPS \|$'
    }

    It 'leads with the overall outcome' {
        Format-LabReport -Result $results -Title 'x' | Should -Match '(?m)^1 of 2 cells passed\.$'
    }

    It 'lists each failed check with its detail' {
        Format-LabReport -Result $results -Title 'x' | Should -Match '(?m)^- \*\*alma-podrl-ag\*\*: reboot: ready over HTTPS: not within 300 s$'
    }

    It 'reads results as Invoke-LabCell.ps1 saves them, through JSON' {
        $saved = $results | ForEach-Object { $_ | ConvertTo-Json -Depth 6 | ConvertFrom-Json }

        $report = Format-LabReport -Result $saved -Title 'x'

        $report | Should -Match '(?m)^\| alma-podrl-ag \| AlmaLinux 9 \| Podman rootless \| air-gapped \| Install \| ❌ 1/2 \| 19 min \| reboot: ready over HTTPS \|$'
        $report | Should -Match '(?m)^1 of 2 cells passed\.$'
    }

    It 'reports a cell that recorded no checks as failed' {
        $broken = [pscustomobject]@{
            Cell = @{ Name = 'u24-podrf-ag'; Os = 'Ubuntu 24.04'; Runtime = 'podman'; Rootless = $false; Network = 'online'; Scenario = 'Install' }
            Checks = @()
            Minutes = 1
            Error = 'the VM never booted'
        }

        $report = Format-LabReport -Result @($broken) -Title 'x'

        $report | Should -Match '(?m)^\| u24-podrf-ag \| Ubuntu 24\.04 \| Podman rootful \| online \| Install \| ❌ 0/0 \| 1 min \| the VM never booted \|$'
        $report | Should -Match '(?m)^0 of 1 cells passed\.$'
    }
}

Describe 'The matrix in Cells.psd1' {
    It 'is valid' {
        $matrix = Import-PowerShellDataFile (Join-Path $PSScriptRoot '..' 'Cells.psd1')

        Test-LabMatrix -Matrix $matrix | Should -BeNullOrEmpty
    }
}

Describe 'Get-LabPostgresMemory' {
    It 'sizes the bundled PostgreSQL as the installer does, matching the documentation''s 8 GB example' {
        $sized = Get-LabPostgresMemory -MemoryMB 8192

        $sized.JIM_DB_SHARED_BUFFERS | Should -Be '2048MB'
        $sized.JIM_DB_EFFECTIVE_CACHE_SIZE | Should -Be '4096MB'
        $sized.JIM_DB_MAINTENANCE_WORK_MEM | Should -Be '512MB'
        $sized.JIM_DB_WORK_MEM | Should -Be '10MB'
        $sized.JIM_DB_SHM_SIZE | Should -Be '2560mb'
    }

    It 'caps shared_buffers at 8 GB and maintenance_work_mem at 2 GB on a large host' {
        $sized = Get-LabPostgresMemory -MemoryMB 65536

        $sized.JIM_DB_SHARED_BUFFERS | Should -Be '8192MB'
        $sized.JIM_DB_MAINTENANCE_WORK_MEM | Should -Be '2048MB'
        $sized.JIM_DB_WORK_MEM | Should -Be '95MB'
    }

    It 'keeps work_mem at PostgreSQL''s own default of 4 MB at least, on a small host' {
        (Get-LabPostgresMemory -MemoryMB 2048).JIM_DB_WORK_MEM | Should -Be '4MB'
    }
}

Describe 'Test-LabVersionAtMost' {
    It 'compares release numbers numerically, not as text' {
        Test-LabVersionAtMost -Version '0.9.0' -Limit '0.16.0' | Should -BeTrue
        Test-LabVersionAtMost -Version '0.16.0' -Limit '0.16.0' | Should -BeTrue
        Test-LabVersionAtMost -Version '0.16.1' -Limit '0.16.0' | Should -BeFalse
        Test-LabVersionAtMost -Version '1.0.0' -Limit '0.16.0' | Should -BeFalse
    }

    It 'reads a lab build''s version by its release number, and a leading v' {
        Test-LabVersionAtMost -Version '0.16.1-lab.f9ad79b' -Limit '0.16.0' | Should -BeFalse
        Test-LabVersionAtMost -Version 'v0.16.0' -Limit '0.16.0' | Should -BeTrue
    }
}

Describe 'Get-LabNextVersion' {
    It 'is the next patch release' {
        Get-LabNextVersion -Version '0.16.0' | Should -Be '0.16.1'
        Get-LabNextVersion -Version 'v0.9.9' | Should -Be '0.9.10'
    }
}

Describe 'ConvertTo-LabRootlessCommand' {
    It 'runs systemctl and podman as the rootless helpers do, as Running on Podman says' {
        ConvertTo-LabRootlessCommand -Command 'systemctl stop jim.service' | Should -Be 'jim-systemctl stop jim.service'
        ConvertTo-LabRootlessCommand -Command "systemctl restart jim-database.service`nsystemctl start jim.service" |
            Should -Be "jim-systemctl restart jim-database.service`njim-systemctl start jim.service"
        ConvertTo-LabRootlessCommand -Command 'for f in docker-images/*.tar; do podman load < "$f"; done' |
            Should -Be 'for f in docker-images/*.tar; do jim-podman load < "$f"; done'
        ConvertTo-LabRootlessCommand -Command 'sudo podman exec jim-database-postgres pg_dump' |
            Should -Be 'jim-podman exec jim-database-postgres pg_dump'
    }

    It 'leaves alone a podman that is not a command, such as the bundle''s podman folder' {
        ConvertTo-LabRootlessCommand -Command 'cp podman/jim.yaml podman/jim-database.yaml setup.sh /opt/jim/' |
            Should -Be 'cp podman/jim.yaml podman/jim-database.yaml setup.sh /opt/jim/'
        ConvertTo-LabRootlessCommand -Command 'jim-systemctl start jim.service' | Should -Be 'jim-systemctl start jim.service'
    }
}

Describe 'ConvertTo-LabMegabytes' {
    It 'reads PostgreSQL''s sizes in any of its units, as SHOW prints them' {
        ConvertTo-LabMegabytes '2995MB' | Should -Be 2995
        ConvertTo-LabMegabytes '3GB' | Should -Be 3072
        ConvertTo-LabMegabytes '131072kB' | Should -Be 128
        ConvertTo-LabMegabytes '1TB' | Should -Be 1048576
    }

    It 'fails on a size it cannot read' {
        { ConvertTo-LabMegabytes '8gb' } | Should -Throw '*8gb*'
    }
}

Describe 'Get-LabImageNameToDrop' {
    It 'returns every name but the one the installer keeps for rolling back, as a tag takeover leaves the image' {
        Get-LabImageNameToDrop -Name @('postgres:18.6', 'postgres:jim-previous', 'postgres@sha256:' + ('a' * 64)) |
            Should -Be @('postgres:18.6', 'postgres@sha256:' + ('a' * 64))
    }

    It 'keeps the name however Docker spells its registry' {
        Get-LabImageNameToDrop -Name @('docker.io/library/postgres:18.6', 'docker.io/library/postgres:jim-previous') |
            Should -Be @('docker.io/library/postgres:18.6')
    }

    It 'returns nothing when the kept name is the only one' {
        @(Get-LabImageNameToDrop -Name @('postgres:jim-previous')).Count | Should -Be 0
    }

    It 'returns nothing when the image was not kept, rather than strip it of every name' {
        @(Get-LabImageNameToDrop -Name @('postgres:18.6')).Count | Should -Be 0
    }
}

Describe 'Get-LabSetupMessage' {
    BeforeAll {
        $script:output = @(
            "`e[0;34m`e[1m[INFO]`e[0m Checking prerequisites..."
            "`e[0;32m`e[1m[OK]`e[0m   All prerequisites met"
            "`e[1;33m`e[1m[WARN]`e[0m JIM_DB_SHARED_BUFFERS is 3000MB, more than half this host's 5.7 GB of memory"
            '  a continuation line'
            '[ERR]  JIM_DB_SHARED_BUFFERS is 64GB, which this host cannot give'
        )
    }

    It 'returns the installer''s messages at a level, without colour or label' {
        Get-LabSetupMessage -Line $output -Level WARN |
            Should -Be @("JIM_DB_SHARED_BUFFERS is 3000MB, more than half this host's 5.7 GB of memory")
    }

    It 'reads messages written without colour' {
        Get-LabSetupMessage -Line $output -Level ERR | Should -Be @('JIM_DB_SHARED_BUFFERS is 64GB, which this host cannot give')
    }

    It 'reads a message printed after a question, on the question''s line' {
        $line = "Overwrite configuration? [y/N]: `e[0;34m`e[1m[INFO]`e[0m Aborting. Existing installation left untouched."

        Get-LabSetupMessage -Line $line -Level INFO | Should -Be @('Aborting. Existing installation left untouched.')
    }

    It 'returns nothing when there is no message at the level' {
        Get-LabSetupMessage -Line @('[OK]   done') -Level ERR | Should -BeNullOrEmpty
    }
}

Describe 'Compare-LabFingerprint' {
    It 'returns nothing for two fingerprints that are the same' {
        Compare-LabFingerprint -Before @('a1 .env', 'jim.web image') -After @('a1 .env', 'jim.web image') | Should -BeNullOrEmpty
    }

    It 'returns what went and what came, in the order of the fingerprint' {
        $before = @('a1 .env', 'b2 docker-compose.yml', 'jim.web image:1')
        $after = @('a1 .env', 'c3 docker-compose.yml', 'jim.web image:1', '.before-setup')

        Compare-LabFingerprint -Before $before -After $after |
            Should -Be @('- b2 docker-compose.yml', '+ c3 docker-compose.yml', '+ .before-setup')
    }

    It 'counts an empty fingerprint as one' {
        Compare-LabFingerprint -Before @() -After @('x') | Should -Be @('+ x')
    }
}
