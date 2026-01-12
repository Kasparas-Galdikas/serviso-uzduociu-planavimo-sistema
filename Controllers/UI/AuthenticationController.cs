using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace ServiceTaskPlanningSystem.Controllers.UI
{
    public class AuthenticationController : Controller
    {
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

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(trimmedEmail) ? "guest" : trimmedEmail),
                new Claim(ClaimTypes.Role, normalizedRole)
            };

            if (!string.IsNullOrWhiteSpace(trimmedEmail))
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, trimmedEmail));
                Response.Cookies.Append(EmailCookieName, trimmedEmail, new CookieOptions
                {
                    Path = "/",
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    HttpOnly = false,
                });
            }

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity)).GetAwaiter().GetResult();

            return RedirectToRoute("ScheduleView", new { view = string.IsNullOrWhiteSpace(view) ? "week" : view });
        }

        [HttpGet]
        public IActionResult Logout()
        {
            Response.Cookies.Delete(EmailCookieName, new CookieOptions { Path = "/" });
            HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).GetAwaiter().GetResult();
            return RedirectToAction("Index");
        }
    }
}
