# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/screenshots/CapturePreflight.ps1.

.DESCRIPTION
    Capture-Screenshots.ps1 signs in through the portal's identity provider before it captures anything (#582). When
    the address the portal sends browsers to is not answering, every image fails with the same "connection refused"
    and nothing says why; the preflight names the address and the usual cause before a browser starts.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot '..' 'screenshots' 'CapturePreflight.ps1')
}

Describe 'Get-ScreenshotSignInProblem' {
    It 'names the sign-in address when the portal redirects to one that does not answer' {
        Mock Get-PortalSignInRedirect { 'http://localhost:8181/realms/jim/protocol/openid-connect/auth?client_id=jim-web' }
        Mock Test-HttpOriginAnswers { $false }

        $problem = Get-ScreenshotSignInProblem -BaseUrl 'http://localhost:5200'

        $problem | Should -Match ([regex]::Escape('http://localhost:8181'))
        Should -Invoke Test-HttpOriginAnswers -Times 1 -ParameterFilter { $Origin -eq 'http://localhost:8181' }
    }

    It 'reports nothing when the sign-in address answers' {
        Mock Get-PortalSignInRedirect { 'http://localhost:8181/realms/jim/protocol/openid-connect/auth' }
        Mock Test-HttpOriginAnswers { $true }

        Get-ScreenshotSignInProblem -BaseUrl 'http://localhost:5200' | Should -BeNullOrEmpty
    }

    It 'reports nothing, and probes nothing, when the portal does not redirect' {
        Mock Get-PortalSignInRedirect { $null }
        Mock Test-HttpOriginAnswers { $false }

        Get-ScreenshotSignInProblem -BaseUrl 'http://localhost:5200' | Should -BeNullOrEmpty
        Should -Invoke Test-HttpOriginAnswers -Times 0
    }

    It 'reports nothing, and probes nothing, when the portal redirects within itself' {
        Mock Get-PortalSignInRedirect { '/Account/Login?returnUrl=%2F' }
        Mock Test-HttpOriginAnswers { $false }

        Get-ScreenshotSignInProblem -BaseUrl 'http://localhost:5200' | Should -BeNullOrEmpty
        Should -Invoke Test-HttpOriginAnswers -Times 0
    }

    It 'treats the portal itself not answering as a problem, naming the portal address' {
        Mock Get-PortalSignInRedirect { throw [System.Net.Http.HttpRequestException]::new('Connection refused') }
        Mock Test-HttpOriginAnswers { $true }

        $problem = Get-ScreenshotSignInProblem -BaseUrl 'http://localhost:5200'

        $problem | Should -Match ([regex]::Escape('http://localhost:5200'))
    }
}
