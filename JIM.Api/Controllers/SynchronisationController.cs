using JIM.Application;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SynchronisationController : ControllerBase
    {
        private readonly ILogger<SynchronisationController> _logger;
        private readonly JimApplication _application;

        public SynchronisationController(ILogger<SynchronisationController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("connected-systems")]
        public async Task<IEnumerable<ConnectedSystem>> GetConnectedSystemsAsync()
        {
            _logger.LogTrace($"Someone requested the connected systems");
            return await _application.ConnectedSystems.GetConnectedSystemsAsync();
        }

        [HttpGet("connected-systems/{connectedSystemId}")]
        public async Task<ConnectedSystem?> GetConnectedSystemAsync(int connectedSystemId)
        {
            _logger.LogTrace($"Someone requested a connected system: {connectedSystemId}");
            return await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        }

        [HttpGet("connected-systems/{connectedSystemId}/object-types")]
        public async Task<IEnumerable<ConnectedSystemObjectType>?> GetConnectedSystemObjectTypesAsync(int connectedSystemId)
        {
            _logger.LogTrace($"Someone requested object types for connected system: {connectedSystemId}");
            return await _application.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
        }

        [HttpGet("connected-systems/{connectedSystemId}/objects/{id}")]
        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
        {
            _logger.LogTrace($"Someone requested an object ({id}) connected system: {connectedSystemId}");
            return await _application.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
        }

        /// <summary>
        /// Gets a preview of what will be affected by deleting a Connected System.
        /// Call this before DeleteConnectedSystemAsync to inform the user of the impact.
        /// </summary>
        /// <param name="connectedSystemId">The ID of the Connected System to preview deletion for.</param>
        /// <returns>A preview showing counts of affected objects and any warnings.</returns>
        [HttpGet("connected-systems/{connectedSystemId}/deletion-preview")]
        public async Task<ActionResult<ConnectedSystemDeletionPreview>> GetConnectedSystemDeletionPreviewAsync(int connectedSystemId)
        {
            _logger.LogInformation("Deletion preview requested for connected system: {Id}", connectedSystemId);

            var preview = await _application.ConnectedSystems.GetDeletionPreviewAsync(connectedSystemId);
            if (preview == null)
                return NotFound($"Connected System {connectedSystemId} not found.");

            return Ok(preview);
        }

        /// <summary>
        /// Deletes a Connected System and all its related data.
        /// This operation may execute synchronously or be queued as a background job depending on system size.
        /// </summary>
        /// <param name="connectedSystemId">The ID of the Connected System to delete.</param>
        /// <returns>The result of the deletion request including outcome and tracking IDs.</returns>
        /// <response code="200">Deletion completed immediately.</response>
        /// <response code="202">Deletion has been queued as a background job.</response>
        /// <response code="400">Deletion failed.</response>
        /// <response code="401">User could not be identified from authentication token.</response>
        [HttpDelete("connected-systems/{connectedSystemId}")]
        [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status202Accepted)]
        [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<ConnectedSystemDeletionResult>> DeleteConnectedSystemAsync(int connectedSystemId)
        {
            _logger.LogInformation("Deletion requested for connected system: {Id}", connectedSystemId);

            // Get the current user from the JWT claims
            var initiatedBy = await GetCurrentUserAsync();
            if (initiatedBy == null)
            {
                _logger.LogWarning("Could not identify user from JWT claims for deletion request");
                return Unauthorized("Could not identify user from authentication token");
            }

            var result = await _application.ConnectedSystems.DeleteAsync(connectedSystemId, initiatedBy);

            if (!result.Success)
                return BadRequest(result);

            // Return 202 Accepted for queued operations, 200 OK for immediate completion
            if (result.Outcome == DeletionOutcome.QueuedAsBackgroundJob ||
                result.Outcome == DeletionOutcome.QueuedAfterSync)
            {
                return Accepted(result);
            }

            return Ok(result);
        }

        /// <summary>
        /// Resolves the current user from JWT claims by looking up their SSO identifier in the Metaverse.
        /// </summary>
        private async Task<Models.Core.MetaverseObject?> GetCurrentUserAsync()
        {
            if (User.Identity?.IsAuthenticated != true)
                return null;

            // Get the service settings to know which claim type contains the unique identifier
            var serviceSettings = await _application.ServiceSettings.GetServiceSettingsAsync();
            if (serviceSettings?.SSOUniqueIdentifierClaimType == null ||
                serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null)
            {
                _logger.LogError("Service settings are not configured for SSO claim mapping");
                return null;
            }

            // Get the unique identifier from the JWT claims
            var uniqueIdClaimValue = IdentityUtilities.GetSsoUniqueIdentifier(
                User,
                serviceSettings.SSOUniqueIdentifierClaimType);

            if (string.IsNullOrEmpty(uniqueIdClaimValue))
            {
                _logger.LogWarning("JWT does not contain the expected claim: {ClaimType}",
                    serviceSettings.SSOUniqueIdentifierClaimType);
                return null;
            }

            // Look up the user in the Metaverse
            var userType = await _application.Metaverse.GetMetaverseObjectTypeAsync(
                Models.Core.Constants.BuiltInObjectTypes.Users,
                false);

            if (userType == null)
            {
                _logger.LogError("Could not find User object type in Metaverse");
                return null;
            }

            return await _application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(
                userType,
                serviceSettings.SSOUniqueIdentifierMetaverseAttribute,
                uniqueIdClaimValue);
        }

        [HttpGet("sync-rules")]
        public async Task<IEnumerable<SyncRule>?> GetSyncRulesAsync()
        {
            _logger.LogTrace("Someone requested the synchronisation rules");
            return await _application.ConnectedSystems.GetSyncRulesAsync();
        }

        [HttpGet("sync-rules/{id}")]
        public async Task<SyncRule?> GetSyncRuleAsync(int id)
        {
            _logger.LogTrace($"Someone requested a specific sync rule: {id}");
            return await _application.ConnectedSystems.GetSyncRuleAsync(id);
        }
    }
}
