using JIM.Application;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class SynchronisationController : ControllerBase
    {
        private readonly ILogger<SynchronisationController> _logger;
        private readonly JimApplication _application;

        public SynchronisationController(ILogger<SynchronisationController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("/synchronisation/connected-systems")]
        public async Task<IEnumerable<ConnectedSystem>> GetConnectedSystemsAsync()
        {
            _logger.LogTrace($"Someone requested the connected systems");
            return await _application.ConnectedSystems.GetConnectedSystemsAsync();
        }

        [HttpGet("/synchronisation/connected-systems/{csid}")]
        public async Task<ConnectedSystem?> GetConnectedSystemAsync(int csid)
        {
            _logger.LogTrace($"Someone requested a connected system: {csid}");
            return await _application.ConnectedSystems.GetConnectedSystemAsync(csid);
        }

        [HttpGet("/synchronisation/connected-systems/{connectedSystemId}/object-types")]
        public async Task<IEnumerable<ConnectedSystemObjectType>?> GetConnectedSystemObjectTypesAsync(int connectedSystemId)
        {
            _logger.LogTrace($"Someone requested object types for connected system: {connectedSystemId}");
            return await _application.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
        }

        [HttpGet("/synchronisation/connected-systems/{connectedSystemId}/objects/{id}")]
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
        [HttpGet("/synchronisation/connected-systems/{connectedSystemId}/deletion-preview")]
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
        [HttpDelete("/synchronisation/connected-systems/{connectedSystemId}")]
        public async Task<ActionResult<ConnectedSystemDeletionResult>> DeleteConnectedSystemAsync(int connectedSystemId)
        {
            _logger.LogInformation("Deletion requested for connected system: {Id}", connectedSystemId);

            // TODO: Get the current user from authentication context
            // For now, we pass null which means the action is not attributed to a specific user
            var result = await _application.ConnectedSystems.DeleteAsync(connectedSystemId, null!);

            if (!result.Success)
                return BadRequest(result);

            return Ok(result);
        }

        [HttpGet("/synchronisation/sync-rules")]
        public async Task<IEnumerable<SyncRule>?> GetSyncRulesAsync()
        {
            _logger.LogTrace("Someone requested the synchronisation rules");
            return await _application.ConnectedSystems.GetSyncRulesAsync();
        }

        [HttpGet("/synchronisation/sync-rules/{id}")]
        public async Task<SyncRule?> GetSyncRuleAsync(int id)
        {
            _logger.LogTrace($"Someone requested a specific sync rule: {id}");
            return await _application.ConnectedSystems.GetSyncRuleAsync(id);
        }
    }
}
