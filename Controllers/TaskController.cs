using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Models.Tasks;

namespace ServiceTaskPlanningSystem.Controllers;

public class TaskController : Controller
{
    [HttpGet]
    public IActionResult Create()
    {
        return View(new TaskCreateViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Create(TaskCreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        ViewData["Created"] = true;
        return View(model);
    }
}

