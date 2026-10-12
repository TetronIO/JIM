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
