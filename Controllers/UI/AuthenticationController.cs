using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Services.Authentication;

namespace ServiceTaskPlanningSystem.Controllers.UI
{
    public class AuthenticationController : Controller
    {
        private const string EmailCookieName = "stsps_email";
        private readonly ILoginService _loginService;

        public AuthenticationController(ILoginService loginService)
        {
            _loginService = loginService;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Login(string? email, string? view)
        {
            var loginResult = _loginService.ValidateLogin(email ?? string.Empty);

            if (!loginResult.Success)
            {
                ViewData["ErrorMessage"] = loginResult.ErrorMessage;
                return View("Index");
            }

            var user = loginResult.User!;
            var trimmedEmail = user.Email;
            var roleString = _loginService.GetRoleString(user);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, trimmedEmail),
                new Claim(ClaimTypes.Role, roleString),
                new Claim(ClaimTypes.NameIdentifier, trimmedEmail)
            };

            Response.Cookies.Append(EmailCookieName, trimmedEmail, new CookieOptions
            {
                Path = "/",
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                HttpOnly = false,
            });

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
