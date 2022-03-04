using JIM.Application;
using JIM.Models.Security;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class SecurityController : ControllerBase
    {
        private readonly ILogger<SynchronisationController> _logger;
        private readonly JimApplication _application;

        public SecurityController(ILogger<SynchronisationController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("/security/roles")]
        public IEnumerable<Role> GetRoles()
        {
            _logger.LogTrace($"Someone requested the roles");
            return _application.Security.GetRoles();
        }
    }
}
