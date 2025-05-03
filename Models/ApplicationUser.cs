using Microsoft.AspNetCore.Identity;

namespace Wayfarer.Models
{
    // Add profile data for application users by adding properties to the ApplicationUser class
    public class ApplicationUser : IdentityUser
    {
        public string DisplayName { get; set; }

        // The user is not locked by Admins/Managers
        public bool IsActive { get; set; }
        // The user attributes are protected and cannot be changed if IsProtected = true
        public bool IsProtected { get; set; }

        // Is user's timeline public? defaults to false
        public bool IsTimelinePublic { get; set; }

        // Safety mechanism to provide a threshold up to when
        // the timeline will be shared in relation to current date and time
        // Implementation specifics:
        // If the user chooses "Up to 2 hours before current time", the PublicTimelineEndDate would be calculated as DateTime.Now - TimeSpan.FromHours(2)
        // If the user chooses "Up to 1 day before today", the PublicTimelineEndDate would be calculated as DateTime.Today - TimeSpan.FromDays(1)
        public string? PublicTimelineTimeThreshold { get; set; } = null;

        // A user has an optional ApiToken 
        public ICollection<ApiToken> ApiTokens { get; set; } = new List<ApiToken>();
        
        // Location navigation property
        public virtual ICollection<Location> Locations { get; set; } = new List<Location>();
        
        // A user has optional LocationImports
        public virtual ICollection<LocationImport> LocationImports { get; set; } = new List<LocationImport>();
    }

}
