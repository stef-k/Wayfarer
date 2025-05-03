using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Models;

namespace Wayfarer.Controllers
{
    public class HomeController : BaseController
    {
        public HomeController(ApplicationDbContext dbContext, ILogger<UsersController> logger) : base(logger, dbContext)
        {

        }

        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            SetPageTitle("Privacy Policy");
            return View();
        }
        
        public IActionResult RegistrationClosed()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            SetPageTitle($"Error ({HttpContext.Response.StatusCode})");
            Exception? exceptionDetails = HttpContext.Features.Get<IExceptionHandlerPathFeature>()?.Error;
            _logger.LogError(exceptionDetails, "Unhandled exception occurred");
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
