# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for PowerShell output normalisation (camelCase wire -> PascalCase cmdlet output).

.DESCRIPTION
    Covers the ConvertTo-JIMOutputObject / ConvertTo-JIMPascalCaseName private helpers and
    their application at the Invoke-JIMApi choke point. The wire (REST API) is camelCase; the
    module must present PascalCase per Microsoft's Cmdlet Development Guidelines, while
    preserving dynamic-key dictionary keys (attribute names, log properties) verbatim.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'ConvertTo-JIMPascalCaseName' {

    It 'Upper-cases the leading character of a camelCase name' {
        InModuleScope JIM {
            ConvertTo-JIMPascalCaseName -Name 'typeId' | Should -BeExactly 'TypeId'
            ConvertTo-JIMPascalCaseName -Name 'displayName' | Should -BeExactly 'DisplayName'
            ConvertTo-JIMPascalCaseName -Name 'id' | Should -BeExactly 'Id'
        }
    }

    It 'Leaves an already-PascalCase name unchanged' {
        InModuleScope JIM {
            ConvertTo-JIMPascalCaseName -Name 'TypeId' | Should -BeExactly 'TypeId'
        }
    }

    It 'Returns an empty string unchanged' {
        InModuleScope JIM {
            ConvertTo-JIMPascalCaseName -Name '' | Should -BeExactly ''
        }
    }

    It 'Only touches the leading character (tail is preserved)' {
        InModuleScope JIM {
            ConvertTo-JIMPascalCaseName -Name 'hasNextPage' | Should -BeExactly 'HasNextPage'
        }
    }
}

Describe 'ConvertTo-JIMOutputObject' {

    Context 'Property name normalisation' {

        It 'Renames top-level camelCase properties to PascalCase' {
            InModuleScope JIM {
                $input = [PSCustomObject]@{ typeId = 5; displayName = 'Jane' }
                $result = ConvertTo-JIMOutputObject -InputObject $input

                $names = $result.PSObject.Properties.Name
                $names -ccontains 'TypeId' | Should -BeTrue
                $names -ccontains 'DisplayName' | Should -BeTrue
                $names -ccontains 'typeId' | Should -BeFalse
                $names -ccontains 'displayName' | Should -BeFalse
            }
        }

        It 'Preserves values while renaming keys' {
            InModuleScope JIM {
                $result = ConvertTo-JIMOutputObject -InputObject ([PSCustomObject]@{ typeId = 5; displayName = 'Jane' })
                $result.TypeId | Should -Be 5
                $result.DisplayName | Should -Be 'Jane'
            }
        }

        It 'Recurses into nested objects' {
            InModuleScope JIM {
                $input = [PSCustomObject]@{ type = [PSCustomObject]@{ id = 1; name = 'Person' } }
                $result = ConvertTo-JIMOutputObject -InputObject $input

                ($result.PSObject.Properties.Name) -ccontains 'Type' | Should -BeTrue
                ($result.Type.PSObject.Properties.Name) -ccontains 'Id' | Should -BeTrue
                ($result.Type.PSObject.Properties.Name) -ccontains 'Name' | Should -BeTrue
                $result.Type.Name | Should -Be 'Person'
            }
        }

        It 'Preserves property order' {
            InModuleScope JIM {
                $input = [PSCustomObject]@{ zebra = 1; alpha = 2; middle = 3 }
                $result = ConvertTo-JIMOutputObject -InputObject $input
                @($result.PSObject.Properties.Name) | Should -Be @('Zebra', 'Alpha', 'Middle')
            }
        }

        It 'Keeps existing scripts working via case-insensitive member access' {
            InModuleScope JIM {
                $result = ConvertTo-JIMOutputObject -InputObject ([PSCustomObject]@{ typeId = 7 })
                # A pre-existing script that read the wire casing still resolves post-rename.
                $result.typeId | Should -Be 7
                $result.TypeId | Should -Be 7
            }
        }
    }

    Context 'Arrays' {

        It 'Normalises each element of an array' {
            InModuleScope JIM {
                $input = @(
                    [PSCustomObject]@{ typeId = 1 },
                    [PSCustomObject]@{ typeId = 2 }
                )
                $result = ConvertTo-JIMOutputObject -InputObject $input
                $result.Count | Should -Be 2
                ($result[0].PSObject.Properties.Name) -ccontains 'TypeId' | Should -BeTrue
                $result[1].TypeId | Should -Be 2
            }
        }

        It 'Preserves a single-element array as an array' {
            InModuleScope JIM {
                $input = [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1 }) }
                $result = ConvertTo-JIMOutputObject -InputObject $input
                # 'items' is not opaque (it is a DTO list), so it is normalised and stays an array.
                , $result.Items | Should -BeOfType [System.Object[]]
                $result.Items.Count | Should -Be 1
                $result.Items[0].Id | Should -Be 1
            }
        }

        It 'Preserves an empty array as an empty array' {
            InModuleScope JIM {
                $input = [PSCustomObject]@{ items = @() }
                $result = ConvertTo-JIMOutputObject -InputObject $input
                , $result.Items | Should -BeOfType [System.Object[]]
                $result.Items.Count | Should -Be 0
            }
        }
    }

    Context 'Dynamic-key dictionaries are preserved verbatim' {

        It 'Renames the container but keeps attribute-name keys verbatim' {
            InModuleScope JIM {
                # Shape of MetaverseObjectHeaderDto: attributes is a dictionary keyed by attribute name.
                $input = [PSCustomObject]@{
                    displayName = 'Jane'
                    attributes  = [PSCustomObject]@{ mail = 'jane@x'; employeeID = 'E1'; givenName = 'Jane' }
                }
                $result = ConvertTo-JIMOutputObject -InputObject $input

                # Container property is PascalCased...
                ($result.PSObject.Properties.Name) -ccontains 'Attributes' | Should -BeTrue
                # ...but its data keys are untouched (would be corruption otherwise).
                $attrNames = $result.Attributes.PSObject.Properties.Name
                $attrNames -ccontains 'mail' | Should -BeTrue
                $attrNames -ccontains 'employeeID' | Should -BeTrue
                $attrNames -ccontains 'givenName' | Should -BeTrue
                $attrNames -ccontains 'Mail' | Should -BeFalse
                $attrNames -ccontains 'EmployeeID' | Should -BeFalse
                $result.Attributes.mail | Should -Be 'jane@x'
            }
        }

        It 'Treats an attributes-shaped list as DTOs, not as a dynamic dictionary' {
            InModuleScope JIM {
                # ConnectedSystemDto.Attributes is a List<DTO> under the same wire name as the
                # header's dynamic dictionary; its element fields MUST still be normalised (the
                # JSON-array shape, not a JSON object, disambiguates the two).
                $input = [PSCustomObject]@{ attributes = @([PSCustomObject]@{ attributeName = 'x'; typeId = 3 }) }
                $result = ConvertTo-JIMOutputObject -InputObject $input
                $element = $result.Attributes[0]
                ($element.PSObject.Properties.Name) -ccontains 'AttributeName' | Should -BeTrue
                ($element.PSObject.Properties.Name) -ccontains 'TypeId' | Should -BeTrue
            }
        }

        It 'Preserves keys verbatim all the way down an opaque subtree' {
            InModuleScope JIM {
                # LogEntryDto.Properties: keyed by Serilog property name, values arbitrary (may nest).
                $input = [PSCustomObject]@{
                    properties = [PSCustomObject]@{ userName = 'svc'; context = [PSCustomObject]@{ runId = 9 } }
                }
                $result = ConvertTo-JIMOutputObject -InputObject $input

                ($result.PSObject.Properties.Name) -ccontains 'Properties' | Should -BeTrue
                ($result.Properties.PSObject.Properties.Name) -ccontains 'userName' | Should -BeTrue
                # Nested data keys stay verbatim too (they are logged data, not DTO fields).
                ($result.Properties.context.PSObject.Properties.Name) -ccontains 'runId' | Should -BeTrue
                ($result.Properties.context.PSObject.Properties.Name) -ccontains 'RunId' | Should -BeFalse
            }
        }

        It 'Pins the set of opaque (dynamic-key) property names' {
            InModuleScope JIM {
                # Guards the maintenance contract: adding a data-keyed dictionary endpoint
                # without adding it here would silently PascalCase (corrupt) its keys.
                $expected = @('attributes', 'mvAttributes', 'csAttributes', 'properties', 'validationErrors')
                ($script:JIMOpaqueValueProperties | Sort-Object) | Should -Be ($expected | Sort-Object)
            }
        }
    }

    Context 'Scalars and null' {

        It 'Returns null unchanged' {
            InModuleScope JIM {
                ConvertTo-JIMOutputObject -InputObject $null | Should -Be $null
            }
        }

        It 'Returns scalar values unchanged' {
            InModuleScope JIM {
                ConvertTo-JIMOutputObject -InputObject 42 | Should -Be 42
                ConvertTo-JIMOutputObject -InputObject $true | Should -Be $true
                ConvertTo-JIMOutputObject -InputObject 'a string' | Should -Be 'a string'
            }
        }
    }
}

Describe 'Invoke-JIMApi output normalisation (wiring)' {

    It 'Returns PascalCase-normalised output from the choke point' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApiRequest {
                [PSCustomObject]@{
                    typeId      = 5
                    displayName = 'Jane'
                    type        = [PSCustomObject]@{ id = 1; name = 'Person' }
                    attributes  = [PSCustomObject]@{ mail = 'jane@x' }
                }
            }

            $result = Invoke-JIMApi -Endpoint '/api/v1/test'

            ($result.PSObject.Properties.Name) -ccontains 'TypeId' | Should -BeTrue
            ($result.PSObject.Properties.Name) -ccontains 'DisplayName' | Should -BeTrue
            $result.Type.Name | Should -Be 'Person'
            # Dynamic attribute key preserved through the choke point.
            ($result.Attributes.PSObject.Properties.Name) -ccontains 'mail' | Should -BeTrue
        }
    }

    It 'Emits nothing (not a $null item) for an empty response' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            # Invoke-RestMethod enumerates an empty JSON array ([]) response and emits
            # nothing; a 204 No Content behaves the same. The choke point must preserve
            # that nothing-ness: emitting an explicit $null makes @(cmdlet) count one
            # (null) object, so "is the list empty?" checks in caller scripts misfire.
            Mock Invoke-JIMApiRequest { }

            $result = @(Invoke-JIMApi -Endpoint '/api/v1/test')

            $result.Count | Should -Be 0
        }
    }

    It 'Normalises a bare-array response' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApiRequest {
                @(
                    [PSCustomObject]@{ typeId = 1 },
                    [PSCustomObject]@{ typeId = 2 }
                )
            }

            $result = @(Invoke-JIMApi -Endpoint '/api/v1/test')

            $result.Count | Should -Be 2
            ($result[0].PSObject.Properties.Name) -ccontains 'TypeId' | Should -BeTrue
        }
    }
}
