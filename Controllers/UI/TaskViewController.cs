using Microsoft.AspNetCore.Mvc;
using ServiceTaskPlanningSystem.Models.Tasks;

namespace ServiceTaskPlanningSystem.Controllers.UI;

[Route("Task")]
public class TaskViewController : Controller
{
    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View(new TaskCreateViewModel());
    }

    [HttpPost("Create")]
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

