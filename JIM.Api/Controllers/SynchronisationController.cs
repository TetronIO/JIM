using JIM.Api.Models;
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
        [ProducesResponseType(typeof(IEnumerable<ConnectedSystem>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetConnectedSystemsAsync()
        {
            _logger.LogTrace("Requested connected systems");
            var systems = await _application.ConnectedSystems.GetConnectedSystemsAsync();
            return Ok(systems);
        }

        [HttpGet("connected-systems/{connectedSystemId:int}")]
        [ProducesResponseType(typeof(ConnectedSystem), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetConnectedSystemAsync(int connectedSystemId)
        {
            _logger.LogTrace("Requested connected system: {Id}", connectedSystemId);
            var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
            if (system == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

            return Ok(system);
        }

        [HttpGet("connected-systems/{connectedSystemId:int}/object-types")]
        [ProducesResponseType(typeof(IEnumerable<ConnectedSystemObjectType>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetConnectedSystemObjectTypesAsync(int connectedSystemId)
        {
            _logger.LogTrace("Requested object types for connected system: {Id}", connectedSystemId);
            var objectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
            if (objectTypes == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

            return Ok(objectTypes);
        }

        [HttpGet("connected-systems/{connectedSystemId:int}/objects/{id:guid}")]
        [ProducesResponseType(typeof(ConnectedSystemObject), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
        {
            _logger.LogTrace("Requested object {ObjectId} for connected system: {SystemId}", id, connectedSystemId);
            var obj = await _application.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
            if (obj == null)
                return NotFound(ApiErrorResponse.NotFound($"Object with ID {id} not found in connected system {connectedSystemId}."));

            return Ok(obj);
        }

        /// <summary>
        /// Gets a preview of what will be affected by deleting a Connected System.
        /// Call this before DeleteConnectedSystemAsync to inform the user of the impact.
        /// </summary>
        /// <param name="connectedSystemId">The ID of the Connected System to preview deletion for.</param>
        /// <returns>A preview showing counts of affected objects and any warnings.</returns>
        [HttpGet("connected-systems/{connectedSystemId:int}/deletion-preview")]
        [ProducesResponseType(typeof(ConnectedSystemDeletionPreview), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetConnectedSystemDeletionPreviewAsync(int connectedSystemId)
        {
            _logger.LogInformation("Deletion preview requested for connected system: {Id}", connectedSystemId);

            var preview = await _application.ConnectedSystems.GetDeletionPreviewAsync(connectedSystemId);
            if (preview == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

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
        [HttpDelete("connected-systems/{connectedSystemId:int}")]
        [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status202Accepted)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> DeleteConnectedSystemAsync(int connectedSystemId)
        {
            _logger.LogInformation("Deletion requested for connected system: {Id}", connectedSystemId);

            // Get the current user from the JWT claims
            var initiatedBy = await GetCurrentUserAsync();
            if (initiatedBy == null)
            {
                _logger.LogWarning("Could not identify user from JWT claims for deletion request");
                return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
            }

            var result = await _application.ConnectedSystems.DeleteAsync(connectedSystemId, initiatedBy);

            if (!result.Success)
                return BadRequest(ApiErrorResponse.BadRequest(result.ErrorMessage ?? "Deletion failed."));

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
        private async Task<JIM.Models.Core.MetaverseObject?> GetCurrentUserAsync()
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
                JIM.Models.Core.Constants.BuiltInObjectTypes.Users,
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
        [ProducesResponseType(typeof(IEnumerable<SyncRule>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSyncRulesAsync()
        {
            _logger.LogTrace("Requested synchronisation rules");
            var rules = await _application.ConnectedSystems.GetSyncRulesAsync();
            return Ok(rules);
        }

        [HttpGet("sync-rules/{id:int}")]
        [ProducesResponseType(typeof(SyncRule), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetSyncRuleAsync(int id)
        {
            _logger.LogTrace("Requested sync rule: {Id}", id);
            var rule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
            if (rule == null)
                return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {id} not found."));

            return Ok(rule);
        }
    }
}
