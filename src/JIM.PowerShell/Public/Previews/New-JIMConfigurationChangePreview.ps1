# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMConfigurationChangePreview {
    <#
    .SYNOPSIS
        Finds out what a proposed configuration change would do, without making it.

    .DESCRIPTION
        Starts a Configuration Change Preview: JIM evaluates a proposed change against the objects
        already in the metaverse and reports what would happen to them, changing nothing.

        Several surfaces can be previewed, selected by which identifier and proposal you pass:

        - -MetaverseObjectTypeId previews a change to that type's deletion settings. The field semantics
          match Set-JIMMetaverseObjectType exactly, so an omitted parameter previews the stored value.
          Pass the same parameters to this cmdlet and then to Set-JIMMetaverseObjectType and the preview
          describes precisely what the change will do.
        - -ConnectedSystemId previews a change to that system's partition and container selection. Pass
          the whole selection rather than one flag: what a deselection costs depends on the rest of the
          selection, because an object leaves import scope only when nothing else still covers it. Apply
          the previewed change with Set-JIMConnectedSystemPartition and Set-JIMConnectedSystemContainer.
        - -SyncRuleId previews a change to that Synchronisation Rule's two destructive toggles: the
          Outbound Deprovision Action (Disconnect or Delete on a scope exit) and the Inbound Out-of-Scope
          Action (RemainJoined or Disconnect). The field semantics match Set-JIMSyncRule exactly, so an
          omitted parameter previews the stored value. Apply the previewed change with Set-JIMSyncRule.
        - -SyncRuleId with -ScopingCriteriaGroup previews a change to that rule's Scoping Criteria: which
          objects it would manage at all, and what each movement in or out of scope would cost.
        - -SyncRuleId with -AttributeFlowMapping previews a change to that rule's Attribute Flow: what
          value every object it manages would end up with, per attribute.
        - -SyncRuleId with -RuleState previews a change to that rule's behaviour toggles: how many objects
          would stop having an identity or an account created for them, and how many would be left free to
          drift. -ProjectToMetaverse, -ProvisionToConnectedSystem and -EnforceState are optional and merge
          with the stored rule. Direction cannot be previewed: a saved rule's mappings are written for the
          direction it has, so a flip comes back with a blocking finding.
        - -ConnectedSystemId with -MatchingRule previews a change to that system's Object Matching Rules:
          which of its unjoined objects would join a different Metaverse Object, join instead of projecting
          a new identity, project instead of joining, or match ambiguously and fail. Add
          -ObjectMatchingRuleMode to preview the Simple/Advanced switch. Objects already joined are never
          re-matched, so no matching change can move them, and the preview says so.

        Evaluation is asynchronous. Without -Wait this returns as soon as the proposal itself has been
        validated, carrying the ActivityId to poll with Get-JIMConfigurationChangePreview. With -Wait it
        polls until the preview reaches a terminal state and returns the finished preview.

        A proposal that cannot be applied comes back with a blocking validation finding and is never
        evaluated: check IsBlocked and ValidationFindings before reading anything else.

        Pass the returned ActivityId to Set-JIMMetaverseObjectType -PreviewActivityId when you make the
        change, so the audit records which preview informed it.

    .PARAMETER MetaverseObjectTypeId
        The Metaverse Object Type whose deletion settings are being proposed. Selects the deletion
        settings surface; other surfaces gain their own parameters as their adapters ship.

    .PARAMETER ConnectedSystemId
        The Connected System whose partition and container selection is being proposed. Selects the scope
        selection surface.

    .PARAMETER SelectedPartitionIds
        The partitions that would be managed. Omitted previews the partitions currently selected, so a
        proposal changing only containers need not restate them.

    .PARAMETER SelectedContainerIds
        The containers that would be managed. Omitted previews the containers currently selected.

        Selecting a container selects its whole subtree, so a descendant does not need listing to be in
        scope; pass the containers that would carry a tick. Read the current selection with
        Get-JIMConnectedSystemPartition.

    .PARAMETER ExcludedContainerIds
        The containers that would be carved out of the selection around them. Omitted previews the
        exclusions currently in force, so a proposal changing only the selection need not restate them;
        an empty array previews lifting every exclusion, which brings those branches back into scope.

        A container states one or the other, so an id passed here must not also appear in
        -SelectedContainerIds; a proposal naming both is refused rather than resolved.

    .PARAMETER DeletionRule
        The proposed deletion rule. Omitted previews the stored rule.
        - Manual: objects are never automatically deleted
        - WhenLastConnectorDisconnected: objects are deleted when all connectors are removed
        - WhenAuthoritativeSourceDisconnected: objects are deleted when an authoritative source disconnects

    .PARAMETER DeletionGracePeriod
        The proposed grace period, as a TimeSpan. Omitted previews the stored grace period;
        [TimeSpan]::Zero previews no grace period, matching how Set-JIMMetaverseObjectType stores it.

    .PARAMETER DeletionTriggerConnectedSystemIds
        The proposed authoritative sources. Omitted previews the stored list.

        Worth knowing before reading the result: this list is consulted at the moment a Connected System
        Object disconnects, not by the housekeeping pass that acts on objects already marked, so changing
        it alone moves no object's deletion date and the preview will honestly report no impact from it.
        What it can do is make the proposal invalid, which comes back as a blocking finding.

    .PARAMETER DeletionTriggerMode
        The proposed trigger mode. Omitted previews the stored mode. Read at the same moment as the
        trigger list, and so with the same standing impact: none.

    .PARAMETER SyncRuleId
        The Synchronisation Rule whose destructive toggles are being proposed. Selects the destructive
        toggles surface.

    .PARAMETER OutboundDeprovisionAction
        The proposed action for a joined target object whose Metaverse Object leaves this export rule's
        scope: Disconnect leaves the object in the target Connected System; Delete stages a Delete export
        that removes it. Omitted previews the stored action. Read only by export Synchronisation Rules,
        and the preview says so honestly when the rule is an import rule.

    .PARAMETER InboundOutOfScopeAction
        The proposed action for a joined Connected System Object that leaves this import rule's scope or
        is obsoleted: RemainJoined keeps the join ("once managed, always managed"); Disconnect breaks it,
        recalls what the object contributed and can trigger the Metaverse Object's deletion rules.
        Omitted previews the stored action. Read only by import Synchronisation Rules.

    .PARAMETER ScopingCriteriaGroup
        The proposed Scoping Criteria, as one hashtable per top-level criteria group. Groups are combined
        with OR, exactly as a synchronisation combines them. Each group takes a Type of 'All' or 'Any', a
        Criteria array, and an optional ChildGroups array of further groups nested inside it. Each
        criterion names one attribute by id (ConnectedSystemAttributeId on an import rule,
        MetaverseAttributeId on an export rule), a ComparisonType, and the value to compare against in the
        field matching the attribute's data type (StringValue, IntValue, LongValue, DecimalValue,
        DateTimeValue, BoolValue or GuidValue).

        Mandatory, and an empty array is a valid and deliberate value: it proposes removing every criterion,
        which hands the rule every object of its type. That is the widest change the Scope tab can make, so
        it has to be asked for rather than arrived at by omission.

    .PARAMETER AttributeFlowMapping
        The proposed Attribute Flow, as one hashtable per mapping. Each mapping names the attribute it
        writes (TargetMetaverseAttributeId on an import rule, TargetConnectedSystemAttributeId on an
        export rule) and a Sources array; a source takes an Order, and either an attribute id
        (ConnectedSystemAttributeId on an import rule, MetaverseAttributeId on an export rule) or an
        Expression with an optional MissingInputBehaviour. A mapping may also carry Priority,
        NullIsValue, InitialExportOnly, InboundValueProcessing and CaseNormalisation, which default to
        the same values the editor uses.

        Mandatory, and an empty array is a valid and deliberate value: it proposes removing every mapping,
        so the rule flows nothing.

        Priority is worth passing deliberately on an import mapping. It defaults to the lowest, so a
        mapping proposed for an attribute another rule already contributes to would be evaluated and then
        write nothing; the preview reports that as a validation finding rather than as values that would
        never be written.

    .PARAMETER FullDataSet
        Keep every object-level detail row rather than the per-group cap's worth. Summary counts are
        exact either way; this decides only how much of the detail behind them can be read back with
        Get-JIMConfigurationChangePreviewDelta. Use it when you intend to export the full list.

    .PARAMETER Wait
        Poll until the preview finishes, and return the finished preview rather than the start result.

    .PARAMETER TimeoutSeconds
        How long -Wait polls before giving up and writing an error. The preview itself keeps running;
        read it later with Get-JIMConfigurationChangePreview. Defaults to 300 seconds.

    .OUTPUTS
        Without -Wait: PSCustomObject with ActivityId, ValidationFindings, IsBlocked, Failed,
        EstimatedAffectedObjects and EstimatedDeltaRows.
        With -Wait: the preview, as returned by Get-JIMConfigurationChangePreview.

    .EXAMPLE
        New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected -Wait

        Reports what switching the User type to automatic deletion would do to the Metaverse Objects
        whose connectors have already gone, and waits for the answer.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionGracePeriod ([TimeSpan]::FromDays(7)) -Wait
        $preview.ImpactCounts

        Shortens the grace period to seven days and reads how many objects each transition affects.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected -Wait
        if ($preview.ImpactCounts.Count -eq 0) {
            Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -PreviewActivityId $preview.ActivityId
        }

        Applies the change only when the preview found nothing would happen to existing objects, and
        records the preview against the change.

    .EXAMPLE
        $current = Get-JIMConnectedSystemPartition -ConnectedSystemId 2
        $keep = $current.containers | Where-Object { $_.selected -and $_.name -ne 'Contractors' }
        New-JIMConfigurationChangePreview -ConnectedSystemId 2 -SelectedContainerIds $keep.id -Wait

        Reports what deselecting the Contractors container would do: how many Connected System Objects
        leave import scope, how many of those are joined and would disconnect from their Metaverse
        Object, and how many Metaverse Objects would then become eligible for automatic deletion.

    .EXAMPLE
        $current = Get-JIMConnectedSystemPartition -ConnectedSystemId 2
        $carveOut = $current.containers | Where-Object name -eq 'Service Accounts'
        New-JIMConfigurationChangePreview -ConnectedSystemId 2 -ExcludedContainerIds $carveOut.id -Wait

        Reports what excluding the Service Accounts container would do. Its parent stays selected, so
        nothing else in the branch moves; the objects inside the carve-out are the ones that leave import
        scope, and the ones already joined would disconnect from their Metaverse Objects.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -ConnectedSystemId 2 -SelectedPartitionIds 5 -Wait
        $preview.ImpactCounts | Where-Object transitionType -eq 'WouldBecomeDeletionEligible'

        Narrows the managed partitions to one and reads how many Metaverse Objects the resulting
        disconnections would put on course for deletion.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -OutboundDeprovisionAction Delete -Wait
        $preview.ImpactCounts

        Reports what flipping an export Synchronisation Rule's Deprovisioning Action to Delete would do:
        how many joined objects already out of scope would be removed from the target Connected System at
        the next synchronisation, and how many managed objects' fate on a future scope exit changes.

    .EXAMPLE
        $group = @{
            Type = 'All'
            Criteria = @(
                @{ ConnectedSystemAttributeId = 101; ComparisonType = 'Equals'; StringValue = 'Sales' }
            )
        }
        $preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -ScopingCriteriaGroup $group -Wait
        $preview.ImpactCounts

        Reports what narrowing an import Synchronisation Rule to the Sales department would do: how many
        joined objects would leave scope and disconnect from their Metaverse Objects, how many unjoined
        ones simply stop matching, and how many objects would newly enter scope and be projected.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -ScopingCriteriaGroup @() -Wait
        $preview.ImpactCounts | Where-Object transitionType -eq 'Projected'

        Reports what removing every Scoping Criterion would do, which puts every object of the rule's type
        in scope. The Projected count is how many Metaverse Objects that would create.

    .EXAMPLE
        $mapping = @{
            TargetMetaverseAttributeId = 201
            Priority = 1
            Sources = @(
                @{ Order = 1; Expression = 'cs["givenName"] + "." + cs["sn"] + "@corp.local"'; MissingInputBehaviour = 'FailMapping' }
            )
        }
        $preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -AttributeFlowMapping $mapping -Wait
        $preview.ImpactCounts

        Reports what an email cutover would write: how many identities' addresses change, and how many
        objects the Expression could not be evaluated for at all because a required input is missing.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -AttributeFlowMapping $mapping -FullDataSet -Wait
        Get-JIMConfigurationChangePreviewDelta -ActivityId $preview.ActivityId |
            Where-Object transitionType -eq 'WouldFailAttributeFlow'

        Keeps every detail row and lists the objects the proposed Expression would not evaluate for, which
        is the handful the cutover would otherwise leave without an address.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -InboundOutOfScopeAction Disconnect -Wait
        if (($preview.ImpactCounts | Measure-Object objectCount -Sum).Sum -eq 0) {
            Set-JIMSyncRule -Id 42 -InboundOutOfScopeAction Disconnect -PreviewActivityId $preview.ActivityId
        }

        Applies the tightened Out-of-Scope Action only when the preview found no object would be
        disconnected by it today, and records the preview against the change.

    .EXAMPLE
        $rules = @(
            @{
                order                      = 0
                connectedSystemObjectTypeId = 9
                metaverseObjectTypeId       = 3
                targetMetaverseAttributeId  = 201
                caseSensitive               = $false
                sources                     = @(@{ order = 0; connectedSystemAttributeId = 102 })
            }
        )
        $preview = New-JIMConfigurationChangePreview -ConnectedSystemId 5 -MatchingRule $rules -Wait
        $preview.ImpactCounts | Where-Object transitionType -eq 'WouldJoinDifferentMetaverseObject'

        Previews matching on a different attribute and reports how many accounts would end up on a
        different identity, which is the count that decides whether the rule is safe to save.

    .EXAMPLE
        New-JIMConfigurationChangePreview -ConnectedSystemId 5 -MatchingRule @() -Wait

        Previews removing every Object Matching Rule: nothing would join, and every unjoined object would
        project a new identity instead.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -SyncRuleId 42 -RuleState Disabled -Wait
        $preview.ImpactCounts

        Previews disabling a Synchronisation Rule and reports what stops happening: the identities and accounts
        that would no longer be created, which is the count that says whether "just pausing it" is what it does.

    .EXAMPLE
        New-JIMConfigurationChangePreview -SyncRuleId 42 -RuleState Enabled -ProvisionToConnectedSystem $true -Wait

        Previews enabling a rule and turning provisioning on together, which is account creation at scale.

    .LINK
        Get-JIMConfigurationChangePreview
        Get-JIMConfigurationChangePreviewDelta
        Stop-JIMConfigurationChangePreview
        Set-JIMMetaverseObjectType
        Set-JIMConnectedSystemPartition
        Set-JIMConnectedSystemContainer
        Set-JIMSyncRule
        Set-JIMMatchingRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'MetaverseObjectTypeDeletionSettings', ValueFromPipelineByPropertyName)]
        [int]$MetaverseObjectTypeId,

        [Parameter(ParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
        [ValidateSet('Manual', 'WhenLastConnectorDisconnected', 'WhenAuthoritativeSourceDisconnected')]
        [string]$DeletionRule,

        [Parameter(ParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
        [TimeSpan]$DeletionGracePeriod,

        [Parameter(ParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
        [int[]]$DeletionTriggerConnectedSystemIds,

        [Parameter(ParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
        [ValidateSet('AllSourcesDisconnect', 'SpecificSourcesDisconnect')]
        [string]$DeletionTriggerMode,

        [Parameter(Mandatory, ParameterSetName = 'ConnectedSystemScopeSelection', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ObjectMatching', ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(ParameterSetName = 'ConnectedSystemScopeSelection')]
        [int[]]$SelectedPartitionIds,

        [Parameter(ParameterSetName = 'ConnectedSystemScopeSelection')]
        [int[]]$SelectedContainerIds,

        [Parameter(ParameterSetName = 'ConnectedSystemScopeSelection')]
        [AllowEmptyCollection()]
        [int[]]$ExcludedContainerIds,

        [Parameter(Mandatory, ParameterSetName = 'SyncRuleDestructiveToggles', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'SyncRuleScopingCriteria', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'SyncRuleAttributeFlow', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'SyncRuleBehaviour', ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(ParameterSetName = 'SyncRuleDestructiveToggles')]
        [ValidateSet('Disconnect', 'Delete')]
        [string]$OutboundDeprovisionAction,

        [Parameter(ParameterSetName = 'SyncRuleDestructiveToggles')]
        [ValidateSet('RemainJoined', 'Disconnect')]
        [string]$InboundOutOfScopeAction,

        [Parameter(Mandatory, ParameterSetName = 'SyncRuleScopingCriteria')]
        [AllowEmptyCollection()]
        [hashtable[]]$ScopingCriteriaGroup,

        [Parameter(Mandatory, ParameterSetName = 'SyncRuleAttributeFlow')]
        [AllowEmptyCollection()]
        [hashtable[]]$AttributeFlowMapping,

        [Parameter(Mandatory, ParameterSetName = 'ObjectMatching')]
        [AllowEmptyCollection()]
        [hashtable[]]$MatchingRule,

        [Parameter(ParameterSetName = 'ObjectMatching')]
        [ValidateSet('ConnectedSystem', 'SyncRule')]
        [string]$ObjectMatchingRuleMode,

        [Parameter(Mandatory, ParameterSetName = 'SyncRuleBehaviour')]
        [ValidateSet('Enabled', 'Disabled')]
        [string]$RuleState,

        [Parameter(ParameterSetName = 'SyncRuleBehaviour')]
        [bool]$ProjectToMetaverse,

        [Parameter(ParameterSetName = 'SyncRuleBehaviour')]
        [bool]$ProvisionToConnectedSystem,

        [Parameter(ParameterSetName = 'SyncRuleBehaviour')]
        [bool]$EnforceState,

        [Parameter()]
        [switch]$FullDataSet,

        [Parameter()]
        [switch]$Wait,

        [Parameter()]
        [ValidateRange(1, 86400)]
        [int]$TimeoutSeconds = 300
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{}

        if ($PSCmdlet.ParameterSetName -eq 'ConnectedSystemScopeSelection') {
            if ($PSBoundParameters.ContainsKey('SelectedPartitionIds')) {
                # Wrapped in @() so a single id serialises as a JSON array rather than a bare number.
                $body.selectedPartitionIds = @($SelectedPartitionIds)
            }

            if ($PSBoundParameters.ContainsKey('SelectedContainerIds')) {
                $body.selectedContainerIds = @($SelectedContainerIds)
            }

            if ($PSBoundParameters.ContainsKey('ExcludedContainerIds')) {
                $body.excludedContainerIds = @($ExcludedContainerIds)
            }
        }

        if ($DeletionRule) {
            # Enum sent as its string name; the API rejects numeric ordinals
            # (JsonStringEnumConverter allowIntegerValues:false).
            $body.deletionRule = $DeletionRule
        }

        if ($PSBoundParameters.ContainsKey('DeletionGracePeriod')) {
            $body.deletionGracePeriod = $DeletionGracePeriod.ToString()
        }

        if ($PSBoundParameters.ContainsKey('DeletionTriggerConnectedSystemIds')) {
            $body.deletionTriggerConnectedSystemIds = $DeletionTriggerConnectedSystemIds
        }

        if ($PSBoundParameters.ContainsKey('DeletionTriggerMode')) {
            $body.deletionTriggerMode = $DeletionTriggerMode
        }

        if ($PSCmdlet.ParameterSetName -eq 'SyncRuleDestructiveToggles') {
            if ($OutboundDeprovisionAction) {
                # Enum sent as its string name; the API rejects numeric ordinals
                # (JsonStringEnumConverter allowIntegerValues:false).
                $body.outboundDeprovisionAction = $OutboundDeprovisionAction
            }

            if ($InboundOutOfScopeAction) {
                $body.inboundOutOfScopeAction = $InboundOutOfScopeAction
            }
        }

        if ($PSCmdlet.ParameterSetName -eq 'SyncRuleScopingCriteria') {
            # Always sent, including as an empty array: omitting the field previews the rule's stored criteria,
            # while an empty array proposes removing every one of them. The two are different questions, and the
            # parameter is mandatory so the caller has to say which they are asking.
            # Wrapped in @() so a single group serialises as a JSON array rather than a bare object.
            $body.criteriaGroups = @($ScopingCriteriaGroup)
        }

        if ($PSCmdlet.ParameterSetName -eq 'SyncRuleAttributeFlow') {
            # Always sent, including as an empty array, for the same reason as the criteria groups above: omitting
            # the field previews the rule's stored mappings, while an empty array proposes removing every one of
            # them. Wrapped in @() so a single mapping serialises as a JSON array rather than a bare object.
            $body.mappings = @($AttributeFlowMapping)
        }

        if ($PSCmdlet.ParameterSetName -eq 'SyncRuleBehaviour') {
            # -RuleState is mandatory because the toggle it sets is a boolean: were it optional, a caller who left
            # it out could not be told apart from one proposing to switch the rule off, and those are opposite
            # questions. The other three are optional and merge with the stored rule, so silence never proposes a
            # change nobody asked for.
            $body.enabled = ($RuleState -eq 'Enabled')

            foreach ($toggle in 'ProjectToMetaverse', 'ProvisionToConnectedSystem', 'EnforceState') {
                if ($PSBoundParameters.ContainsKey($toggle)) {
                    $body[[System.Char]::ToLowerInvariant($toggle[0]) + $toggle.Substring(1)] = $PSBoundParameters[$toggle]
                }
            }
        }

        if ($PSCmdlet.ParameterSetName -eq 'ObjectMatching') {
            # Always sent, including as an empty array, for the same reason as the mappings above: omitting the
            # field previews the Connected System's stored rules, while an empty array proposes removing every one
            # of them, leaving nothing able to join. Wrapped in @() so a single rule serialises as a JSON array
            # rather than a bare object.
            $body.rules = @($MatchingRule)

            # The mode is only sent when the caller is changing it; omitted, the preview keeps the stored mode, so
            # a caller editing rules alone does not have to restate which mode they are in.
            if ($ObjectMatchingRuleMode) {
                $body.mode = $ObjectMatchingRuleMode
            }
        }

        if ($FullDataSet) {
            $body.deltaPersistence = 'Full'
        }

        if ($PSCmdlet.ParameterSetName -eq 'ConnectedSystemScopeSelection') {
            $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scope-selection/preview"
            $subject = "Connected System $ConnectedSystemId"
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'SyncRuleDestructiveToggles') {
            $endpoint = "/api/v1/synchronisation/sync-rules/$SyncRuleId/destructive-toggles/preview"
            $subject = "Synchronisation Rule $SyncRuleId"
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'SyncRuleScopingCriteria') {
            $endpoint = "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/preview"
            $subject = "Synchronisation Rule $SyncRuleId"
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'SyncRuleAttributeFlow') {
            $endpoint = "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/preview"
            $subject = "Synchronisation Rule $SyncRuleId"
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'ObjectMatching') {
            $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules/preview"
            $subject = "Connected System $ConnectedSystemId"
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'SyncRuleBehaviour') {
            $endpoint = "/api/v1/synchronisation/sync-rules/$SyncRuleId/behaviour/preview"
            $subject = "Synchronisation Rule $SyncRuleId"
        }
        else {
            $endpoint = "/api/v1/metaverse/object-types/$MetaverseObjectTypeId/deletion-settings/preview"
            $subject = "Metaverse Object Type $MetaverseObjectTypeId"
        }

        try {
            $start = Invoke-JIMApi -Endpoint $endpoint -Method 'POST' -Body $body
        }
        catch {
            Write-Error "Failed to start a preview for ${subject}: $_"
            return
        }

        if (-not $start) {
            return
        }

        foreach ($finding in $start.ValidationFindings) {
            # Surfaced as they are produced rather than left for the caller to find in the object: a
            # blocking finding means nothing was evaluated, and a script that read straight past it
            # would treat "this change is impossible" as "this change affects nothing".
            $message = "Preview validation ($($finding.Severity)): $($finding.Message)"
            if ($finding.Severity -eq 'Blocking') { Write-Warning $message } else { Write-Verbose $message }
        }

        if (-not $Wait -or $start.IsBlocked -or $start.Failed) {
            return $start
        }

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ($true) {
            $preview = Get-JIMConfigurationChangePreview -ActivityId $start.ActivityId
            if ($preview.IsComplete -or $preview.HasFailed -or $preview.ActivityStatus -eq 'Cancelled') {
                return $preview
            }

            if ((Get-Date) -ge $deadline) {
                Write-Error "The preview did not finish within $TimeoutSeconds seconds. It is still running; read it with Get-JIMConfigurationChangePreview -ActivityId $($start.ActivityId), or abandon it with Stop-JIMConfigurationChangePreview."
                return $preview
            }

            Start-Sleep -Seconds 2
        }
    }
}
