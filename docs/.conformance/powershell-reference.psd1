# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Configuration for scripts/Update-PowerShellReference.ps1, which generates each cmdlet's Syntax,
# Parameters and Output on the docs/powershell/ pages from the module (ACC-5; #1083).
#
# This folder starts with a dot, so MkDocs never publishes it.
@{
    # A cmdlet declares what it returns with [OutputType('JIM.<Schema>', ParameterSetName = '<Set>')], and
    # the script lists that schema's properties from the OpenAPI document. A cmdlet that builds its own
    # object returns a type the API does not have; list it here with the reason, and describe its
    # properties in the cmdlet's .OUTPUTS help instead. An entry no cmdlet declares fails the check.
    NonApiOutputTypes = @{
        'JIM.CauseCohort'       = 'Get-JIMActivityExecutionItem -Causes flattens the causes endpoint''s nested cohorts into one row per cohort'
        'JIM.Connection'        = 'Connect-JIM builds it from the connection it made and the health endpoint'
        'JIM.ConnectionStatus'  = 'Test-JIMConnection builds it from the module''s connection and the health endpoint'
        'JIM.ConnectedSystemDeletionQueued' = 'Remove-JIMConnectedSystem builds it from the queued deletion and the deletion preview''s headline counts'
        'JIM.ConsequenceCohort' = 'Get-JIMActivityExecutionItem -Consequences flattens the consequences endpoint''s nested cohorts into one row per cohort'
        'JIM.MetaverseObjectConnectionEntry' = 'Get-JIMMetaverseObjectConnection flattens the connections endpoint''s joined and not-connected lists into one row per entry, with the same properties on both'
    }

    # A cmdlet that gives the API's object a type name of its own (PSObject.TypeNames), or adds properties
    # to it, declares that name; map it here to the schema whose properties the object carries, and they
    # are tabled under the name, followed by any the cmdlet adds and lists in its .OUTPUTS help. An entry
    # no cmdlet declares fails the check.
    OutputTypeSchemas = @{
        'JIM.ExampleDataTemplateExecution' = 'ExampleDataTemplateExecutionResponse'
        'JIM.ObjectTypeMatchingRule'       = 'ObjectMatchingRuleDto'
        'JIM.ScheduleExecution'            = 'ScheduleExecutionDetailDto'
        'JIM.ScheduleExecutionSummary'     = 'ScheduleExecutionDto'
        'JIM.ScopingCriteriaGroup'         = 'SyncRuleScopingCriteriaGroupDto'
        'JIM.ScopingCriterion'             = 'SyncRuleScopingCriteriaDto'
        'JIM.RetiredGeneratedValue'        = 'RetiredGeneratedValueDto'
        'JIM.SetConnectedSystemObjectPasswordResult' = 'SetMetaverseObjectPasswordResponse'
        'JIM.SetMetaverseObjectPasswordResult' = 'SetMetaverseObjectPasswordResponse'
        'JIM.ServiceHealth'                = 'ServiceHealthEntryResponse'
        'JIM.ServiceHealthSummary'         = 'ServiceHealthResponse'
        'JIM.SyncPreview'                  = 'SyncPreviewResponse'
    }
}
