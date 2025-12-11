#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the JIM PowerShell module.

.DESCRIPTION
    Tests module structure, manifest, and cmdlet availability.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..'
    $ModuleName = 'JIM'

    # Remove module if already loaded
    Get-Module $ModuleName -ErrorAction SilentlyContinue | Remove-Module -Force

    # Import the module
    Import-Module $ModulePath -Force
}

AfterAll {
    # Clean up
    Get-Module $ModuleName -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Module: JIM' {

    Context 'Module Structure' {

        It 'Should have a module manifest' {
            $manifestPath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
            Test-Path $manifestPath | Should -BeTrue
        }

        It 'Should have a valid module manifest' {
            $manifestPath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
            { Test-ModuleManifest -Path $manifestPath -ErrorAction Stop } | Should -Not -Throw
        }

        It 'Should have a root module file' {
            $modulePath = Join-Path $PSScriptRoot '..' 'JIM.psm1'
            Test-Path $modulePath | Should -BeTrue
        }

        It 'Should import without errors' {
            { Import-Module (Join-Path $PSScriptRoot '..') -Force -ErrorAction Stop } | Should -Not -Throw
        }
    }

    Context 'Module Metadata' {

        BeforeAll {
            $manifestPath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
            $manifest = Test-ModuleManifest -Path $manifestPath
        }

        It 'Should have the correct module name' {
            $manifest.Name | Should -Be 'JIM'
        }

        It 'Should have a version number' {
            $manifest.Version | Should -Not -BeNullOrEmpty
        }

        It 'Should have an author' {
            $manifest.Author | Should -Be 'Tetron'
        }

        It 'Should have a description' {
            $manifest.Description | Should -Not -BeNullOrEmpty
        }

        It 'Should require PowerShell 7.0 or later' {
            $manifest.PowerShellVersion | Should -BeGreaterOrEqual ([version]'7.0')
        }

        It 'Should have a project URI' {
            $manifest.PrivateData.PSData.ProjectUri | Should -Be 'https://github.com/TetronIO/JIM'
        }
    }

    Context 'Exported Functions' {

        BeforeAll {
            $exportedFunctions = (Get-Module JIM).ExportedFunctions.Keys
        }

        It 'Should export Connect-JIM' {
            $exportedFunctions | Should -Contain 'Connect-JIM'
        }

        It 'Should export Disconnect-JIM' {
            $exportedFunctions | Should -Contain 'Disconnect-JIM'
        }

        It 'Should export Test-JIMConnection' {
            $exportedFunctions | Should -Contain 'Test-JIMConnection'
        }

        It 'Should export Get-JIMConnectedSystem' {
            $exportedFunctions | Should -Contain 'Get-JIMConnectedSystem'
        }

        It 'Should export Remove-JIMConnectedSystem' {
            $exportedFunctions | Should -Contain 'Remove-JIMConnectedSystem'
        }

        It 'Should export Get-JIMSyncRule' {
            $exportedFunctions | Should -Contain 'Get-JIMSyncRule'
        }

        It 'Should export Get-JIMRunProfile' {
            $exportedFunctions | Should -Contain 'Get-JIMRunProfile'
        }

        It 'Should export Start-JIMRunProfile' {
            $exportedFunctions | Should -Contain 'Start-JIMRunProfile'
        }

        It 'Should export Get-JIMActivity' {
            $exportedFunctions | Should -Contain 'Get-JIMActivity'
        }

        It 'Should export Get-JIMActivityStats' {
            $exportedFunctions | Should -Contain 'Get-JIMActivityStats'
        }

        It 'Should not export private functions' {
            $exportedFunctions | Should -Not -Contain 'Invoke-JIMApi'
        }
    }
}
