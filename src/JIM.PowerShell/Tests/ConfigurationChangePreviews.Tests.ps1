# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'New-JIMConfigurationChangePreview' {
    Context 'Request construction' {
        It 'Omits every deletion setting the caller did not supply, so the preview describes the stored values' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                $script:capturedEndpoint = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    $script:capturedEndpoint = $Endpoint
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                }

                New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 | Out-Null

                # An empty body is the correct request for "what would the settings already in force do?".
                # Sending nulls instead would ask the server to preview clearing every setting.
                $script:capturedEndpoint | Should -Be '/api/v1/metaverse/object-types/1/deletion-settings/preview'
                $script:capturedBody.Keys.Count | Should -Be 0
            }
        }

        It 'Sends the deletion rule as its enum name (the API rejects numeric enum values)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                }

                New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected | Out-Null

                $script:capturedBody.deletionRule | Should -BeExactly 'WhenLastConnectorDisconnected'
            }
        }

        It 'Sends a zero grace period rather than dropping it, because zero is a real proposal' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                }

                New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionGracePeriod ([TimeSpan]::Zero) | Out-Null

                # Dropping it would silently preview the stored grace period, so the caller would be told
                # what a change they are not making would do.
                $script:capturedBody.ContainsKey('deletionGracePeriod') | Should -BeTrue
                $script:capturedBody.deletionGracePeriod | Should -Be '00:00:00'
            }
        }

        It 'Sends the proposed exclusions, so a carve-out can be previewed before it is made' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                }

                New-JIMConfigurationChangePreview -ConnectedSystemId 2 -SelectedContainerIds 21 -ExcludedContainerIds 23 | Out-Null

                $script:capturedBody.excludedContainerIds | Should -Be @(23)
            }
        }

        It 'Sends a single excluded id as a JSON array rather than a bare number' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                }

                New-JIMConfigurationChangePreview -ConnectedSystemId 2 -ExcludedContainerIds 23 | Out-Null

                $script:capturedBody.excludedContainerIds -is [array] | Should -BeTrue
            }
        }

        It 'Omits the exclusions the caller did not supply, so the stored carve-outs stay in force' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                }

                New-JIMConfigurationChangePreview -ConnectedSystemId 2 -SelectedContainerIds 21 | Out-Null

                # Sending an empty list instead would preview lifting every exclusion, which is a change the
                # caller did not ask for and reads as objects flooding back into scope.
                $script:capturedBody.ContainsKey('excludedContainerIds') | Should -BeFalse
            }
        }

        It 'Sends an explicitly empty exclusion list, because lifting every carve-out is a real proposal' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                }

                New-JIMConfigurationChangePreview -ConnectedSystemId 2 -ExcludedContainerIds @() | Out-Null

                $script:capturedBody.ContainsKey('excludedContainerIds') | Should -BeTrue
                $script:capturedBody.excludedContainerIds.Count | Should -Be 0
            }
        }

        It 'Asks for the full data set only when -FullDataSet is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null
                Mock Invoke-JIMApi {
                    $script:capturedBody = $Body
                    [PSCustomObject]@{ ActivityId = [guid]::NewGuid(); IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                }

                New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -FullDataSet | Out-Null
                $script:capturedBody.deltaPersistence | Should -BeExactly 'Full'

                $script:capturedBody = $null
                New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 | Out-Null
                $script:capturedBody.ContainsKey('deltaPersistence') | Should -BeFalse
            }
        }
    }

    Context 'Waiting' {
        It 'Returns the start result without polling when the proposal is blocked' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:calls = 0
                Mock Invoke-JIMApi {
                    $script:calls++
                    [PSCustomObject]@{
                        ActivityId         = [guid]::NewGuid()
                        IsBlocked          = $true
                        Failed             = $false
                        ValidationFindings = @([PSCustomObject]@{ Severity = 'Blocking'; Message = 'needs at least one authoritative source' })
                    }
                }

                $result = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -Wait -WarningAction SilentlyContinue

                # A blocked proposal is never evaluated, so polling for results would wait for something
                # that is never going to arrive.
                $result.IsBlocked | Should -BeTrue
                $script:calls | Should -Be 1
            }
        }

        It 'Polls until the preview reaches a terminal state and returns the finished preview' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $activityId = [guid]::NewGuid()
                $script:reads = 0
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ ActivityId = $activityId; IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                    }

                    $script:reads++
                    [PSCustomObject]@{
                        ActivityId     = $activityId
                        IsComplete     = ($script:reads -ge 2)
                        HasFailed      = $false
                        ActivityStatus = if ($script:reads -ge 2) { 'Complete' } else { 'InProgress' }
                    }
                }
                Mock Start-Sleep { }

                $result = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -Wait

                $result.IsComplete | Should -BeTrue
                $script:reads | Should -Be 2
            }
        }

        It 'Stops waiting on a failed preview rather than polling to the timeout' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $activityId = [guid]::NewGuid()
                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ ActivityId = $activityId; IsBlocked = $false; Failed = $false; ValidationFindings = @() }
                    }
                    [PSCustomObject]@{ ActivityId = $activityId; IsComplete = $false; HasFailed = $true; ActivityStatus = 'FailedWithError' }
                }
                Mock Start-Sleep { }

                $result = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -Wait

                $result.HasFailed | Should -BeTrue
            }
        }
    }

    Context 'Parameter validation' {
        BeforeAll {
            $command = Get-Command New-JIMConfigurationChangePreview
        }

        It 'Restricts the deletion rule to the rules the model defines' {
            $validateSet = $command.Parameters['DeletionRule'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Contain 'Manual'
            $validateSet.ValidValues | Should -Contain 'WhenLastConnectorDisconnected'
            $validateSet.ValidValues | Should -Contain 'WhenAuthoritativeSourceDisconnected'
        }

        It 'Accepts MetaverseObjectTypeId from the pipeline by property name' {
            $parameter = $command.Parameters['MetaverseObjectTypeId']
            ($parameter.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName
            }) | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMConfigurationChangePreview' {
    It 'Reads the preview by its Activity id' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $activityId = [guid]::NewGuid()
            $script:capturedEndpoint = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                [PSCustomObject]@{ ActivityId = $activityId }
            }

            Get-JIMConfigurationChangePreview -ActivityId $activityId | Out-Null

            $script:capturedEndpoint | Should -Be "/api/v1/previews/$activityId"
        }
    }

    It 'Surfaces the pattern a group was recognised as' {
        # The detected pattern is what makes a scripted preview reviewable without opening the portal:
        # "4,812 objects, EmailDomainChanged" is actionable where a bare count is not.
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    ActivityId = [guid]::NewGuid()
                    Groups     = @(
                        [PSCustomObject]@{ AttributeName = 'Email'; PatternKey = 'EmailDomainChanged'; ObjectCount = 4812 }
                        [PSCustomObject]@{ AttributeName = 'Department'; PatternKey = $null; ObjectCount = 12 }
                    )
                }
            }

            $preview = Get-JIMConfigurationChangePreview -ActivityId ([guid]::NewGuid())

            $preview.Groups[0].PatternKey | Should -Be 'EmailDomainChanged'
            $preview.Groups[1].PatternKey | Should -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMConfigurationChangePreviewDelta' {
    It 'Restricts the rows to one summary group when -GroupId is supplied' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $activityId = [guid]::NewGuid()
            $groupId = [guid]::NewGuid()
            $script:capturedEndpoint = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                [PSCustomObject]@{ items = @(); totalCount = 0 }
            }

            Get-JIMConfigurationChangePreviewDelta -ActivityId $activityId -GroupId $groupId | Out-Null

            $script:capturedEndpoint | Should -BeLike "*/previews/$activityId/deltas?*"
            $script:capturedEndpoint | Should -BeLike "*groupId=$groupId*"
        }
    }

    It 'Returns an empty page without error rather than treating it as a failure' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

            $rows = @(Get-JIMConfigurationChangePreviewDelta -ActivityId ([guid]::NewGuid()) -ErrorAction Stop)

            $rows.Count | Should -Be 0
        }
    }

    It 'Exposes the Page and All parameter sets' {
        $command = Get-Command Get-JIMConfigurationChangePreviewDelta
        $command.DefaultParameterSet | Should -Be 'Page'
        $command.ParameterSets.Name | Should -Contain 'All'
    }
}

Describe 'Stop-JIMConfigurationChangePreview' {
    It 'Sends a DELETE for the preview' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $activityId = [guid]::NewGuid()
            $script:capturedEndpoint = $null
            $script:capturedMethod = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                $script:capturedMethod = $Method
            }

            Stop-JIMConfigurationChangePreview -ActivityId $activityId -Confirm:$false

            $script:capturedEndpoint | Should -Be "/api/v1/previews/$activityId"
            $script:capturedMethod | Should -Be 'DELETE'
        }
    }

    It 'Supports ShouldProcess so a preview is not stopped by a dry run' {
        (Get-Command Stop-JIMConfigurationChangePreview).Parameters.ContainsKey('WhatIf') | Should -BeTrue
    }
}

Describe 'Set-JIMMetaverseObjectType preview linkage' {
    It 'Sends previewActivityId so the change records the preview that informed it' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $previewActivityId = [guid]::NewGuid()
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ id = 1 }
            }

            Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected `
                -PreviewActivityId $previewActivityId -Confirm:$false

            $script:capturedBody.previewActivityId | Should -Be $previewActivityId
        }
    }

    It 'Omits previewActivityId when no preview was run' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ id = 1 }
            }

            Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -Confirm:$false

            $script:capturedBody.ContainsKey('previewActivityId') | Should -BeFalse
        }
    }
}
