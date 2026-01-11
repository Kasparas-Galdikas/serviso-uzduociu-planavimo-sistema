using Microsoft.AspNetCore.Mvc;

namespace ServiceTaskPlanningSystem.Controllers.UI
{
    public class AuthenticationController : Controller
    {
        private const string RoleCookieName = "stsps_role";
        private const string EmailCookieName = "stsps_email";

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Login(string? email, string? password, string? role, string? view)
        {
            var normalizedRole = (role ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedRole != "technician" && normalizedRole != "manager")
            {
                normalizedRole = "manager";
            }

            var trimmedEmail = (email ?? string.Empty).Trim();

            Response.Cookies.Append(RoleCookieName, normalizedRole, new CookieOptions
            {
                Path = "/",
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                HttpOnly = false,
            });

            if (!string.IsNullOrWhiteSpace(trimmedEmail))
            {
                Response.Cookies.Append(EmailCookieName, trimmedEmail, new CookieOptions
                {
                    Path = "/",
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    HttpOnly = false,
                });
            }

            return RedirectToRoute("ScheduleView", new { view = string.IsNullOrWhiteSpace(view) ? "week" : view, role = normalizedRole });
        }

        [HttpGet]
        public IActionResult Logout()
        {
            Response.Cookies.Delete(RoleCookieName, new CookieOptions { Path = "/" });
            Response.Cookies.Delete(EmailCookieName, new CookieOptions { Path = "/" });
            return RedirectToAction("Index");
        }
    }
}
