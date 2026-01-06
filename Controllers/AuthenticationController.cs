using Microsoft.AspNetCore.Mvc;

namespace ServiceTaskPlanningSystem.Controllers
{
    public class AuthenticationController : Controller
    {
        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }
    }
}
