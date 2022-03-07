using JIM.Application;
using JIM.Models.Security;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class SecurityController : ControllerBase
    {
        private readonly ILogger<SecurityController> _logger;
        private readonly JimApplication _application;

        public SecurityController(ILogger<SecurityController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("/security/roles")]
        public async Task<IEnumerable<Role>> GetRolesAsync()
        {
            _logger.LogTrace($"Someone requested the roles");
            return await _application.Security.GetRolesAsync();
        }
    }
}
