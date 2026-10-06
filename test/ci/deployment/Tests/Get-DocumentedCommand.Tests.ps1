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

### 3. Check the keys

=== "Podman"

    ```bash
    podman check
    ```

=== "Docker"

    Words only, with no command.

Then, on either:

```bash
tar tzf keys.tar.gz | grep key-
```

## Restoring {#restoring}

1. **Restore the keys first**:

    === "Docker"

        Some words first.

        ```bash
        docker run --rm -v keys:/keys image sh -c "rm -rf /keys/*"
        ```

2. **Restore the database** from the dump:

    === "Podman"

        ```bash
        podman exec database restore
        ```

    === "Docker"

        ```bash
        docker exec database restore
        ```

3. **Start the stack**:

    === "Docker"

        ```bash
        docker compose up
        ```

### Only Podman here

=== "Podman"

    ```bash
    podman run image
    ```

## Rootless helpers

Define these first:

```bash
helper() { echo "$@"; }
```

Then:

```bash
helper status
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

    It 'returns the block under the named tab of the named step, past an earlier step''s' {
        $command = & $script:ScriptPath -Path $script:Page -Heading 'Restoring' -Step 'Restore the database' -Tab 'Docker'

        $command | Should -BeExactly 'docker exec database restore'
    }

    It 'stops when the section has no such step, rather than taking another step''s block' {
        { & $script:ScriptPath -Path $script:Page -Heading 'Restoring' -Step 'Restore the logs' -Tab 'Docker' } |
            Should -Throw '*Restoring*Restore the logs*Docker*'
    }

    It 'stops at the next heading of the same level, rather than taking a later section''s block' {
        { & $script:ScriptPath -Path $script:Page -Heading 'Only Podman here' -Tab 'Docker' } |
            Should -Throw '*Only Podman here*Docker*'
    }

    It 'returns the section''s first block when no tab is named, for a section that gives one command for all' {
        $command = & $script:ScriptPath -Path $script:Page -Heading 'Rootless helpers'

        $command | Should -BeExactly 'helper() { echo "$@"; }'
    }

    It 'stops when the page has no such heading' {
        { & $script:ScriptPath -Path $script:Page -Heading '4. Back up the logs' -Tab 'Docker' } |
            Should -Throw '*4. Back up the logs*'
    }

    It 'returns the first block outside any tab when no tab is named, past the tabs before it' {
        $command = & $script:ScriptPath -Path $script:Page -Heading '3. Check the keys'

        $command | Should -BeExactly 'tar tzf keys.tar.gz | grep key-'
    }

    It 'does not take a block after the end of a tab as the tab''s own' {
        { & $script:ScriptPath -Path $script:Page -Heading '3. Check the keys' -Tab 'Docker' } |
            Should -Throw '*3. Check the keys*Docker*'
    }

    It 'stops, when no tab is named, at a section whose every block is in a tab' {
        { & $script:ScriptPath -Path $script:Page -Heading '1. Back up the database' } |
            Should -Throw '*1. Back up the database*outside a tab*'
    }

    It 'finds the key backup, its check and the restore in Backup & Disaster Recovery, which the deployment-boot check runs' {
        $page = Join-Path $script:RepositoryRoot 'docs' 'administration' 'backup-recovery.md'

        foreach ($tab in 'Docker', 'Podman') {
            $backup = & $script:ScriptPath -Path $page -Heading '2. Back up the encryption keys' -Tab $tab
            $restore = & $script:ScriptPath -Path $page -Heading 'Restoring' -Tab $tab

            $backup | Should -BeLike '*jim-keys-volume*jim-keys-*.tar.gz*'
            $restore | Should -BeLike '*jim-keys-volume*jim-keys-*.tar.gz*'
        }
        & $script:ScriptPath -Path $page -Heading '2. Back up the encryption keys' | Should -BeLike 'tar *jim-keys-*.tar.gz*key-*'
    }

    It 'finds the jim-podman function in Running on Podman, which the rootless deployment-boot leg runs the Podman commands with' {
        $page = Join-Path $script:RepositoryRoot 'docs' 'administration' 'podman.md'

        $commands = & $script:ScriptPath -Path $page -Heading 'Rootless commands'

        $commands | Should -Match '(?m)^jim-podman\(\) \{.*\}$'
    }

    It 'finds the jim-systemctl function in Running on Podman, which the rootless deployment-boot leg stops and starts JIM with' {
        $page = Join-Path $script:RepositoryRoot 'docs' 'administration' 'podman.md'

        $commands = & $script:ScriptPath -Path $page -Heading 'Rootless commands'

        $commands | Should -Match '(?m)^jim-systemctl\(\) \{.*\}$'
    }

    It 'finds the database backup and restore in Backup & Disaster Recovery, for Docker and Podman, which the deployment-boot check runs' {
        $page = Join-Path $script:RepositoryRoot 'docs' 'administration' 'backup-recovery.md'

        foreach ($tab in 'Docker', 'Podman') {
            $backup = & $script:ScriptPath -Path $page -Heading '1. Back up the database' -Tab $tab
            $restore = & $script:ScriptPath -Path $page -Heading 'Restoring' -Step 'Restore the database' -Tab $tab

            $backup | Should -BeLike '*pg_dump*jim-db-*.dump*'
            $restore | Should -BeLike '*jim-db-*.dump*'
            $restore | Should -BeLike '*pg_restore*'
        }
    }
}
