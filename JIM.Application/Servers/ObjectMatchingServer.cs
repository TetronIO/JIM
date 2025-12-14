using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// Provides object matching functionality for both import (CSO→MVO join) and export evaluation (MVO→CSO lookup).
/// Supports two modes:
/// - ConnectedSystem mode (default): Uses ObjectMatchingRules defined on ConnectedSystemObjectType
/// - SyncRule mode (advanced): Uses ObjectMatchingRules defined on individual SyncRules
/// </summary>
public class ObjectMatchingServer
{
    #region accessors
    private JimApplication Application { get; }
    #endregion

    #region constructors
    internal ObjectMatchingServer(JimApplication application)
    {
        Application = application;
    }
    #endregion

    #region public methods

    /// <summary>
    /// Attempts to find a Metaverse Object that matches a Connected System Object using the appropriate matching rules.
    /// This is used during import to join CSOs to existing MVOs.
    /// </summary>
    /// <param name="connectedSystemObject">The CSO to find a matching MVO for.</param>
    /// <param name="connectedSystem">The Connected System (needed to determine matching mode).</param>
    /// <param name="syncRule">The sync rule being evaluated. Used to get rules in SyncRule mode, or to filter by object type in ConnectedSystem mode.</param>
    /// <returns>The matching MVO, or null if no match found.</returns>
    /// <exception cref="MultipleMatchesException">Thrown if multiple MVOs match the criteria.</exception>
    public async Task<MetaverseObject?> FindMatchingMetaverseObjectAsync(
        ConnectedSystemObject connectedSystemObject,
        ConnectedSystem connectedSystem,
        SyncRule syncRule)
    {
        var matchingRules = GetMatchingRulesForImport(connectedSystem, syncRule, connectedSystemObject.TypeId);

        if (matchingRules.Count == 0)
        {
            Log.Debug("FindMatchingMetaverseObjectAsync: No matching rules found for CSO type {TypeId}", connectedSystemObject.TypeId);
            return null;
        }

        // Evaluate rules in order until we find a match
        foreach (var matchingRule in matchingRules.OrderBy(r => r.Order))
        {
            if (!matchingRule.IsValid())
            {
                Log.Warning("FindMatchingMetaverseObjectAsync: Skipping invalid matching rule {RuleId}", matchingRule.Id);
                continue;
            }

            try
            {
                var mvo = await Application.Repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(
                    connectedSystemObject,
                    syncRule.MetaverseObjectType!,
                    matchingRule);

                if (mvo != null)
                {
                    Log.Debug("FindMatchingMetaverseObjectAsync: Found MVO {MvoId} using rule {RuleId}", mvo.Id, matchingRule.Id);
                    return mvo;
                }
            }
            catch (MultipleMatchesException)
            {
                // Re-throw - caller needs to handle this
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FindMatchingMetaverseObjectAsync: Error evaluating matching rule {RuleId}", matchingRule.Id);
                // Continue to next rule
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to find an existing Connected System Object in a target system that matches a Metaverse Object.
    /// This is used during export evaluation to find existing CSOs for provisioning/update decisions.
    /// </summary>
    /// <param name="metaverseObject">The MVO to find a matching CSO for.</param>
    /// <param name="connectedSystem">The target Connected System.</param>
    /// <param name="syncRule">The export sync rule being evaluated.</param>
    /// <returns>The matching CSO, or null if no match found.</returns>
    public async Task<ConnectedSystemObject?> FindMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        SyncRule syncRule)
    {
        var matchingRules = GetMatchingRulesForExport(connectedSystem, syncRule);

        if (matchingRules.Count == 0)
        {
            Log.Debug("FindMatchingConnectedSystemObjectAsync: No matching rules found for export to CS {CsId}", connectedSystem.Id);
            return null;
        }

        // Evaluate rules in order until we find a match
        foreach (var matchingRule in matchingRules.OrderBy(r => r.Order))
        {
            if (!matchingRule.IsValid())
            {
                Log.Warning("FindMatchingConnectedSystemObjectAsync: Skipping invalid matching rule {RuleId}", matchingRule.Id);
                continue;
            }

            try
            {
                var cso = await Application.Repository.ConnectedSystems.FindConnectedSystemObjectUsingMatchingRuleAsync(
                    metaverseObject,
                    connectedSystem,
                    syncRule.ConnectedSystemObjectType!,
                    matchingRule);

                if (cso != null)
                {
                    Log.Debug("FindMatchingConnectedSystemObjectAsync: Found CSO {CsoId} using rule {RuleId}", cso.Id, matchingRule.Id);
                    return cso;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FindMatchingConnectedSystemObjectAsync: Error evaluating matching rule {RuleId}", matchingRule.Id);
                // Continue to next rule
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates a single matching rule to compute the expected external ID value that would be used
    /// to create a new object in the connected system. This is used during provisioning to pre-populate
    /// CSO attributes before export.
    /// </summary>
    /// <param name="metaverseObject">The source MVO being provisioned.</param>
    /// <param name="matchingRule">The matching rule defining how to compute the value.</param>
    /// <returns>The computed value, or null if the value cannot be computed.</returns>
    public object? ComputeMatchingValueFromMvo(MetaverseObject metaverseObject, ObjectMatchingRule matchingRule)
    {
        if (!matchingRule.IsValid() || matchingRule.Sources.Count == 0)
            return null;

        var source = matchingRule.Sources.OrderBy(s => s.Order).First();

        // For export matching, the source should reference an MVO attribute
        if (source.MetaverseAttribute != null)
        {
            var attributeValue = metaverseObject.AttributeValues
                .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id || av.Attribute?.Id == source.MetaverseAttribute.Id);

            if (attributeValue == null)
                return null;

            // Return the appropriate value based on what's populated
            if (!string.IsNullOrEmpty(attributeValue.StringValue))
                return attributeValue.StringValue;
            if (attributeValue.IntValue.HasValue)
                return attributeValue.IntValue.Value;
            if (attributeValue.GuidValue.HasValue)
                return attributeValue.GuidValue.Value;
            if (attributeValue.BoolValue.HasValue)
                return attributeValue.BoolValue.Value;
            if (attributeValue.DateTimeValue.HasValue)
                return attributeValue.DateTimeValue.Value;
            if (attributeValue.ByteValue != null)
                return attributeValue.ByteValue;

            return null;
        }

        // Function-based sources not yet supported
        if (source.Function != null)
        {
            Log.Warning("ComputeMatchingValueFromMvo: Function-based matching rules not yet supported");
            return null;
        }

        return null;
    }

    #endregion

    #region private methods

    /// <summary>
    /// Gets the appropriate matching rules for import (CSO→MVO join) based on the Connected System's mode.
    /// </summary>
    private List<ObjectMatchingRule> GetMatchingRulesForImport(ConnectedSystem connectedSystem, SyncRule syncRule, int csoTypeId)
    {
        return connectedSystem.ObjectMatchingRuleMode switch
        {
            ObjectMatchingRuleMode.ConnectedSystem => GetConnectedSystemObjectTypeRules(connectedSystem, csoTypeId),
            ObjectMatchingRuleMode.SyncRule => syncRule.ObjectMatchingRules.ToList(),
            _ => new List<ObjectMatchingRule>()
        };
    }

    /// <summary>
    /// Gets the appropriate matching rules for export (MVO→CSO lookup) based on the Connected System's mode.
    /// </summary>
    private List<ObjectMatchingRule> GetMatchingRulesForExport(ConnectedSystem connectedSystem, SyncRule syncRule)
    {
        return connectedSystem.ObjectMatchingRuleMode switch
        {
            ObjectMatchingRuleMode.ConnectedSystem => GetConnectedSystemObjectTypeRules(connectedSystem, syncRule.ConnectedSystemObjectTypeId),
            ObjectMatchingRuleMode.SyncRule => syncRule.ObjectMatchingRules.ToList(),
            _ => new List<ObjectMatchingRule>()
        };
    }

    /// <summary>
    /// Gets matching rules from the ConnectedSystemObjectType (Mode A - default).
    /// </summary>
    private List<ObjectMatchingRule> GetConnectedSystemObjectTypeRules(ConnectedSystem connectedSystem, int? objectTypeId)
    {
        if (objectTypeId == null || connectedSystem.ObjectTypes == null)
            return new List<ObjectMatchingRule>();

        var objectType = connectedSystem.ObjectTypes.FirstOrDefault(ot => ot.Id == objectTypeId);
        return objectType?.ObjectMatchingRules?.ToList() ?? new List<ObjectMatchingRule>();
    }

    #endregion
}
