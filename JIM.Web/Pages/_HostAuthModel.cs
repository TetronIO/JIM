using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JIM.Web.Pages
{
    public class _HostAuthModel : PageModel
    {
        public async Task OnGetSignout()
        {
            await HttpContext.SignOutAsync("Cookies");
            await HttpContext.SignOutAsync("oidc", AuthProps());
        }

        private AuthenticationProperties AuthProps() => new()
        {
            RedirectUri = Url.Content("~/")
        };
    }
}
