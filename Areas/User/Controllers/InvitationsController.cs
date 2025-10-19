using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    /// <summary>
    /// Displays pending invitations for the current user and hosts the client script
    /// that accepts/declines invites via the API.
    /// </summary>
    public class InvitationsController : BaseController
    {
        public InvitationsController(ILogger<BaseController> logger, ApplicationDbContext dbContext)
            : base(logger, dbContext)
        {
        }

        /// <summary>
        /// Lists the user's pending invitations.
        /// GET /User/Invitations
        /// </summary>
        public IActionResult Index()
        {
            SetPageTitle("Invitations");
            return View();
        }
    }
}
