using Microsoft.Extensions.Caching.Memory;
using Wayfarer.Models;

namespace Wayfarer.Parsers
{
    public interface IApplicationSettingsService
    {
        ApplicationSettings GetSettings();
        void RefreshSettings();
    }

    public class ApplicationSettingsService : IApplicationSettingsService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly IMemoryCache _cache;
        private const string SettingsCacheKey = "ApplicationSettings";

        public ApplicationSettingsService(ApplicationDbContext dbContext, IMemoryCache cache)
        {
            _dbContext = dbContext;
            _cache = cache;
        }

        public ApplicationSettings GetSettings()
        {
            // Attempt to get the settings from the memory cache
            if (!_cache.TryGetValue(SettingsCacheKey, out ApplicationSettings settings))
            {
                // If not in cache, load from the database and cache it
                settings = LoadSettingsFromDb();
                // Store settings in memory cache with a sliding expiration of 10 minutes
                _cache.Set(SettingsCacheKey, settings, TimeSpan.FromMinutes(10));
            }

            return settings;
        }

        public void RefreshSettings()
        {
            ApplicationSettings settings = LoadSettingsFromDb();
            // Update the settings in memory cache
            _cache.Set(SettingsCacheKey, settings, TimeSpan.FromMinutes(10));
        }

        private ApplicationSettings LoadSettingsFromDb()
        {
            // Load settings from the database
            var settings = _dbContext.ApplicationSettings.Find(1);
            if (settings == null)
            {
                settings = new ApplicationSettings();
            }
            return settings == null ? throw new InvalidOperationException("Application settings not found in the database.") : settings;
        }
        
    }

}
