using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Controllers;

[Route("Error")]
public class ErrorController : Controller
{
    [Route("404")]
    public IActionResult PageNotFound()
    {
        Response.StatusCode = 404;
        return View("~/Views/Shared/404.cshtml");
    }
}