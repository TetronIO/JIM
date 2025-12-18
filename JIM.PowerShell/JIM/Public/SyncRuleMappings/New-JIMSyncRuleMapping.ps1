function New-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Creates a new Sync Rule Mapping (attribute flow rule) in JIM.

    .DESCRIPTION
        Creates a new attribute flow mapping for a Sync Rule.
        For Import rules, this maps Connected System attributes to Metaverse attributes.
        For Export rules, this maps Metaverse attributes to Connected System attributes.
        Alternatively, an expression can be used as the source for dynamic value generation.

    .PARAMETER SyncRuleId
        The unique identifier of the Sync Rule to add the mapping to.
        Also accepts pipeline input via the Id property.

    .PARAMETER TargetMetaverseAttributeId
        For Import rules: The ID of the Metaverse attribute that will receive the value.

    .PARAMETER TargetConnectedSystemAttributeId
        For Export rules: The ID of the Connected System attribute that will receive the value.

    .PARAMETER SourceConnectedSystemAttributeId
        For Import rules: The ID of the Connected System attribute to use as the source.
        Can be a single value or an array for multiple sources.
        Mutually exclusive with -Expression.

    .PARAMETER SourceMetaverseAttributeId
        For Export rules: The ID of the Metaverse attribute to use as the source.
        Can be a single value or an array for multiple sources.
        Mutually exclusive with -Expression.

    .PARAMETER Expression
        An expression to evaluate for the source value.
        Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
        Example: '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"'

    .OUTPUTS
        PSCustomObject representing the created Sync Rule Mapping.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10

        Creates an import mapping that flows data from CS attribute 10 to MV attribute 5.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -SourceMetaverseAttributeId 8

        Creates an export mapping that flows data from MV attribute 8 to CS attribute 15.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=TestUsers,DC=domain,DC=local"'

        Creates an export mapping that uses an expression to construct a Distinguished Name.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"]) + "@company.com"'

        Creates an import mapping that uses an expression to construct an email address.

    .LINK
        Get-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
        Get-JIMSyncRule
        Test-JIMExpression
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ImportAttribute')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ImportExpression')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ExportAttribute')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ExportExpression')]
        [Alias('Id')]
        [int]$SyncRuleId,

        [Parameter(ParameterSetName = 'ImportAttribute')]
        [Parameter(ParameterSetName = 'ImportExpression')]
        [int]$TargetMetaverseAttributeId,

        [Parameter(ParameterSetName = 'ExportAttribute')]
        [Parameter(ParameterSetName = 'ExportExpression')]
        [int]$TargetConnectedSystemAttributeId,

        [Parameter(ParameterSetName = 'ImportAttribute')]
        [int[]]$SourceConnectedSystemAttributeId,

        [Parameter(ParameterSetName = 'ExportAttribute')]
        [int[]]$SourceMetaverseAttributeId,

        [Parameter(ParameterSetName = 'ImportExpression')]
        [Parameter(ParameterSetName = 'ExportExpression')]
        [string]$Expression
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Determine direction and validate parameters
        $isImport = $PSBoundParameters.ContainsKey('TargetMetaverseAttributeId')
        $isExport = $PSBoundParameters.ContainsKey('TargetConnectedSystemAttributeId')
        $hasExpression = $PSBoundParameters.ContainsKey('Expression') -and -not [string]::IsNullOrWhiteSpace($Expression)

        if (-not $isImport -and -not $isExport) {
            Write-Error "You must specify either -TargetMetaverseAttributeId (for import) or -TargetConnectedSystemAttributeId (for export)."
            return
        }

        # Build request body
        $body = @{
            sources = @()
        }

        if ($isImport) {
            $body.targetMetaverseAttributeId = $TargetMetaverseAttributeId

            if ($hasExpression) {
                # Expression-based import mapping
                $body.sources += @{
                    order = 0
                    expression = $Expression
                }
            }
            elseif ($SourceConnectedSystemAttributeId) {
                # Attribute-based import mapping
                $order = 0
                foreach ($sourceId in $SourceConnectedSystemAttributeId) {
                    $body.sources += @{
                        order = $order
                        connectedSystemAttributeId = $sourceId
                    }
                    $order++
                }
            }
            else {
                Write-Error "-SourceConnectedSystemAttributeId or -Expression is required for import mappings."
                return
            }

            $targetDescription = "MV Attribute $TargetMetaverseAttributeId"
        }
        else {
            $body.targetConnectedSystemAttributeId = $TargetConnectedSystemAttributeId

            if ($hasExpression) {
                # Expression-based export mapping
                $body.sources += @{
                    order = 0
                    expression = $Expression
                }
            }
            elseif ($SourceMetaverseAttributeId) {
                # Attribute-based export mapping
                $order = 0
                foreach ($sourceId in $SourceMetaverseAttributeId) {
                    $body.sources += @{
                        order = $order
                        metaverseAttributeId = $sourceId
                    }
                    $order++
                }
            }
            else {
                Write-Error "-SourceMetaverseAttributeId or -Expression is required for export mappings."
                return
            }

            $targetDescription = "CS Attribute $TargetConnectedSystemAttributeId"
        }

        if ($PSCmdlet.ShouldProcess("$targetDescription in Sync Rule $SyncRuleId", "Create Mapping")) {
            Write-Verbose "Creating Sync Rule Mapping for Sync Rule: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings" -Method 'POST' -Body $body

                Write-Verbose "Created Sync Rule Mapping with ID: $($result.id)"

                $result
            }
            catch {
                Write-Error "Failed to create Sync Rule Mapping: $_"
            }
        }
    }
}
