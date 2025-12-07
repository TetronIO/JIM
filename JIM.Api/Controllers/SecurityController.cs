using JIM.Application;
using JIM.Models.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SecurityController : ControllerBase
    {
        private readonly ILogger<SecurityController> _logger;
        private readonly JimApplication _application;

        public SecurityController(ILogger<SecurityController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("roles")]
        public async Task<IEnumerable<Role>> GetRolesAsync()
        {
            _logger.LogTrace($"Someone requested the roles");
            return await _application.Security.GetRolesAsync();
        }
    }
}
