// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
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
    /// administrator just-in-time if this is their first contact, and supplementing any
    /// missing profile attributes from the identity. Returns <c>null</c> when the identity
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

        var user = await Application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(
            userType, serviceSettings.SSOUniqueIdentifierMetaverseAttribute, identity.UniqueId);

        var initialAdminId = Environment.GetEnvironmentVariable(Constants.Config.SsoInitialAdmin);
        var isInitialAdmin = !string.IsNullOrEmpty(initialAdminId) && identity.UniqueId == initialAdminId;

        // Bootstrap the initial admin just-in-time on first contact (web or API/CLI).
        if (user == null && isInitialAdmin)
            user = await CreateInitialAdminUserAsync(userType, serviceSettings.SSOUniqueIdentifierMetaverseAttribute, identity);

        // Unknown user who isn't the initial admin: they must be provisioned via synchronisation.
        if (user == null)
            return null;

        // The initial admin always retains the Administrator role, even if they already existed.
        if (isInitialAdmin && !await Application.Security.IsObjectInRoleAsync(user, Constants.BuiltInRoles.Administrator))
            await Application.Security.AddObjectToRoleAsync(user, Constants.BuiltInRoles.Administrator);

        // Supplement any missing profile attributes from the identity. This is a no-op for a
        // freshly created user (attributes were set during creation) and for an existing user
        // whose attributes are already populated.
        await SupplementUserAttributesAsync(user, identity);

        return user;
    }

    /// <summary>
    /// Creates the initial admin user just-in-time on their first sign-in, with all available
    /// identity values populated in a single operation (producing one "Created" change event),
    /// and assigns the Administrator role. Both steps are recorded as Activities.
    /// </summary>
    private async Task<MetaverseObject> CreateInitialAdminUserAsync(
        MetaverseObjectType userType,
        MetaverseAttribute uniqueIdentifierAttribute,
        SsoIdentity identity)
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
                StringValue = identity.UniqueId
            });

            // Type attribute (required)
            var typeAttribute = await Application.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.Type) ??
                throw new InvalidOperationException($"Couldn't get essential attribute: {Constants.BuiltInAttributes.Type}");
            user.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = user,
                Attribute = typeAttribute,
                StringValue = "PersonEntity"
            });

            // populate optional attributes from the identity so everything is set in one go
            await AddAttributeFromValueAsync(user, identity.DisplayName, Constants.BuiltInAttributes.DisplayName);
            await AddAttributeFromValueAsync(user, identity.FirstName, Constants.BuiltInAttributes.FirstName);
            await AddAttributeFromValueAsync(user, identity.LastName, Constants.BuiltInAttributes.LastName);
            await AddAttributeFromValueAsync(user, identity.UserPrincipalName, Constants.BuiltInAttributes.UserPrincipalName);

            await Application.Metaverse.CreateMetaverseObjectAsync(
                user,
                changeInitiatorType: MetaverseObjectChangeInitiatorType.System);

            // update the parent activity with the user's details now that the MVO exists
            activity.TargetName = user.DisplayName;
            activity.MetaverseObjectId = user.Id;

            // assign the Administrator role as a child activity
            var roleActivity = new Activity
            {
                ParentActivityId = activity.Id,
                TargetType = ActivityTargetType.MetaverseObject,
                TargetOperationType = ActivityTargetOperationType.Update,
                TargetName = user.DisplayName,
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

            activity.Message = $"Created initial administrator '{user.DisplayName}'";
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
    /// Adds an attribute value to a MetaverseObject during creation, if a value is present.
    /// </summary>
    private async Task AddAttributeFromValueAsync(MetaverseObject user, string? value, string metaverseAttributeName)
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
            StringValue = value
        });
        Log.Verbose("AuthServer: Added {Attribute} from identity.", metaverseAttributeName);
    }

    /// <summary>
    /// Supplements an existing user's missing profile attributes from the identity, persisting
    /// only the additions. Attributes already present are left untouched.
    /// </summary>
    private async Task SupplementUserAttributesAsync(MetaverseObject user, SsoIdentity identity)
    {
        var additions = new List<MetaverseObjectAttributeValue>();

        await AddMissingAttributeAsync(user, additions, identity.DisplayName, Constants.BuiltInAttributes.DisplayName);
        await AddMissingAttributeAsync(user, additions, identity.FirstName, Constants.BuiltInAttributes.FirstName);
        await AddMissingAttributeAsync(user, additions, identity.LastName, Constants.BuiltInAttributes.LastName);
        await AddMissingAttributeAsync(user, additions, identity.UserPrincipalName, Constants.BuiltInAttributes.UserPrincipalName);

        if (additions.Count > 0)
        {
            await Application.Metaverse.UpdateMetaverseObjectAsync(
                user,
                additions: additions,
                changeInitiatorType: MetaverseObjectChangeInitiatorType.System);
            Log.Debug("AuthServer: Supplemented {Count} attribute value(s) on user {UserId} from the SSO identity.", additions.Count, user.Id);
        }
    }

    /// <summary>
    /// Stages an attribute addition when the value is present and the user does not already
    /// have a value for that attribute.
    /// </summary>
    private async Task AddMissingAttributeAsync(
        MetaverseObject user,
        List<MetaverseObjectAttributeValue> additions,
        string? value,
        string metaverseAttributeName)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (user.HasAttributeValue(metaverseAttributeName))
            return;

        var attribute = await Application.Metaverse.GetMetaverseAttributeAsync(metaverseAttributeName);
        if (attribute == null)
            return;

        var attributeValue = new MetaverseObjectAttributeValue
        {
            Attribute = attribute,
            StringValue = value
        };
        user.AttributeValues.Add(attributeValue);
        additions.Add(attributeValue);
    }
}
