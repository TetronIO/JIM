using JIM.Api.Models;
using JIM.Application;
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
        [ProducesResponseType(typeof(IEnumerable<RoleDto>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetRolesAsync()
        {
            _logger.LogTrace("Requested roles");
            var roles = await _application.Security.GetRolesAsync();
            var dtos = roles.Select(RoleDto.FromEntity);
            return Ok(dtos);
        }
    }
}
