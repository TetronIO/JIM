# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/screenshots/TourPullRequest.ps1.

.DESCRIPTION
    A release's screenshot refresh reaches junctional.io as a pull request (#582), committed through GitHub's Git Data
    API with the jim-automation app's token, so GitHub signs the commit. These tests replace the API with a recorder.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'screenshots' 'TourPullRequest.ps1')
}

Describe 'Invoke-GitHubApi' {
    BeforeAll {
        function New-HttpFailure([int]$Status, [string]$Message) {
            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]$Status)
            $exception = [Microsoft.PowerShell.Commands.HttpResponseException]::new($Message, $response)
            $record = [System.Management.Automation.ErrorRecord]::new($exception, 'WebCmdletWebResponseException', 'InvalidOperation', $null)
            $record.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($Message)
            return $record
        }
    }

    It 'reports a missing resource as not found' {
        Mock Invoke-RestMethod { throw (New-HttpFailure 404 '{"message":"Not Found"}') }

        { Invoke-GitHubApi -Method GET -Path 'repos/o/r/git/ref/heads/x' -Token 't' } | Should -Throw -ExceptionType ([GitHubApiNotFoundException])
    }

    It 'reports updating a branch that does not exist as not found' {
        Mock Invoke-RestMethod { throw (New-HttpFailure 422 '{"message":"Reference does not exist"}') }

        { Invoke-GitHubApi -Method PATCH -Path 'repos/o/r/git/refs/heads/x' -Body @{ sha = 'a' } -Token 't' } | Should -Throw -ExceptionType ([GitHubApiNotFoundException])
    }

    It 'reports any other refusal as a failure, with what GitHub said' {
        Mock Invoke-RestMethod { throw (New-HttpFailure 403 '{"message":"Resource not accessible by integration"}') }

        { Invoke-GitHubApi -Method POST -Path 'repos/o/r/pulls' -Body @{} -Token 't' } | Should -Throw '*403*not accessible by integration*'
    }

    # Invoke-RestMethod writes a JSON array to the pipeline as one object, not as its items (the unary comma below
    # does the same). Passed on as it is, an empty list reaches the caller as one item that is itself an empty list.
    It 'answers an empty list as no items' {
        Mock Invoke-RestMethod { , @() }

        @(Invoke-GitHubApi -Method GET -Path 'repos/o/r/pulls?head=o:x&state=open' -Token 't').Count | Should -Be 0
    }

    It 'answers a list as its items' {
        Mock Invoke-RestMethod { , @([pscustomobject]@{ number = 7 }) }

        $items = @(Invoke-GitHubApi -Method GET -Path 'repos/o/r/pulls?head=o:x&state=open' -Token 't')

        $items.Count | Should -Be 1
        $items[0].number | Should -Be 7
    }

    It 'sends the token and a JSON body' {
        Mock Invoke-RestMethod { @{ ok = $true } }

        Invoke-GitHubApi -Method POST -Path 'repos/o/r/git/blobs' -Body @{ content = 'x' } -Token 'secret' | Out-Null

        Should -Invoke Invoke-RestMethod -Times 1 -ParameterFilter {
            $Uri -eq 'https://api.github.com/repos/o/r/git/blobs' -and $Headers.Authorization -eq 'Bearer secret' -and
            $ContentType -eq 'application/json' -and ($Body | ConvertFrom-Json).content -eq 'x'
        }
    }
}

Describe 'ConvertFrom-GitStatusLine' {
    It 'reads added, modified and deleted paths, and leaves directories to git' {
        $changes = ConvertFrom-GitStatusLine -Line @(
            '?? src/assets/tour/v0.17.0/homepage-dark.png',
            ' M src/i18n/en-GB.ts',
            'M  src/i18n/en-US.ts',
            ' D src/assets/tour/v0.10.3/homepage-dark.png'
        )

        $changes.Count | Should -Be 4
        ($changes | Where-Object Path -eq 'src/assets/tour/v0.10.3/homepage-dark.png').Deleted | Should -BeTrue
        ($changes | Where-Object Path -eq 'src/i18n/en-GB.ts').Deleted | Should -BeFalse
        ($changes | Where-Object Path -eq 'src/assets/tour/v0.17.0/homepage-dark.png').Deleted | Should -BeFalse
    }

    It 'refuses a rename, which a refresh never makes' {
        { ConvertFrom-GitStatusLine -Line @('R  a.png -> b.png') } | Should -Throw '*a.png*'
    }
}

Describe 'Publish-SiteChangeCommit' {
    BeforeEach {
        $script:calls = [System.Collections.Generic.List[object]]::new()
        $site = Join-Path $TestDrive ([guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path (Join-Path $site 'src' 'i18n') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $site 'src' 'i18n' 'en-GB.ts') -Value 'imageVersion: "v0.17.0"' -NoNewline

        Mock Invoke-GitHubApi {
            $script:calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body })
            switch -Regex ("$Method $Path") {
                '^GET .*/git/ref/heads/main$' { return @{ object = @{ sha = 'main-sha' } } }
                '^GET .*/git/commits/main-sha$' { return @{ tree = @{ sha = 'main-tree' } } }
                '^POST .*/git/blobs$' { return @{ sha = "blob-$($script:calls.Count)" } }
                '^POST .*/git/trees$' { return @{ sha = 'new-tree' } }
                '^POST .*/git/commits$' { return @{ sha = 'new-commit' } }
                default { return @{} }
            }
        }
    }

    It 'commits additions as blobs and deletions as removed paths, on top of the base branch' {
        $changes = @(
            [pscustomobject]@{ Path = 'src/i18n/en-GB.ts'; Deleted = $false },
            [pscustomobject]@{ Path = 'src/assets/tour/v0.10.3/homepage-dark.png'; Deleted = $true }
        )

        $sha = Publish-SiteChangeCommit -Repository 'TetronIO/junctional.io' -BaseBranch 'main' -Branch 'screenshots/v0.17.0' `
            -SitePath $site -Change $changes -Message 'Refresh' -Token 'token'

        $sha | Should -Be 'new-commit'
        @($script:calls | Where-Object { $_.Path -like '*/git/blobs' }).Count | Should -Be 1
        $tree = ($script:calls | Where-Object { $_.Path -like '*/git/trees' }).Body
        $tree.base_tree | Should -Be 'main-tree'
        ($tree.tree | Where-Object path -eq 'src/assets/tour/v0.10.3/homepage-dark.png').sha | Should -BeNullOrEmpty
        ($tree.tree | Where-Object path -eq 'src/i18n/en-GB.ts').sha | Should -Not -BeNullOrEmpty
        ($script:calls | Where-Object { $_.Path -like '*/git/commits' -and $_.Method -eq 'POST' }).Body.parents | Should -Be @('main-sha')
    }

    It 'sends a file''s bytes as base64' {
        $changes = @([pscustomobject]@{ Path = 'src/i18n/en-GB.ts'; Deleted = $false })

        Publish-SiteChangeCommit -Repository 'TetronIO/junctional.io' -BaseBranch 'main' -Branch 'screenshots/v0.17.0' `
            -SitePath $site -Change $changes -Message 'Refresh' -Token 'token' | Out-Null

        $blob = ($script:calls | Where-Object { $_.Path -like '*/git/blobs' }).Body
        $blob.encoding | Should -Be 'base64'
        [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($blob.content)) | Should -Be 'imageVersion: "v0.17.0"'
    }

    It 'moves an existing branch to the new commit' {
        Publish-SiteChangeCommit -Repository 'TetronIO/junctional.io' -BaseBranch 'main' -Branch 'screenshots/v0.17.0' `
            -SitePath $site -Change @() -Message 'Refresh' -Token 'token' | Out-Null

        $update = $script:calls | Where-Object { $_.Method -eq 'PATCH' -and $_.Path -like '*/git/refs/heads/screenshots/v0.17.0' }
        $update.Body.sha | Should -Be 'new-commit'
        $update.Body.force | Should -BeTrue
        @($script:calls | Where-Object { $_.Method -eq 'POST' -and $_.Path -like '*/git/refs' }).Count | Should -Be 0
    }

    It 'creates the branch when it does not exist yet' {
        Mock Invoke-GitHubApi {
            $script:calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body })
            switch -Regex ("$Method $Path") {
                '^GET .*/git/ref/heads/main$' { return @{ object = @{ sha = 'main-sha' } } }
                '^GET .*/git/commits/main-sha$' { return @{ tree = @{ sha = 'main-tree' } } }
                '^POST .*/git/trees$' { return @{ sha = 'new-tree' } }
                '^POST .*/git/commits$' { return @{ sha = 'new-commit' } }
                '^PATCH .*/git/refs/heads/' { throw [GitHubApiNotFoundException]::new('Reference does not exist') }
                default { return @{} }
            }
        }

        Publish-SiteChangeCommit -Repository 'TetronIO/junctional.io' -BaseBranch 'main' -Branch 'screenshots/v0.17.0' `
            -SitePath $site -Change @() -Message 'Refresh' -Token 'token' | Out-Null

        $create = $script:calls | Where-Object { $_.Method -eq 'POST' -and $_.Path -like '*/git/refs' }
        $create.Body.ref | Should -Be 'refs/heads/screenshots/v0.17.0'
        $create.Body.sha | Should -Be 'new-commit'
    }
}

Describe 'Open-TourPullRequest' {
    BeforeEach {
        $script:calls = [System.Collections.Generic.List[object]]::new()
    }

    It 'opens a pull request when the branch has none' {
        Mock Invoke-GitHubApi {
            $script:calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body })
            if ($Method -eq 'GET') { return @() }
            return @{ html_url = 'https://github.com/TetronIO/junctional.io/pull/7' }
        }

        $url = Open-TourPullRequest -Repository 'TetronIO/junctional.io' -BaseBranch 'main' -Branch 'screenshots/v0.17.0' `
            -Title 'Refresh' -Body 'Body' -Token 'token'

        $url | Should -Be 'https://github.com/TetronIO/junctional.io/pull/7'
        $create = $script:calls | Where-Object Method -eq 'POST'
        $create.Body.head | Should -Be 'screenshots/v0.17.0'
        $create.Body.base | Should -Be 'main'
        ($script:calls | Where-Object Method -eq 'GET').Path | Should -Match ([regex]::Escape('head=TetronIO:screenshots/v0.17.0'))
    }

    # Through the real Invoke-GitHubApi, with GitHub answering as Invoke-RestMethod delivers it: the first refresh for a
    # version, when the branch has no pull request yet.
    It 'opens a pull request when GitHub lists none for the branch' {
        Mock Invoke-RestMethod {
            $script:calls.Add([pscustomobject]@{ Method = $Method; Uri = $Uri })
            if ($Method -eq 'GET') { return , @() }
            return [pscustomobject]@{ html_url = 'https://github.com/TetronIO/junctional.io/pull/7' }
        }

        $url = Open-TourPullRequest -Repository 'TetronIO/junctional.io' -BaseBranch 'main' -Branch 'screenshots/v0.17.0' `
            -Title 'Refresh' -Body 'Body' -Token 'token'

        $url | Should -Be 'https://github.com/TetronIO/junctional.io/pull/7'
        @($script:calls | Where-Object Method -eq 'POST').Count | Should -Be 1
    }

    It 'updates the open pull request rather than opening a second one' {
        Mock Invoke-GitHubApi {
            $script:calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body })
            if ($Method -eq 'GET') { return @(@{ number = 7; html_url = 'https://github.com/TetronIO/junctional.io/pull/7' }) }
            return @{ html_url = 'https://github.com/TetronIO/junctional.io/pull/7' }
        }

        $url = Open-TourPullRequest -Repository 'TetronIO/junctional.io' -BaseBranch 'main' -Branch 'screenshots/v0.17.0' `
            -Title 'Refresh' -Body 'New body' -Token 'token'

        $url | Should -Be 'https://github.com/TetronIO/junctional.io/pull/7'
        @($script:calls | Where-Object Method -eq 'POST').Count | Should -Be 0
        ($script:calls | Where-Object Method -eq 'PATCH').Body.body | Should -Be 'New body'
    }
}

# A release publishes its screenshots by merging these pull requests, from whichever of the release workflow and the
# screenshots job finishes last. Invoke-RestMethod answers as it really does: a JSON list arrives as one object.
Describe 'Test-JimReleasePublished' {
    BeforeAll {
        function New-HttpFailure([int]$Status, [string]$Message) {
            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]$Status)
            $exception = [Microsoft.PowerShell.Commands.HttpResponseException]::new($Message, $response)
            $record = [System.Management.Automation.ErrorRecord]::new($exception, 'WebCmdletWebResponseException', 'InvalidOperation', $null)
            $record.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($Message)
            return $record
        }
    }

    It 'is false while the version has no release' {
        Mock Invoke-RestMethod { throw (New-HttpFailure 404 '{"message":"Not Found"}') }

        Test-JimReleasePublished -Version 'v0.17.0' -Token 't' | Should -BeFalse
    }

    It 'is true once the release is published' {
        Mock Invoke-RestMethod { [pscustomobject]@{ tag_name = 'v0.17.0'; draft = $false } }

        Test-JimReleasePublished -Version 'v0.17.0' -Token 't' | Should -BeTrue
        Should -Invoke Invoke-RestMethod -ParameterFilter { $Uri -eq 'https://api.github.com/repos/TetronIO/JIM/releases/tags/v0.17.0' }
    }

    It 'is false for a draft' {
        Mock Invoke-RestMethod { [pscustomobject]@{ tag_name = 'v0.17.0'; draft = $true } }

        Test-JimReleasePublished -Version 'v0.17.0' -Token 't' | Should -BeFalse
    }
}

Describe 'Merge-ScreenshotPullRequest' {
    BeforeAll {
        function New-HttpFailure([int]$Status, [string]$Message) {
            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]$Status)
            $exception = [Microsoft.PowerShell.Commands.HttpResponseException]::new($Message, $response)
            $record = [System.Management.Automation.ErrorRecord]::new($exception, 'WebCmdletWebResponseException', 'InvalidOperation', $null)
            $record.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($Message)
            return $record
        }

        $script:pull = [pscustomobject]@{
            number = 12; html_url = 'https://github.com/TetronIO/tetron.io/pull/12'
            head = [pscustomobject]@{ sha = 'abc123' }; created_at = '2026-01-01T00:00:00Z'
        }
        # The pull request as GET pulls/12 answers it: its mergeable state, whether it is merged, and its head commit.
        function New-PullState([string]$MergeableState, [switch]$Merged, [string]$State = 'open', [string]$Sha = 'abc123') {
            [pscustomobject]@{
                number = 12; state = $State; merged = [bool]$Merged; mergeable_state = $MergeableState
                head = [pscustomobject]@{ sha = $Sha }
            }
        }

        # Answers each API call by its path: GET pulls/12 from $script:pullStates in turn (the last one repeats), and
        # the merge refuses any method listed in $script:refusedMethods.
        function Set-GitHubAnswers {
            Mock Invoke-RestMethod {
                $script:calls.Add([pscustomobject]@{ Method = $Method; Uri = $Uri; Body = $Body })
                if ($Uri -like '*/pulls?head=*state=open') { return , @($script:openPulls) }
                if ($Uri -like '*/pulls?head=*state=closed') { return , @($script:closedPulls) }
                if ($Method -eq 'GET' -and $Uri -like '*/pulls/12') {
                    if ($script:pullStates.Count -gt 1) { return $script:pullStates.Dequeue() }
                    return $script:pullStates.Peek()
                }
                if ($Method -eq 'PUT' -and $Uri -like '*/pulls/12/merge') {
                    $chosen = ($Body | ConvertFrom-Json).merge_method
                    if ($chosen -in $script:refusedMethods) { throw (New-HttpFailure 405 "{`"message`":`"$chosen merges are not allowed on this repository.`"}") }
                    return [pscustomobject]@{ merged = $true }
                }
                if ($Method -eq 'DELETE') {
                    if ($script:deleteRefused) { throw (New-HttpFailure 403 '{"message":"Resource not accessible by integration"}') }
                    return $null
                }
                throw "Unexpected call: $Method $Uri"
            }
        }
    }

    BeforeEach {
        $script:calls = [System.Collections.Generic.List[object]]::new()
        $script:pullStates = [System.Collections.Generic.Queue[object]]::new()
        $script:openPulls = @($script:pull)
        $script:closedPulls = @()
        $script:refusedMethods = @()
        $script:deleteRefused = $false
    }

    It 'merges the open pull request once its checks have passed, then deletes the branch' {
        $script:pullStates.Enqueue((New-PullState 'clean'))
        Set-GitHubAnswers

        $result = Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0

        $result.Status | Should -Be 'Merged'
        $result.Url | Should -Be 'https://github.com/TetronIO/tetron.io/pull/12'
        $merge = $script:calls | Where-Object Method -eq 'PUT' | Select-Object -Last 1
        ($merge.Body | ConvertFrom-Json).sha | Should -Be 'abc123'
        ($script:calls | Where-Object Method -eq 'DELETE').Uri | Should -Be 'https://api.github.com/repos/TetronIO/tetron.io/git/refs/heads/screenshots/v0.17.0'
    }

    It 'waits while GitHub works out the state and a check runs, then merges' {
        $script:pullStates.Enqueue((New-PullState 'unknown'))
        $script:pullStates.Enqueue((New-PullState 'unstable'))
        $script:pullStates.Enqueue((New-PullState 'clean'))
        Set-GitHubAnswers

        $result = Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0

        $result.Status | Should -Be 'Merged'
        @($script:calls | Where-Object { $_.Method -eq 'GET' -and $_.Uri -like '*/pulls/12' }).Count | Should -Be 3
    }

    # A re-run of the screenshots job moves the branch; what merges is what was seen to pass.
    It 'merges the head commit it saw pass, not the one it first found' {
        $script:pullStates.Enqueue((New-PullState 'clean' -Sha 'def456'))
        Set-GitHubAnswers

        Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0 | Out-Null

        (($script:calls | Where-Object Method -eq 'PUT' | Select-Object -Last 1).Body | ConvertFrom-Json).sha | Should -Be 'def456'
    }

    It 'gives up on checks that do not pass in time' {
        $script:pullStates.Enqueue((New-PullState 'unstable'))
        Set-GitHubAnswers

        { Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0 -CheckTimeoutSeconds 0 } |
            Should -Throw '*not all passed after 0 seconds*unstable*'
        @($script:calls | Where-Object Method -eq 'PUT').Count | Should -Be 0
    }

    It 'refuses at once a pull request that conflicts with its base' {
        $script:pullStates.Enqueue((New-PullState 'dirty'))
        Set-GitHubAnswers

        { Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0 } |
            Should -Throw '*GitHub reports it as dirty*'
        @($script:calls | Where-Object Method -eq 'PUT').Count | Should -Be 0
    }

    # Closing the pull request is how a person keeps a release's screenshots off the site.
    It 'refuses a pull request closed while it waited' {
        $script:pullStates.Enqueue((New-PullState 'unstable'))
        $script:pullStates.Enqueue((New-PullState 'clean' -State 'closed'))
        Set-GitHubAnswers

        { Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0 } |
            Should -Throw '*was closed*'
        @($script:calls | Where-Object Method -eq 'PUT').Count | Should -Be 0
    }

    It 'merges with a merge commit where the repository does not allow squashing' {
        $script:pullStates.Enqueue((New-PullState 'clean'))
        $script:refusedMethods = @('squash')
        Set-GitHubAnswers

        $result = Merge-ScreenshotPullRequest -Repository 'TetronIO/junctional.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0

        $result.Status | Should -Be 'Merged'
        (($script:calls | Where-Object Method -eq 'PUT' | Select-Object -Last 1).Body | ConvertFrom-Json).merge_method | Should -Be 'merge'
    }

    # Both sides found it open; the other merged it while this one waited on the checks.
    It 'reports a pull request merged while it waited as already merged' {
        $script:pullStates.Enqueue((New-PullState 'unstable'))
        $script:pullStates.Enqueue((New-PullState 'unknown' -Merged -State 'closed'))
        Set-GitHubAnswers

        $result = Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0

        $result.Status | Should -Be 'AlreadyMerged'
        $result.Url | Should -Be 'https://github.com/TetronIO/tetron.io/pull/12'
        @($script:calls | Where-Object Method -eq 'PUT').Count | Should -Be 0
    }

    It 'reports a pull request merged between its checks passing and the merge as already merged' {
        $script:pullStates.Enqueue((New-PullState 'clean'))
        $script:pullStates.Enqueue((New-PullState 'unknown' -Merged -State 'closed'))
        $script:refusedMethods = @('squash', 'merge', 'rebase')
        Set-GitHubAnswers

        $result = Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0

        $result.Status | Should -Be 'AlreadyMerged'
    }

    It 'still refuses when no method merges and the pull request is open' {
        $script:pullStates.Enqueue((New-PullState 'clean'))
        $script:refusedMethods = @('squash', 'merge', 'rebase')
        Set-GitHubAnswers

        { Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0 } |
            Should -Throw 'Not merging*squash*'
    }

    It 'reports the merge even when the branch cannot be deleted' {
        $script:pullStates.Enqueue((New-PullState 'clean'))
        $script:deleteRefused = $true
        Set-GitHubAnswers

        $result = Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't' -PollSeconds 0 -WarningAction SilentlyContinue

        $result.Status | Should -Be 'Merged'
    }

    It 'reports a branch with no pull request as not open' {
        $script:openPulls = @()
        Set-GitHubAnswers

        (Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't').Status | Should -Be 'NotOpen'
    }

    # The release workflow and the screenshots job can both try; the second finds the work done.
    It 'reports a pull request the other side already merged' {
        $script:openPulls = @()
        $script:closedPulls = @([pscustomobject]@{ number = 12; html_url = 'https://github.com/TetronIO/tetron.io/pull/12'; merged_at = '2026-01-01T00:00:00Z' })
        Set-GitHubAnswers

        $result = Merge-ScreenshotPullRequest -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Token 't'

        $result.Status | Should -Be 'AlreadyMerged'
        $result.Url | Should -Be 'https://github.com/TetronIO/tetron.io/pull/12'
    }
}

Describe 'Invoke-ScreenshotMergeIfReleased' {
    It 'leaves the pull request for the release workflow while the release is not out' {
        Mock Test-JimReleasePublished { $false }
        Mock Merge-ScreenshotPullRequest { throw 'should not merge' }

        $result = Invoke-ScreenshotMergeIfReleased -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Version 'v0.17.0' -Token 't' -ReleaseToken 'r'

        $result | Should -BeNullOrEmpty
        Should -Invoke Test-JimReleasePublished -Times 1 -ParameterFilter { $Version -eq 'v0.17.0' -and $Token -eq 'r' }
    }

    It 'merges straight away when the release is already published' {
        Mock Test-JimReleasePublished { $true }
        Mock Merge-ScreenshotPullRequest { [pscustomobject]@{ Status = 'Merged'; Url = 'https://github.com/TetronIO/tetron.io/pull/12' } }

        $result = Invoke-ScreenshotMergeIfReleased -Repository 'TetronIO/tetron.io' -Branch 'screenshots/v0.17.0' -Version 'v0.17.0' -Token 't' -ReleaseToken 'r'

        $result.Status | Should -Be 'Merged'
        Should -Invoke Merge-ScreenshotPullRequest -Times 1 -ParameterFilter { $Repository -eq 'TetronIO/tetron.io' -and $Branch -eq 'screenshots/v0.17.0' -and $Token -eq 't' }
    }
}

Describe 'Invoke-ReleaseScreenshotMerge' {
    It "merges each site's pull request for the release's branch" {
        Mock Merge-ScreenshotPullRequest { [pscustomobject]@{ Status = 'Merged'; Url = "https://github.com/$Repository/pull/3" } }

        $results = @(Invoke-ReleaseScreenshotMerge -Version 'v0.17.0' -Repository 'TetronIO/junctional.io', 'TetronIO/tetron.io' -Token 't')

        $results.Repository | Should -Be @('TetronIO/junctional.io', 'TetronIO/tetron.io')
        $results.Status | Should -Be @('Merged', 'Merged')
        Should -Invoke Merge-ScreenshotPullRequest -Times 2 -ParameterFilter { $Branch -eq 'screenshots/v0.17.0' -and $Token -eq 't' }
    }

    It 'carries on to the next site after one fails, and reports why' {
        Mock Merge-ScreenshotPullRequest { throw 'Not merging https://github.com/TetronIO/junctional.io/pull/3: Build ended failure.' } -ParameterFilter { $Repository -eq 'TetronIO/junctional.io' }
        Mock Merge-ScreenshotPullRequest { [pscustomobject]@{ Status = 'NotOpen'; Url = $null } } -ParameterFilter { $Repository -eq 'TetronIO/tetron.io' }

        $results = @(Invoke-ReleaseScreenshotMerge -Version 'v0.17.0' -Repository 'TetronIO/junctional.io', 'TetronIO/tetron.io' -Token 't')

        $results.Status | Should -Be @('Failed', 'NotOpen')
        $results[0].Message | Should -BeLike '*Build ended failure*'
    }
}
