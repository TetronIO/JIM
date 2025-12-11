using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JIM.Web.Pages
{
    public class _HostAuthModel : PageModel
    {
        public async Task OnGetSignout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, AuthProps());
        }

        private AuthenticationProperties AuthProps() => new()
        {
            RedirectUri = Url.Content("~/")
        };
    }
}
