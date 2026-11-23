using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Wayfarer.Areas.User.Controllers;
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

        /// <summary>Redirects legacy documentation bookmarks to the canonical GitHub Pages homepage.</summary>
        [AcceptVerbs("GET", "HEAD", Route = "/docs")]
        public IActionResult Documentation()
        {
            return RedirectPermanent("https://stef-k.github.io/Wayfarer/");
        }

        /// <summary>Renders the localized privacy interface and the English policy.</summary>
        public IActionResult Privacy()
        {
            return View();
        }
        
        public IActionResult RegistrationClosed()
        {
            return View();
        }

        /// <summary>Renders action-specific download feedback when export generation fails.</summary>
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            SetPageTitle($"Error ({HttpContext.Response.StatusCode})");
            var failure = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            var action = failure?.Endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (action?.ControllerTypeInfo.AsType() == typeof(LocationExportController))
                ViewData["TransferErrorKey"] = "Transfers.ExportFailed";
            _logger.LogError(failure?.Error, "Unhandled exception occurred");
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
