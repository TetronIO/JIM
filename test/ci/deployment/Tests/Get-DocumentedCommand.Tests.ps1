# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for test/ci/deployment/Get-DocumentedCommand.ps1.

.DESCRIPTION
    Reads commands out of pages shaped as the documentation writes them, a heading, then tabs per runtime each
    holding a fenced block, and checks the script returns the block the deployment-boot check is to run.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Get-DocumentedCommand.ps1')).Path
    $script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' '..')).Path

    function New-Page {
        param([string]$Markdown)
        $path = Join-Path $TestDrive "$([Guid]::NewGuid().ToString('N')).md"
        Set-Content -Path $path -Value $Markdown
        $path
    }

    $script:Page = New-Page @'
# Page

## Taking a backup

### 1. Back up the database

=== "Docker"

    ```bash
    docker exec database dump
    ```

=== "Podman"

    ```bash
    podman exec database dump
    ```

### 2. Back up the keys

=== "Podman"

    ```bash
    podman volume export keys
    ```

=== "Docker"

    ```bash
    docker run --rm \
      -v keys:/keys:ro \
      image tar czf /backup/keys.tar.gz -C /keys .
    ```

## Restoring {#restoring}

1. **Restore the keys first**:

    === "Docker"

        Some words first.

        ```bash
        docker run --rm -v keys:/keys image sh -c "rm -rf /keys/*"
        ```

### Only Podman here

=== "Podman"

    ```bash
    podman run image
    ```

## Docker later

=== "Docker"

    ```bash
    docker run later
    ```
'@
}

Describe 'Get-DocumentedCommand' {
    It 'returns the block under the named tab of the named section, without its indentation' {
        $command = & $script:ScriptPath -Path $script:Page -Heading '2. Back up the keys' -Tab 'Docker'

        $command | Should -BeExactly "docker run --rm \`n  -v keys:/keys:ro \`n  image tar czf /backup/keys.tar.gz -C /keys ."
    }

    It 'finds the tab in a list item, past words before the block, and matches a heading without its anchor' {
        $command = & $script:ScriptPath -Path $script:Page -Heading 'Restoring' -Tab 'Docker'

        $command | Should -BeExactly 'docker run --rm -v keys:/keys image sh -c "rm -rf /keys/*"'
    }

    It 'stops at the next heading of the same level, rather than taking a later section''s block' {
        { & $script:ScriptPath -Path $script:Page -Heading 'Only Podman here' -Tab 'Docker' } |
            Should -Throw '*Only Podman here*Docker*'
    }

    It 'stops when the page has no such heading' {
        { & $script:ScriptPath -Path $script:Page -Heading '3. Back up the logs' -Tab 'Docker' } |
            Should -Throw '*3. Back up the logs*'
    }

    It 'finds the Docker key backup and restore in Backup & Disaster Recovery, which the deployment-boot check runs' {
        $page = Join-Path $script:RepositoryRoot 'docs' 'administration' 'backup-recovery.md'

        $backup = & $script:ScriptPath -Path $page -Heading '2. Back up the encryption keys' -Tab 'Docker'
        $restore = & $script:ScriptPath -Path $page -Heading 'Restoring' -Tab 'Docker'

        $backup | Should -BeLike '*jim-keys-volume*jim-keys-*.tar.gz*'
        $restore | Should -BeLike '*jim-keys-volume*jim-keys-*.tar.gz*'
    }
}
