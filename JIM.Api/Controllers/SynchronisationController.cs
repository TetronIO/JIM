using JIM.Application;
using JIM.Models.Logic;
using JIM.Models.Staging;
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

        [HttpGet("/synchronisation/connected_systems")]
        public async Task<IEnumerable<ConnectedSystem>> GetConnectedSystemsAsync()
        {
            _logger.LogTrace($"Someone requested the connected systems");
            return await _application.ConnectedSystems.GetConnectedSystemsAsync();
        }

        [HttpGet("/synchronisation/connected_systems/{csid}")]
        public async Task<ConnectedSystem?> GetConnectedSystemAsync(int csid)
        {
            _logger.LogTrace($"Someone requested a connected system: {csid}");
            return await _application.ConnectedSystems.GetConnectedSystemAsync(csid);
        }

        [HttpGet("/synchronisation/connected_systems/{csid}/object_types")]
        public async Task<IEnumerable<ConnectedSystemObjectType>?> GetConnectedSystemObjectTypesAsync(int csid)
        {
            _logger.LogTrace($"Someone requested object types for connected system: {csid}");
            return await _application.ConnectedSystems.GetObjectTypesAsync(csid);
        }

        [HttpGet("/synchronisation/connected_systems/{csid}/objects/{id}")]
        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int csid, Guid id)
        {
            _logger.LogTrace($"Someone requested an object ({id}) connected system: {csid}");
            return await _application.ConnectedSystems.GetConnectedSystemObjectAsync(csid, id);
        }

        [HttpGet("/synchronisation/sync_rules")]
        public async Task<IEnumerable<SyncRule>?> GetSyncRulesAsync()
        {
            _logger.LogTrace("Someone requested the synchronisation rules");
            return await _application.ConnectedSystems.GetSyncRulesAsync();
        }

        [HttpGet("/synchronisation/sync_rules/{id}")]
        public async Task<SyncRule?> GetSyncRuleAsync(int id)
        {
            _logger.LogTrace($"Someone requested a specific sync rule: {id}");
            return await _application.ConnectedSystems.GetSyncRuleAsync(id);
        }
    }
}
