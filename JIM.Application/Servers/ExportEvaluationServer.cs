using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// Evaluates export rules and creates PendingExports when Metaverse Objects change.
/// Implements Q1 decision: evaluate exports immediately when MVO changes.
/// </summary>
public class ExportEvaluationServer
{
    private JimApplication Application { get; }
    private IExpressionEvaluator ExpressionEvaluator { get; }

    internal ExportEvaluationServer(JimApplication application)
    {
        Application = application;
        ExpressionEvaluator = new DynamicExpressoEvaluator();
    }

    /// <summary>
    /// Evaluates all export rules for an MVO that has changed and creates PendingExports.
    /// This is the main entry point called after inbound sync updates an MVO.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed</param>
    /// <param name="changedAttributes">The attributes that changed on the MVO</param>
    /// <param name="sourceSystem">The connected system that caused this change (for Q3 circular prevention)</param>
    /// <returns>List of PendingExports that were created</returns>
    public async Task<List<PendingExport>> EvaluateExportRulesAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ConnectedSystem? sourceSystem = null)
    {
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateExportRulesAsync: MVO {MvoId} has no type set, cannot evaluate export rules", mvo.Id);
            return pendingExports;
        }

        // Get all enabled export rules for this MVO's object type
        var exportRules = await GetExportRulesForObjectTypeAsync(mvo.Type.Id);

        foreach (var exportRule in exportRules)
        {
            // Q3: Skip if this is the source system (circular sync prevention)
            if (sourceSystem != null && exportRule.ConnectedSystemId == sourceSystem.Id)
            {
                Log.Debug("EvaluateExportRulesAsync: Skipping export to {System} - it is the source of these changes (Q3 circular prevention)",
                    exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString());
                continue;
            }

            // Check if MVO is in scope for this export rule
            if (!IsMvoInScopeForExportRule(mvo, exportRule))
            {
                Log.Debug("EvaluateExportRulesAsync: MVO {MvoId} is not in scope for export rule {RuleName}",
                    mvo.Id, exportRule.Name);
                continue;
            }

            // Find or create the pending export for this MVO → target system
            var pendingExport = await CreateOrUpdatePendingExportAsync(mvo, exportRule, changedAttributes);
            if (pendingExport != null)
            {
                pendingExports.Add(pendingExport);
            }
        }

        return pendingExports;
    }

    /// <summary>
    /// Evaluates if an MVO has fallen out of scope for any export rules and handles deprovisioning.
    /// Called when MVO attributes change to check if scoping criteria no longer match.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed</param>
    /// <param name="sourceSystem">The connected system that caused this change (for Q3 circular prevention)</param>
    /// <returns>List of PendingExports for deprovisioning actions</returns>
    public async Task<List<PendingExport>> EvaluateOutOfScopeExportsAsync(
        MetaverseObject mvo,
        ConnectedSystem? sourceSystem = null)
    {
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateOutOfScopeExportsAsync: MVO {MvoId} has no type set, cannot evaluate scope", mvo.Id);
            return pendingExports;
        }

        // Get all enabled export rules for this MVO's object type
        var exportRules = await GetExportRulesForObjectTypeAsync(mvo.Type.Id);

        foreach (var exportRule in exportRules)
        {
            // Q3: Skip if this is the source system (circular sync prevention)
            if (sourceSystem != null && exportRule.ConnectedSystemId == sourceSystem.Id)
            {
                continue;
            }

            // Check if MVO is in scope for this export rule
            if (IsMvoInScopeForExportRule(mvo, exportRule))
            {
                // Still in scope, no deprovisioning needed
                continue;
            }

            // MVO is OUT of scope - check if there's an existing CSO to deprovision
            var existingCso = await Application.Repository.ConnectedSystems
                .GetConnectedSystemObjectByMetaverseObjectIdAsync(mvo.Id, exportRule.ConnectedSystemId);

            if (existingCso == null)
            {
                // No CSO exists, nothing to deprovision
                continue;
            }

            Log.Information("EvaluateOutOfScopeExportsAsync: MVO {MvoId} is out of scope for export rule {RuleName}. Handling deprovisioning for CSO {CsoId}",
                mvo.Id, exportRule.Name, existingCso.Id);

            // Handle based on OutboundDeprovisionAction
            var pendingExport = await HandleOutboundDeprovisioningAsync(mvo, existingCso, exportRule);
            if (pendingExport != null)
            {
                pendingExports.Add(pendingExport);
            }
        }

        return pendingExports;
    }

    /// <summary>
    /// Handles deprovisioning based on the sync rule's OutboundDeprovisionAction setting.
    /// </summary>
    private async Task<PendingExport?> HandleOutboundDeprovisioningAsync(
        MetaverseObject mvo,
        ConnectedSystemObject cso,
        SyncRule exportRule)
    {
        switch (exportRule.OutboundDeprovisionAction)
        {
            case OutboundDeprovisionAction.Disconnect:
                // Break the join between CSO and MVO, but leave CSO in the target system
                Log.Information("HandleOutboundDeprovisioningAsync: Disconnecting CSO {CsoId} from MVO {MvoId} (OutboundDeprovisionAction=Disconnect)",
                    cso.Id, mvo.Id);

                // Break the join
                cso.MetaverseObject = null;
                cso.MetaverseObjectId = null;
                cso.JoinType = ConnectedSystemObjectJoinType.NotJoined;
                cso.DateJoined = null;

                // Remove from MVO's collection
                mvo.ConnectedSystemObjects.Remove(cso);

                // Update the CSO in the database
                await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(cso);

                // Check if this was the last connector for the MVO
                if (mvo.ConnectedSystemObjects.Count == 0 && mvo.Origin == MetaverseObjectOrigin.Projected)
                {
                    // Handle MVO deletion rules (set LastConnectorDisconnectedDate)
                    if (mvo.Type?.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected)
                    {
                        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
                        Log.Information("HandleOutboundDeprovisioningAsync: MVO {MvoId} has no more connectors. LastConnectorDisconnectedDate set to {Date}",
                            mvo.Id, mvo.LastConnectorDisconnectedDate);
                    }
                }

                return null; // No pending export needed for disconnect

            case OutboundDeprovisionAction.Delete:
                // Create a pending export to delete the CSO from the target system
                Log.Information("HandleOutboundDeprovisioningAsync: Creating delete PendingExport for CSO {CsoId} (OutboundDeprovisionAction=Delete)",
                    cso.Id);

                var pendingExport = new PendingExport
                {
                    Id = Guid.NewGuid(),
                    ConnectedSystemId = cso.ConnectedSystemId,
                    ConnectedSystemObject = cso,
                    ChangeType = PendingExportChangeType.Delete,
                    Status = PendingExportStatus.Pending,
                    SourceMetaverseObjectId = mvo.Id,
                    CreatedAt = DateTime.UtcNow
                };

                await Application.Repository.ConnectedSystems.CreatePendingExportAsync(pendingExport);

                Log.Information("HandleOutboundDeprovisioningAsync: Created delete PendingExport {ExportId} for CSO {CsoId} in system {SystemId}",
                    pendingExport.Id, cso.Id, cso.ConnectedSystemId);

                return pendingExport;

            default:
                Log.Warning("HandleOutboundDeprovisioningAsync: Unknown OutboundDeprovisionAction {Action} for rule {RuleName}",
                    exportRule.OutboundDeprovisionAction, exportRule.Name);
                return null;
        }
    }

    /// <summary>
    /// Evaluates export rules for an MVO that is being deleted.
    /// Implements Q4 decision: only create delete exports for Provisioned CSOs.
    /// </summary>
    public async Task<List<PendingExport>> EvaluateMvoDeletionAsync(MetaverseObject mvo)
    {
        var pendingExports = new List<PendingExport>();

        // Get all CSOs joined to this MVO
        var joinedCsos = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(mvo.Id);

        foreach (var cso in joinedCsos)
        {
            // Q4 Decision: Only create delete exports for Provisioned CSOs
            if (cso.JoinType != ConnectedSystemObjectJoinType.Provisioned)
            {
                Log.Debug("EvaluateMvoDeletionAsync: Skipping delete for CSO {CsoId} - JoinType is {JoinType}, not Provisioned",
                    cso.Id, cso.JoinType);
                continue;
            }

            var pendingExport = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = cso.ConnectedSystemId,
                ConnectedSystemObject = cso,
                ChangeType = PendingExportChangeType.Delete,
                Status = PendingExportStatus.Pending,
                SourceMetaverseObjectId = mvo.Id,
                CreatedAt = DateTime.UtcNow
            };

            await Application.Repository.ConnectedSystems.CreatePendingExportAsync(pendingExport);
            pendingExports.Add(pendingExport);

            Log.Information("EvaluateMvoDeletionAsync: Created delete PendingExport {ExportId} for CSO {CsoId} in system {SystemId}",
                pendingExport.Id, cso.Id, cso.ConnectedSystemId);
        }

        return pendingExports;
    }

    /// <summary>
    /// Gets all enabled export sync rules for a given MVO object type.
    /// </summary>
    private async Task<List<SyncRule>> GetExportRulesForObjectTypeAsync(int metaverseObjectTypeId)
    {
        var allSyncRules = await Application.Repository.ConnectedSystems.GetSyncRulesAsync();

        return allSyncRules
            .Where(sr => sr.Enabled &&
                         sr.Direction == SyncRuleDirection.Export &&
                         sr.MetaverseObjectTypeId == metaverseObjectTypeId)
            .ToList();
    }

    /// <summary>
    /// Checks if an MVO is in scope for an export rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    public bool IsMvoInScopeForExportRule(MetaverseObject mvo, SyncRule exportRule)
    {
        // No scoping criteria means all objects are in scope
        if (exportRule.ObjectScopingCriteriaGroups.Count == 0)
            return true;

        // Evaluate each criteria group (they are ORed together at the top level)
        foreach (var criteriaGroup in exportRule.ObjectScopingCriteriaGroups)
        {
            if (EvaluateScopingCriteriaGroup(mvo, criteriaGroup))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Evaluates a single scoping criteria group against an MVO.
    /// </summary>
    private bool EvaluateScopingCriteriaGroup(MetaverseObject mvo, SyncRuleScopingCriteriaGroup group)
    {
        var criteriaResults = new List<bool>();

        // Evaluate individual criteria
        foreach (var criterion in group.Criteria)
        {
            criteriaResults.Add(EvaluateScopingCriterion(mvo, criterion));
        }

        // Evaluate child groups recursively
        foreach (var childGroup in group.ChildGroups)
        {
            criteriaResults.Add(EvaluateScopingCriteriaGroup(mvo, childGroup));
        }

        if (criteriaResults.Count == 0)
            return true; // Empty group is always true

        // Apply AND/OR logic based on group type
        return group.Type switch
        {
            SearchGroupType.All => criteriaResults.All(r => r),
            SearchGroupType.Any => criteriaResults.Any(r => r),
            _ => false
        };
    }

    /// <summary>
    /// Evaluates a single scoping criterion against an MVO attribute.
    /// </summary>
    private bool EvaluateScopingCriterion(MetaverseObject mvo, SyncRuleScopingCriteria criterion)
    {
        if (criterion.MetaverseAttribute == null)
            return false;

        // Get the MVO attribute value
        var mvoAttributeValue = mvo.AttributeValues
            .FirstOrDefault(av => av.AttributeId == criterion.MetaverseAttribute.Id);

        // Handle null/missing attribute values
        if (mvoAttributeValue == null)
        {
            // Only Equals with null value should match
            return criterion.ComparisonType == SearchComparisonType.Equals &&
                   criterion.StringValue == null &&
                   criterion.IntValue == null &&
                   criterion.DateTimeValue == null &&
                   criterion.BoolValue == null &&
                   criterion.GuidValue == null;
        }

        // Evaluate based on attribute type
        return criterion.MetaverseAttribute.Type switch
        {
            AttributeDataType.Text => EvaluateStringComparison(mvoAttributeValue.StringValue, criterion.StringValue, criterion.ComparisonType),
            AttributeDataType.Number => EvaluateNumberComparison(mvoAttributeValue.IntValue, criterion.IntValue, criterion.ComparisonType),
            AttributeDataType.DateTime => EvaluateDateTimeComparison(mvoAttributeValue.DateTimeValue, criterion.DateTimeValue, criterion.ComparisonType),
            AttributeDataType.Boolean => EvaluateBooleanComparison(mvoAttributeValue.BoolValue, criterion.BoolValue, criterion.ComparisonType),
            AttributeDataType.Guid => EvaluateGuidComparison(mvoAttributeValue.GuidValue, criterion.GuidValue, criterion.ComparisonType),
            _ => false
        };
    }

    private bool EvaluateStringComparison(string? actual, string? expected, SearchComparisonType comparisonType)
    {
        return comparisonType switch
        {
            SearchComparisonType.Equals => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            SearchComparisonType.NotEquals => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            SearchComparisonType.StartsWith => actual?.StartsWith(expected ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            SearchComparisonType.NotStartsWith => !(actual?.StartsWith(expected ?? "", StringComparison.OrdinalIgnoreCase) ?? false),
            SearchComparisonType.EndsWith => actual?.EndsWith(expected ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            SearchComparisonType.NotEndsWith => !(actual?.EndsWith(expected ?? "", StringComparison.OrdinalIgnoreCase) ?? false),
            SearchComparisonType.Contains => actual?.Contains(expected ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            SearchComparisonType.NotContains => !(actual?.Contains(expected ?? "", StringComparison.OrdinalIgnoreCase) ?? false),
            _ => false
        };
    }

    private bool EvaluateNumberComparison(int? actual, int? expected, SearchComparisonType comparisonType)
    {
        if (!actual.HasValue || !expected.HasValue)
            return comparisonType == SearchComparisonType.Equals && actual == expected;

        return comparisonType switch
        {
            SearchComparisonType.Equals => actual.Value == expected.Value,
            SearchComparisonType.NotEquals => actual.Value != expected.Value,
            SearchComparisonType.LessThan => actual.Value < expected.Value,
            SearchComparisonType.LessThanOrEquals => actual.Value <= expected.Value,
            SearchComparisonType.GreaterThan => actual.Value > expected.Value,
            SearchComparisonType.GreaterThanOrEquals => actual.Value >= expected.Value,
            _ => false
        };
    }

    private bool EvaluateDateTimeComparison(DateTime? actual, DateTime? expected, SearchComparisonType comparisonType)
    {
        if (!actual.HasValue || !expected.HasValue)
            return comparisonType == SearchComparisonType.Equals && actual == expected;

        return comparisonType switch
        {
            SearchComparisonType.Equals => actual.Value == expected.Value,
            SearchComparisonType.NotEquals => actual.Value != expected.Value,
            SearchComparisonType.LessThan => actual.Value < expected.Value,
            SearchComparisonType.LessThanOrEquals => actual.Value <= expected.Value,
            SearchComparisonType.GreaterThan => actual.Value > expected.Value,
            SearchComparisonType.GreaterThanOrEquals => actual.Value >= expected.Value,
            _ => false
        };
    }

    private bool EvaluateBooleanComparison(bool? actual, bool? expected, SearchComparisonType comparisonType)
    {
        return comparisonType switch
        {
            SearchComparisonType.Equals => actual == expected,
            SearchComparisonType.NotEquals => actual != expected,
            _ => false
        };
    }

    private bool EvaluateGuidComparison(Guid? actual, Guid? expected, SearchComparisonType comparisonType)
    {
        return comparisonType switch
        {
            SearchComparisonType.Equals => actual == expected,
            SearchComparisonType.NotEquals => actual != expected,
            _ => false
        };
    }

    /// <summary>
    /// Creates or updates a PendingExport for an MVO change to a target system.
    /// For provisioning (Create) scenarios, also creates a CSO with Status=PendingProvisioning
    /// to establish the CSO↔MVO relationship before the object exists in the target system.
    /// </summary>
    private async Task<PendingExport?> CreateOrUpdatePendingExportAsync(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes)
    {
        // Find existing CSO for this MVO in the target system
        var existingCso = await Application.Repository.ConnectedSystems
            .GetConnectedSystemObjectByMetaverseObjectIdAsync(mvo.Id, exportRule.ConnectedSystemId);

        PendingExportChangeType changeType;
        ConnectedSystemObject? csoForExport = existingCso;

        if (existingCso == null)
        {
            // No CSO exists - check if we should provision
            if (exportRule.ProvisionToConnectedSystem != true)
            {
                Log.Debug("CreateOrUpdatePendingExportAsync: No CSO exists and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return null;
            }

            // Create CSO with PendingProvisioning status to establish the relationship before export
            csoForExport = await CreatePendingProvisioningCsoAsync(mvo, exportRule);
            changeType = PendingExportChangeType.Create;
        }
        else
        {
            changeType = PendingExportChangeType.Update;
        }

        // Create attribute value changes based on the export rule mappings
        var attributeChanges = CreateAttributeValueChanges(mvo, exportRule, changedAttributes);

        if (attributeChanges.Count == 0 && changeType == PendingExportChangeType.Update)
        {
            Log.Debug("CreateOrUpdatePendingExportAsync: No attribute changes for MVO {MvoId} to system {SystemId}",
                mvo.Id, exportRule.ConnectedSystemId);
            return null;
        }

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = exportRule.ConnectedSystemId,
            ConnectedSystemObject = csoForExport,
            ChangeType = changeType,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = mvo.Id,
            AttributeValueChanges = attributeChanges,
            CreatedAt = DateTime.UtcNow
        };

        await Application.Repository.ConnectedSystems.CreatePendingExportAsync(pendingExport);

        Log.Information("CreateOrUpdatePendingExportAsync: Created {ChangeType} PendingExport {ExportId} for MVO {MvoId} to system {SystemName} with {AttrCount} attribute changes",
            changeType, pendingExport.Id, mvo.Id, exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString(), attributeChanges.Count);

        return pendingExport;
    }

    /// <summary>
    /// Creates a Connected System Object with PendingProvisioning status for provisioning scenarios.
    /// This establishes the CSO↔MVO relationship before the object exists in the target system,
    /// ensuring that the subsequent import will correctly join rather than create a duplicate.
    /// </summary>
    private async Task<ConnectedSystemObject> CreatePendingProvisioningCsoAsync(MetaverseObject mvo, SyncRule exportRule)
    {
        if (exportRule.ConnectedSystemObjectType == null)
            throw new InvalidOperationException($"Export rule {exportRule.Name} has no ConnectedSystemObjectType configured.");

        // Find the external ID and secondary external ID attributes from the object type
        var externalIdAttribute = exportRule.ConnectedSystemObjectType.Attributes
            .FirstOrDefault(a => a.IsExternalId);
        var secondaryExternalIdAttribute = exportRule.ConnectedSystemObjectType.Attributes
            .FirstOrDefault(a => a.IsSecondaryExternalId);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = exportRule.ConnectedSystemId,
            TypeId = exportRule.ConnectedSystemObjectType.Id,
            Type = exportRule.ConnectedSystemObjectType,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            DateJoined = DateTime.UtcNow,
            Created = DateTime.UtcNow,
            ExternalIdAttributeId = externalIdAttribute?.Id ?? 0,
            SecondaryExternalIdAttributeId = secondaryExternalIdAttribute?.Id
        };

        // Add the CSO to the MVO's collection for navigation
        mvo.ConnectedSystemObjects.Add(cso);

        await Application.Repository.ConnectedSystems.CreateConnectedSystemObjectAsync(cso);

        Log.Information("CreatePendingProvisioningCsoAsync: Created PendingProvisioning CSO {CsoId} for MVO {MvoId} in system {SystemId}",
            cso.Id, mvo.Id, exportRule.ConnectedSystemId);

        return cso;
    }

    /// <summary>
    /// Creates PendingExportAttributeValueChange objects based on export rule mappings.
    /// Maps MVO attributes → CSO attributes.
    /// For export rules:
    /// - Sources[].MetaverseAttribute = the source MVO attribute
    /// - TargetConnectedSystemAttribute = the target CSO attribute
    /// </summary>
    private List<PendingExportAttributeValueChange> CreateAttributeValueChanges(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes)
    {
        var changes = new List<PendingExportAttributeValueChange>();

        foreach (var mapping in exportRule.AttributeFlowRules)
        {
            // For export rules, the target is the CSO attribute
            if (mapping.TargetConnectedSystemAttribute == null)
            {
                Log.Warning("CreateAttributeValueChanges: Export mapping has no TargetConnectedSystemAttribute set");
                continue;
            }

            foreach (var source in mapping.Sources)
            {
                // Handle expression-based mappings
                if (!string.IsNullOrWhiteSpace(source.Expression))
                {
                    try
                    {
                        // Build expression context with MVO attributes
                        var mvAttributes = BuildAttributeDictionary(mvo);
                        var context = new ExpressionContext(mvAttributes, null);

                        // Evaluate the expression
                        var result = ExpressionEvaluator.Evaluate(source.Expression, context);

                        if (result != null)
                        {
                            var change = new PendingExportAttributeValueChange
                            {
                                Id = Guid.NewGuid(),
                                AttributeId = mapping.TargetConnectedSystemAttribute.Id,
                                ChangeType = PendingExportAttributeChangeType.Update
                            };

                            // Set the value based on the result type
                            switch (result)
                            {
                                case string strValue:
                                    change.StringValue = strValue;
                                    break;
                                case int intValue:
                                    change.IntValue = intValue;
                                    break;
                                case DateTime dtValue:
                                    change.DateTimeValue = dtValue;
                                    break;
                                case bool boolValue:
                                    change.StringValue = boolValue.ToString();
                                    break;
                                case Guid guidValue:
                                    change.StringValue = guidValue.ToString();
                                    break;
                                case byte[] byteValue:
                                    change.ByteValue = byteValue;
                                    break;
                                default:
                                    // Fall back to string representation
                                    change.StringValue = result.ToString();
                                    break;
                            }

                            changes.Add(change);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "CreateAttributeValueChanges: Failed to evaluate expression '{Expression}' for attribute {AttributeName}",
                            source.Expression, mapping.TargetConnectedSystemAttribute.Name);
                    }

                    continue;
                }

                // Handle direct attribute flow mappings
                if (source.MetaverseAttribute == null)
                    continue;

                // Check if this attribute was changed
                var changedValue = changedAttributes
                    .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id);

                // For Create operations, include all mapped attributes, not just changed ones
                var mvoValue = changedValue ?? mvo.AttributeValues
                    .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id);

                if (mvoValue == null)
                    continue;

                var attributeChange = new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = mapping.TargetConnectedSystemAttribute.Id,
                    ChangeType = PendingExportAttributeChangeType.Update
                };

                // Set the appropriate value based on data type
                switch (source.MetaverseAttribute.Type)
                {
                    case AttributeDataType.Text:
                        attributeChange.StringValue = mvoValue.StringValue;
                        break;
                    case AttributeDataType.Number:
                        attributeChange.IntValue = mvoValue.IntValue;
                        break;
                    case AttributeDataType.DateTime:
                        attributeChange.DateTimeValue = mvoValue.DateTimeValue;
                        break;
                    case AttributeDataType.Boolean:
                        // Convert bool to string for now (model doesn't have BoolValue)
                        attributeChange.StringValue = mvoValue.BoolValue?.ToString();
                        break;
                    case AttributeDataType.Guid:
                        // Convert Guid to string for now (model doesn't have GuidValue)
                        attributeChange.StringValue = mvoValue.GuidValue?.ToString();
                        break;
                    case AttributeDataType.Binary:
                        attributeChange.ByteValue = mvoValue.ByteValue;
                        break;
                    case AttributeDataType.Reference:
                        // For reference attributes, store the MVO ID as unresolved reference - will be resolved during export execution
                        if (mvoValue.ReferenceValue != null)
                        {
                            attributeChange.UnresolvedReferenceValue = mvoValue.ReferenceValue.Id.ToString();
                        }
                        break;
                }

                changes.Add(attributeChange);
            }
        }

        return changes;
    }

    /// <summary>
    /// Builds a dictionary of attribute values from a Metaverse Object for expression evaluation.
    /// The dictionary keys are attribute names, and values are the attribute values.
    /// </summary>
    private Dictionary<string, object?> BuildAttributeDictionary(MetaverseObject mvo)
    {
        var attributes = new Dictionary<string, object?>();

        if (mvo.Type == null)
            return attributes;

        foreach (var attributeValue in mvo.AttributeValues)
        {
            if (attributeValue.Attribute == null)
                continue;

            var attributeName = attributeValue.Attribute.Name;

            // Use the appropriate typed value based on the attribute type
            object? value = attributeValue.Attribute.Type switch
            {
                AttributeDataType.Text => attributeValue.StringValue,
                AttributeDataType.Number => attributeValue.IntValue,
                AttributeDataType.DateTime => attributeValue.DateTimeValue,
                AttributeDataType.Boolean => attributeValue.BoolValue,
                AttributeDataType.Guid => attributeValue.GuidValue,
                AttributeDataType.Binary => attributeValue.ByteValue,
                AttributeDataType.Reference => attributeValue.ReferenceValue?.Id.ToString(),
                _ => null
            };

            attributes[attributeName] = value;
        }

        return attributes;
    }
}
