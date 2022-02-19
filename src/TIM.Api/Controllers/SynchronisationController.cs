using Microsoft.AspNetCore.Mvc;
using TIM.Application;
using TIM.Models.Core;
using TIM.Models.Logic;
using TIM.Models.Staging;
using TIM.Models.Transactional;

namespace TIM.Api.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class SynchronisationController : ControllerBase
    {
        private readonly ILogger<SynchronisationController> _logger;
        private readonly TimApplication _application;

        public SynchronisationController(ILogger<SynchronisationController> logger, TimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("/synchronisation/connected_systems")]
        public IEnumerable<ConnectedSystem> GetConnectedSystems()
        {
            _logger.LogTrace($"Someone requested the connected systems");
            return _application.ConnectedSystems.GetConnectedSystems();
        }

        [HttpGet("/synchronisation/connected_systems/{csid}")]
        public ConnectedSystem? GetConnectedSystem(Guid csid)
        {
            _logger.LogTrace($"Someone requested a connected system: {csid}");
            return _application.ConnectedSystems.GetConnectedSystem(csid);
        }

        [HttpGet("/synchronisation/connected_systems/{csid}/run_history")]
        public IEnumerable<SyncRun>? GetConnectedSystemRunHistories(Guid csid)
        {
            _logger.LogTrace($"Someone requested synchronisation runs for system: {csid}");
            return _application.ConnectedSystems.GetSynchronisationRuns(csid);
        }

        [HttpGet("/synchronisation/connected_systems/{csid}/attributes")]
        public IEnumerable<ConnectedSystemAttribute>? GetConnectedSystemAttributes(Guid csid)
        {
            _logger.LogTrace($"Someone requested attributes for connected system: {csid}");
            return _application.ConnectedSystems.GetAttributes(csid);
        }

        [HttpGet("/synchronisation/connected_systems/{csid}/object_types")]
        public IEnumerable<ConnectedSystemObjectType>? GetConnectedSystemObjectTypes(Guid csid)
        {
            _logger.LogTrace($"Someone requested object types for connected system: {csid}");
            return _application.ConnectedSystems.GetObjectTypes(csid);
        }

        [HttpGet("/synchronisation/connected_systems/{csid}/objects/{id}")]
        public ConnectedSystemObject? GetConnectedSystemObject(Guid csid, Guid id)
        {
            _logger.LogTrace($"Someone requested an object ({id}) connected system: {csid}");
            return _application.ConnectedSystems.GetConnectedSystemObject(csid, id);
        }

        [HttpGet("/synchronisation/sync_rules")]
        public IEnumerable<SyncRule>? GetSyncRules()
        {
            _logger.LogTrace("Someone requested the synchronisation rules");
            return _application.ConnectedSystems.GetSyncRules();
        }

        [HttpGet("/synchronisation/sync_rules/{id}")]
        public SyncRule? GetSyncRule(Guid id)
        {
            _logger.LogTrace($"Someone requested a specific sync rule: {id}");
            return _application.ConnectedSystems.GetSyncRule(id);
        }
    }
}
