using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Models.DTOs.Responses.Authentication;
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

        [AllowAnonymous]
        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string? email, string? view)
        {
            var loginResult = _loginService.ValidateLogin(email ?? string.Empty);

            if (!loginResult.Success)
            {
                ViewData["ErrorMessage"] = GetLoginErrorMessage(loginResult.FailureCode);
                return View("Index");
            }

            var user = loginResult.User!;
            var trimmedEmail = user.Email;
            var displayName = string.IsNullOrWhiteSpace(user.Name) ? trimmedEmail : user.Name.Trim();
            var roleString = _loginService.GetRoleString(user);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, displayName),
                new Claim(ClaimTypes.Role, roleString),
                new Claim(ClaimTypes.NameIdentifier, trimmedEmail)
            };

            Response.Cookies.Append(EmailCookieName, trimmedEmail, new CookieOptions
            {
                Path = "/",
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                HttpOnly = true,
            });

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return RedirectToRoute("ScheduleView", new { view = string.IsNullOrWhiteSpace(view) ? "week" : view });
        }

        [HttpGet]
        public async Task<IActionResult> Logout()
        {
            Response.Cookies.Delete(EmailCookieName, new CookieOptions { Path = "/" });
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index");
        }

        private static string GetLoginErrorMessage(LoginFailureCode failureCode)
        {
            return failureCode switch
            {
                LoginFailureCode.MissingEmail => "Prašome ivesti elektroninio pašto adresą.",
                LoginFailureCode.UserNotFoundOrInactive => "Vartotojas su tokiu el. pašto adresu nerastas arba neaktyvus.",
                _ => "Nepavyko prisijungti."
            };
        }
    }
}

