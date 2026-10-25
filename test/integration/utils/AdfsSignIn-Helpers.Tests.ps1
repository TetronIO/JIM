# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for AdfsSignIn-Helpers.ps1, the runner's side of the AD FS sign-in checks (#2441).

.DESCRIPTION
    The checks themselves run in the lab's toolbox container. What the runner decides is proven here with docker
    replaced: how it reads a check's result, and that a person's name and password reach the container through the
    docker exec's environment and never its command line.
#>

BeforeAll {
    . "$PSScriptRoot/AdfsSignIn-Helpers.ps1"
}

Describe 'ConvertFrom-AdfsSignInCheckOutput' {
    It 'reads the result line, past the module''s own output before it' {
        $result = ConvertFrom-AdfsSignInCheckOutput -Line @('Connected to JIM', 'ADFS-SIGN-IN-RESULT {"check":"Portal","ok":true,"userInfo":{"authorised":true}}')
        $result.check | Should -Be 'Portal'
        $result.ok | Should -BeTrue
        $result.userInfo.authorised | Should -BeTrue
    }

    It 'refuses output with no result line, naming the last lines' {
        { ConvertFrom-AdfsSignInCheckOutput -Line @('pwsh: command not found') } | Should -Throw '*printed no result*command not found*'
    }
}

Describe 'Invoke-AdfsSignInCheck' {
    BeforeEach {
        # Pester's Mock, scoped to this test, of the helpers' one docker call: a global docker function would leak into
        # every other test file in the run (it broke ComposeFiles.Tests.ps1's discovery, which runs docker compose).
        $script:Calls = [System.Collections.Generic.List[object]]::new()
        Mock Invoke-AdfsSignInDocker {
            $script:Calls.Add([pscustomobject]@{
                    Arguments = @($Arguments)
                    UserName  = $env:JIM_SIGN_IN_USERNAME
                    Password  = $env:JIM_SIGN_IN_PASSWORD
                })
            'ADFS-SIGN-IN-RESULT {"check":"Portal","ok":true}'
        }
    }

    It 'passes the name and password through the exec''s environment, never its command line, and clears them after' {
        $result = Invoke-AdfsSignInCheck -Check Portal -JimUrl 'https://jim.web:8443' -UserName 'jim-sso-admin@panoply.local' -Password 'placeholder-password'

        $result.ok | Should -BeTrue
        $call = $script:Calls[0]
        $call.UserName | Should -Be 'jim-sso-admin@panoply.local'
        $call.Password | Should -Be 'placeholder-password'
        ($call.Arguments -join ' ') | Should -Not -Match 'placeholder-password'
        ($call.Arguments -join ' ') | Should -Match '-e JIM_SIGN_IN_USERNAME -e JIM_SIGN_IN_PASSWORD jim-ldap-toolbox pwsh'
        ($call.Arguments -join ' ') | Should -Match '-Check Portal -JimUrl https://jim.web:8443'
        $env:JIM_SIGN_IN_USERNAME | Should -BeNullOrEmpty
        $env:JIM_SIGN_IN_PASSWORD | Should -BeNullOrEmpty
    }
}
