// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Resolves authenticated SSO identities to JIM MetaverseObjects, and bootstraps the
/// initial administrator just-in-time on first contact.
///
/// This logic is auth-scheme agnostic: it operates on an <see cref="SsoIdentity"/> DTO,
/// so the same provisioning runs whether the user arrives via the interactive browser
/// (cookie/OIDC) flow or via a bearer token (the PowerShell module / REST API). That is
/// what allows a fresh deployment to be administered entirely from the CLI without ever
/// signing in to the web portal.
///
/// Only the configured initial administrator (<c>JIM_SSO_INITIAL_ADMIN</c>) is created
/// just-in-time. Every other user must be provisioned through synchronisation.
/// </summary>
public class AuthServer
{
    private JimApplication Application { get; }

    internal AuthServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Resolves an authenticated SSO identity to its MetaverseObject, creating the initial
    /// administrator just-in-time if this is their first contact, and keeping the profile
    /// attributes the token owns in step with the identity (filling any that are missing). Returns <c>null</c> when the identity
    /// cannot be matched and is not the initial administrator (such users are provisioned
    /// via synchronisation, not from a token).
    /// </summary>
    /// <param name="identity">The provider-agnostic identity built from the validated token.</param>
    public async Task<MetaverseObject?> ResolveOrProvisionSsoIdentityAsync(SsoIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrEmpty(identity.UniqueId))
        {
            Log.Warning("AuthServer: SSO identity has no unique id; cannot resolve a JIM user.");
            return null;
        }

        var serviceSettings = await Application.ServiceSettings.GetServiceSettingsAsync() ??
            throw new InvalidOperationException("ServiceSettings was null. Cannot resolve SSO identity.");

        if (serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null)
            throw new InvalidOperationException("ServiceSettings.SSOUniqueIdentifierMetaverseAttribute is null.");

        var userType = await Application.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, false) ??
            throw new InvalidOperationException("Could not retrieve the User object type.");

        var identifierAttribute = serviceSettings.SSOUniqueIdentifierMetaverseAttribute;
        var initialAdminId = Environment.GetEnvironmentVariable(Constants.Config.SsoInitialAdmin);
        bool isInitialAdmin;
        Guid? identifierGuid = null;

        if (identifierAttribute.Type == AttributeDataType.Guid)
        {
            if (!TryReadGuidIdentifier(identity.UniqueId, identifierAttribute, serviceSettings.SSOUniqueIdentifierClaimType, out var guid))
                return null;

            identifierGuid = guid;
            isInitialAdmin = IdentifierParser.TryFromString(initialAdminId, out var initialAdminGuid) && initialAdminGuid == guid;
        }
        else
        {
            isInitialAdmin = !string.IsNullOrEmpty(initialAdminId) && identity.UniqueId == initialAdminId;
        }

        var user = identifierGuid is { } identifier
            ? await Application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(userType, identifierAttribute, identifier)
            : await Application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(userType, identifierAttribute, identity.UniqueId);

        // Bootstrap the initial admin just-in-time on first contact (web or API/CLI).
        if (user == null && isInitialAdmin)
            user = await CreateInitialAdminUserAsync(userType, identifierAttribute, identifierGuid, identity, serviceSettings.SSOUniqueIdentifierClaimType);

        // Unknown user who isn't the initial admin: they must be provisioned via synchronisation.
        if (user == null)
            return null;

        // The initial admin always retains the Administrator role, even if they already existed.
        if (isInitialAdmin && !await Application.Security.IsObjectInRoleAsync(user, Constants.BuiltInRoles.Administrator))
            await Application.Security.AddObjectToRoleAsync(user, Constants.BuiltInRoles.Administrator);

        // Keep the profile attributes the token owns in step with it, and fill any that are missing. This is a
        // no-op for a freshly created user (attributes were set during creation) and for one already up to date.
        await RefreshUserAttributesFromIdentityAsync(user, identity);

        return user;
    }

    /// <summary>
    /// Creates the initial admin user just-in-time on their first sign-in, with all available
    /// identity values populated in a single operation (producing one "Created" change event),
    /// and assigns the Administrator role. Both steps are recorded as Activities.
    /// </summary>
    /// <param name="userType">The User object type.</param>
    /// <param name="uniqueIdentifierAttribute">The attribute sign-in matches people on.</param>
    /// <param name="identifierGuid">The identity's GUID, when that attribute is Guid-typed; it is stored in place of the claim text.</param>
    /// <param name="identity">The identity built from the validated token.</param>
    /// <param name="identifierClaimType">The claim the identifier was read from, recorded as the identifier value's source.</param>
    private async Task<MetaverseObject> CreateInitialAdminUserAsync(
        MetaverseObjectType userType,
        MetaverseAttribute uniqueIdentifierAttribute,
        Guid? identifierGuid,
        SsoIdentity identity,
        string? identifierClaimType)
    {
        Log.Information("AuthServer: Creating initial admin user just-in-time ({UniqueId}).", identity.UniqueId);

        var activity = new Activity
        {
            TargetType = ActivityTargetType.MetaverseObject,
            TargetOperationType = ActivityTargetOperationType.Create,
            Message = "Creating initial administrator user on first sign-in"
        };
        await Application.Activities.CreateSystemActivityAsync(activity);

        try
        {
            // Set Origin to Internal to protect the admin from automatic deletion rules.
            var user = new MetaverseObject
            {
                Type = userType,
                Origin = MetaverseObjectOrigin.Internal
            };

            // unique identifier attribute (required)
            user.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = user,
                Attribute = uniqueIdentifierAttribute,
                StringValue = identifierGuid == null ? identity.UniqueId : null,
                GuidValue = identifierGuid,
                ContributedByKind = ValueContributorKind.SignInClaim,
                ContributedByClaimType = identifierClaimType
            });

            // Type attribute (required). A value JIM fixes, not one the token supplied, so its source is JIM itself.
            var typeAttribute = await Application.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.Type) ??
                throw new InvalidOperationException($"Couldn't get essential attribute: {Constants.BuiltInAttributes.Type}");
            user.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = user,
                Attribute = typeAttribute,
                StringValue = "PersonEntity",
                ContributedByKind = ValueContributorKind.System
            });

            // populate optional attributes from the identity so everything is set in one go
            await AddAttributeFromValueAsync(user, identity.DisplayName, Constants.BuiltInAttributes.DisplayName, SsoIdentity.DisplayNameClaimType);
            await AddAttributeFromValueAsync(user, identity.FirstName, Constants.BuiltInAttributes.FirstName, SsoIdentity.FirstNameClaimType);
            await AddAttributeFromValueAsync(user, identity.LastName, Constants.BuiltInAttributes.LastName, SsoIdentity.LastNameClaimType);
            await AddAttributeFromValueAsync(user, identity.UserPrincipalName, Constants.BuiltInAttributes.UserPrincipalName, SsoIdentity.UserPrincipalNameClaimType);

            // System made the object, inside the Activity recording its creation, and the change record says both.
            await Application.Metaverse.CreateMetaverseObjectAsync(
                user,
                initiatedByType: activity.InitiatedByType,
                initiatedByName: activity.InitiatedByName,
                changeInitiatorType: MetaverseObjectChangeInitiatorType.System,
                activityId: activity.Id);

            // update the parent activity with the user's details now that the MVO exists
            activity.TargetName = user.Name;
            activity.MetaverseObjectId = user.Id;

            // assign the Administrator role as a child activity
            var roleActivity = new Activity
            {
                ParentActivityId = activity.Id,
                TargetType = ActivityTargetType.MetaverseObject,
                TargetOperationType = ActivityTargetOperationType.Update,
                TargetName = user.Name,
                MetaverseObjectId = user.Id,
                Message = $"Assigning {Constants.BuiltInRoles.Administrator} role to initial administrator"
            };
            await Application.Activities.CreateSystemActivityAsync(roleActivity);

            try
            {
                await Application.Security.AddObjectToRoleAsync(user, Constants.BuiltInRoles.Administrator);
                roleActivity.Message = $"Assigned {Constants.BuiltInRoles.Administrator} role to initial administrator";
                await Application.Activities.CompleteActivityAsync(roleActivity);
            }
            catch (Exception ex)
            {
                // Record the failure against the audit activity before propagating; any failure
                // mode (persistence, role lookup) must mark the activity failed and rethrow.
                await Application.Activities.FailActivityWithErrorAsync(roleActivity, ex);
                throw;
            }

            activity.Message = $"Created initial administrator '{user.NameOrId}'";
            await Application.Activities.CompleteActivityAsync(activity);

            Log.Information("AuthServer: Initial admin user created and assigned {Role} role.", Constants.BuiltInRoles.Administrator);
            return user;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Finds the user an SSO identifier claim names, without provisioning anyone: the lookup the API's role enrichment
    /// makes on every request, matching people exactly as <see cref="ResolveOrProvisionSsoIdentityAsync"/> does.
    /// </summary>
    /// <param name="claimValue">The value of the identifier claim.</param>
    /// <returns>The user, or null when the claim names nobody, or cannot name anyone.</returns>
    public async Task<MetaverseObject?> FindSsoUserAsync(string claimValue)
    {
        var serviceSettings = await Application.ServiceSettings.GetServiceSettingsAsync();
        var identifierAttribute = serviceSettings?.SSOUniqueIdentifierMetaverseAttribute;
        if (identifierAttribute == null || string.IsNullOrEmpty(claimValue))
            return null;

        var userType = await Application.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, false);
        if (userType == null)
            return null;

        if (identifierAttribute.Type != AttributeDataType.Guid)
            return await Application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(userType, identifierAttribute, claimValue);

        return TryReadGuidIdentifier(claimValue, identifierAttribute, serviceSettings!.SSOUniqueIdentifierClaimType, out var guid)
            ? await Application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(userType, identifierAttribute, guid)
            : null;
    }

    /// <summary>
    /// Reads the GUID a claim carries for a Guid-typed identifier attribute, such as the Object GUID AD FS issues as
    /// <c>oid</c> (#2438): GUID text, or the attribute's 16 bytes in base64. Logs why when it carries neither.
    /// </summary>
    private static bool TryReadGuidIdentifier(string claimValue, MetaverseAttribute identifierAttribute, string? claimType, out Guid guid)
    {
        if (IdentifierParser.TryFromStringOrMicrosoftBase64(claimValue, out guid))
            return true;

        Log.Warning("AuthServer: The SSO identity's {ClaimType} claim is not a GUID, so it cannot match {Attribute}, which holds GUIDs. " +
                    "Check {ClaimTypeSetting} and {AttributeSetting}.",
            LogSanitiser.Sanitise(claimType), identifierAttribute.Name, Constants.Config.SsoClaimType, Constants.Config.SsoMvAttribute);
        return false;
    }

    /// <summary>
    /// What is wrong with the SSO identifier settings, as a sentence naming the setting to change, or null when sign-in
    /// can match people with them. The identifier attribute must hold text or GUIDs, and when it holds GUIDs the initial
    /// administrator must be named by one (#2438).
    /// </summary>
    /// <param name="identifierAttribute">The attribute <c>JIM_SSO_MV_ATTRIBUTE</c> names.</param>
    /// <param name="initialAdmin">The value of <c>JIM_SSO_INITIAL_ADMIN</c>, if set.</param>
    public static string? DescribeSsoIdentifierProblem(MetaverseAttribute identifierAttribute, string? initialAdmin)
    {
        ArgumentNullException.ThrowIfNull(identifierAttribute);

        switch (identifierAttribute.Type)
        {
            case AttributeDataType.Guid:
                return string.IsNullOrEmpty(initialAdmin) || IdentifierParser.TryFromString(initialAdmin, out _)
                    ? null
                    : $"{Constants.Config.SsoInitialAdmin} must be a GUID, such as 550e8400-e29b-41d4-a716-446655440000, because " +
                      $"{Constants.Config.SsoMvAttribute} names '{identifierAttribute.Name}', which holds GUIDs.";

            case AttributeDataType.Text:
            case AttributeDataType.NotSet:
                return null;

            default:
                return $"{Constants.Config.SsoMvAttribute} names '{identifierAttribute.Name}', a {identifierAttribute.Type} attribute; " +
                       "sign-in can only match people on a Text or Guid attribute.";
        }
    }

    /// <summary>
    /// Adds an attribute value to a MetaverseObject during creation, if a value is present, recording the sign-in
    /// claim it was read from as its source.
    /// </summary>
    private async Task AddAttributeFromValueAsync(MetaverseObject user, string? value, string metaverseAttributeName, string claimType)
    {
        if (string.IsNullOrEmpty(value))
            return;

        var attribute = await Application.Metaverse.GetMetaverseAttributeAsync(metaverseAttributeName);
        if (attribute == null)
            return;

        user.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = user,
            Attribute = attribute,
            StringValue = value,
            ContributedByKind = ValueContributorKind.SignInClaim,
            ContributedByClaimType = claimType
        });
        Log.Verbose("AuthServer: Added {Attribute} from identity.", metaverseAttributeName);
    }

    /// <summary>
    /// Keeps a user's profile attributes in step with the identity (#2383): fills an attribute the user has no value
    /// for, and refreshes one whose value the token owns (a sign-in claim set it) when the token now says something
    /// different. A value from any other source, such as a Synchronisation Rule, is left untouched, so the token never
    /// fights synchronisation; so is a value with no recorded source, since nothing says the token owns it. A claim the
    /// token no longer carries leaves its value in place. Persists only when something changed, recording the change
    /// as System's.
    /// </summary>
    private async Task RefreshUserAttributesFromIdentityAsync(MetaverseObject user, SsoIdentity identity)
    {
        var additions = new List<MetaverseObjectAttributeValue>();
        var removals = new List<MetaverseObjectAttributeValue>();

        await RefreshAttributeFromClaimAsync(user, additions, removals, identity.DisplayName, Constants.BuiltInAttributes.DisplayName, SsoIdentity.DisplayNameClaimType);
        await RefreshAttributeFromClaimAsync(user, additions, removals, identity.FirstName, Constants.BuiltInAttributes.FirstName, SsoIdentity.FirstNameClaimType);
        await RefreshAttributeFromClaimAsync(user, additions, removals, identity.LastName, Constants.BuiltInAttributes.LastName, SsoIdentity.LastNameClaimType);
        await RefreshAttributeFromClaimAsync(user, additions, removals, identity.UserPrincipalName, Constants.BuiltInAttributes.UserPrincipalName, SsoIdentity.UserPrincipalNameClaimType);

        if (additions.Count > 0)
        {
            await Application.Metaverse.UpdateMetaverseObjectAsync(
                user,
                additions: additions,
                removals: removals,
                initiatedByType: ActivityInitiatorType.System,
                initiatedByName: "System",
                changeInitiatorType: MetaverseObjectChangeInitiatorType.System);
            Log.Debug("AuthServer: Filled {Filled} and refreshed {Refreshed} attribute value(s) on user {UserId} from the SSO identity.",
                additions.Count - removals.Count, removals.Count, user.Id);
        }
    }

    /// <summary>
    /// Stages one profile attribute's change from the identity, recording the sign-in claim as its source: an addition
    /// when the user has no value for the attribute, or, when the one value it holds came from a sign-in claim and
    /// differs from the token's, that value updated in place, with a snapshot of what it held staged as the removal so
    /// the change history reads as the old value replaced by the new. Anything else stages nothing.
    /// </summary>
    private async Task RefreshAttributeFromClaimAsync(
        MetaverseObject user,
        List<MetaverseObjectAttributeValue> additions,
        List<MetaverseObjectAttributeValue> removals,
        string? value,
        string metaverseAttributeName,
        string claimType)
    {
        if (string.IsNullOrEmpty(value))
            return;

        var existing = user.AttributeValues.Where(av => av.Attribute?.Name == metaverseAttributeName).ToList();
        if (existing.Count == 0)
        {
            var attribute = await Application.Metaverse.GetMetaverseAttributeAsync(metaverseAttributeName);
            if (attribute == null)
                return;

            var attributeValue = new MetaverseObjectAttributeValue
            {
                Attribute = attribute,
                StringValue = value,
                ContributedByKind = ValueContributorKind.SignInClaim,
                ContributedByClaimType = claimType
            };
            user.AttributeValues.Add(attributeValue);
            additions.Add(attributeValue);
            return;
        }

        // The profile attributes are single-valued; more than one value means something else manages the attribute.
        if (existing.Count != 1)
            return;

        // Only a value the token owns is the token's to refresh. A recorded Connected System is the authority for a
        // synchronised value, whatever kind sits beside it (#2472).
        var current = existing[0];
        if (current.ContributedByKind != ValueContributorKind.SignInClaim || current.ContributedBySystemId.HasValue)
            return;

        if (string.Equals(current.StringValue, value, StringComparison.Ordinal) &&
            string.Equals(current.ContributedByClaimType, claimType, StringComparison.Ordinal))
            return;

        removals.Add(current.CopyFor(user));
        current.StringValue = value;
        current.ContributedByClaimType = claimType;
        additions.Add(current);
        Log.Verbose("AuthServer: Refreshed {Attribute} from identity.", metaverseAttributeName);
    }
}
