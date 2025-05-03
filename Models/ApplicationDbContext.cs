using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Wayfarer.Models
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        private readonly IServiceProvider _serviceProvider;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options,
            IServiceProvider serviceProvider)
            : base(options)
        {
            _serviceProvider = serviceProvider;
        }

        public DbSet<Location> Locations { get; set; }
        public DbSet<Vehicle> Vehicles { get; set; }
        public DbSet<ApiToken> ApiTokens { get; set; }

        public DbSet<ApplicationUser> ApplicationUsers { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<JobHistory> JobHistories { get; set; }
        public DbSet<ActivityType> ActivityTypes { get; set; }
        public DbSet<ApplicationSettings> ApplicationSettings { get; set; }
        
        public DbSet<TileCacheMetadata> TileCacheMetadata { get; set; }
        
        public DbSet<LocationImport> LocationImports { get; set; }
        
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Configure the Location entity to use PostGIS Point type for Coordinates
            builder.Entity<Location>()
                .Property(l => l.Coordinates)
                .HasColumnType("geography(Point, 4326)");  // Define the column as PostGIS geography type (SRID 4326 is WGS84)

            // Set Cascade Delete for the optional relationship between Location and Vehicle
            builder.Entity<Location>()
                .HasOne(l => l.Vehicle)  // Specify the related entity
                .WithMany(v => v.Locations)  // Specify the navigation property on Vehicle
                .HasForeignKey(l => l.VehicleId)  // Specify the foreign key in the Location table
                .OnDelete(DeleteBehavior.Cascade);  // Cascade delete when a Vehicle is deleted

            // Add index on Location Coordinates for faster spatial queries (if frequently queried)
            builder.Entity<Location>()
                .HasIndex(l => l.Coordinates)  // Create an index on the Coordinates column
                .HasMethod("GIST") // 👈 this forces GiST for faster Gis spatial queries
                .HasDatabaseName("IX_Location_Coordinates");

            // Configure the Vehicle entity to use JSONB for Passengers field
            builder.Entity<Vehicle>()
                .Property(v => v.Passengers)
                .HasColumnType("jsonb"); // Define Passengers as JSONB type

            // Add a GIN index for the JSONB column (Passengers)
            builder.Entity<Vehicle>()
                .HasIndex(v => v.Passengers)
                .HasMethod("GIN")
                .HasDatabaseName("IX_Vehicle_Passengers_GIN");

            builder.Entity<Vehicle>()
                .Property(v => v.Cargo)
                .HasColumnType("jsonb"); // Define Cargo as JSONB type

            // Add a GIN index for the JSONB column (Cargo)
            builder.Entity<Vehicle>()
                .HasIndex(v => v.Cargo)
                .HasMethod("GIN")
                .HasDatabaseName("IX_Vehicle_Cargo_GIN");

            // Index on PlateNumber for Vehicle (for fast lookup by plate number)
            builder.Entity<Vehicle>()
                .HasIndex(v => v.PlateNumber)
                .HasDatabaseName("IX_Vehicle_PlateNumber");

            builder.Entity<ApiToken>()
                .Property(at => at.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");  // PostgreSQL will set the current timestamp by default

            // Define a unique constraint on Name and UserId
            builder.Entity<ApiToken>()
                .HasIndex(at => new { at.Name, at.UserId })
                .IsUnique()
                .HasDatabaseName("IX_ApiToken_Name_UserId"); // Optional: Custom index name

            builder.Entity<ApplicationUser>()
                .HasMany(u => u.ApiTokens)
                .WithOne(at => at.User)
                .HasForeignKey(at => at.UserId)
                .OnDelete(DeleteBehavior.Cascade);  // Cascade delete when a User is deleted

            builder.Entity<ApplicationUser>()
                .HasIndex(u => u.UserName)
                .IsUnique();

            builder.Entity<ApplicationUser>()
                .Property(u => u.IsActive)
                .HasDefaultValue(true);  // Default value for IsActive is true
            
            // ApplicationUser x Location setup
            builder.Entity<ApplicationUser>()
                .HasMany(u => u.Locations)
                .WithOne()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // IsTimelinePublic defaults to false!
            builder.Entity<ApplicationUser>()
                .Property(u => u.IsTimelinePublic)
                .HasDefaultValue(false);  // Specify the default value here
            
            // Tile Cache Metadata
            // EF to use the RowVersion in order to handle race conditions in code
            builder.Entity<TileCacheMetadata>(b =>
            {
                b.Property(e => e.RowVersion)
                    .HasColumnName("xmin")              // hidden pg column
                    .IsRowVersion()                     // EF’s “use for concurrency” flag
                    .ValueGeneratedOnAddOrUpdate();     // reload it after every update
            });
            
            builder.Entity<TileCacheMetadata>()
                .Property(t => t.LastAccessed)
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP"); // Default value for LastAccessed (can be overridden when accessed)

            builder.Entity<TileCacheMetadata>()
                .Property(t => t.Size)
                .IsRequired(); // Size should always be required

            builder.Entity<TileCacheMetadata>()
                .Property(t => t.TileFilePath)
                .IsRequired(false); // TileFilePath may not always be required (depending on storage model)

            // TileLocation as a spatial index (if needed)
            builder.Entity<TileCacheMetadata>()
                .HasIndex(t => t.TileLocation)
                .HasMethod("GIST"); // This creates a spatial index (if you're using PostGIS)
            
            // Application Settings
            builder.Entity<ApplicationSettings>()
                .Property(x => x.IsRegistrationOpen)
                .HasDefaultValue(false);
            
            // Location Imports
            builder.Entity<LocationImport>()
                .HasOne(li => li.User)
                .WithMany(u => u.LocationImports)
                .HasForeignKey(li => li.UserId)
                .OnDelete(DeleteBehavior.Cascade); 
            
            // Store status as strings, eg: Pending, InProgress, etc
            builder.Entity<LocationImport>()
                .Property(e => e.Status)
                .HasConversion(
                    v => v.Value,     // Convert to string when saving to DB
                    v => new ImportStatus(v)  // Convert back from string when reading from DB
                );
            
            // For CreatedAt, set a default value of CURRENT_TIMESTAMP on creation
            builder.Entity<LocationImport>()
                .Property(li => li.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // For UpdatedAt, we need to update this on every update to the record
            builder.Entity<LocationImport>()
                .Property(li => li.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
        }
        
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // Automatically set the UpdatedAt field before saving changes to the database
            foreach (var entry in ChangeTracker.Entries<LocationImport>())
            {
                if (entry.State == EntityState.Modified)
                {
                    entry.Property(x => x.UpdatedAt).CurrentValue = DateTime.UtcNow;
                }

                if (entry.State == EntityState.Added)
                {
                    entry.Property(x => x.CreatedAt).CurrentValue = DateTime.UtcNow;
                    entry.Property(x => x.UpdatedAt).CurrentValue = DateTime.UtcNow;
                }
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}