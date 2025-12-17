using System.Collections.Generic;
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
        public DbSet<ApiToken> ApiTokens { get; set; }

        public DbSet<ApplicationUser> ApplicationUsers { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<JobHistory> JobHistories { get; set; }
        public DbSet<ActivityType> ActivityTypes { get; set; }
        public DbSet<ApplicationSettings> ApplicationSettings { get; set; }

        public DbSet<TileCacheMetadata> TileCacheMetadata { get; set; }

        public DbSet<LocationImport> LocationImports { get; set; }

        public DbSet<HiddenArea> HiddenAreas { get; set; }

        // Trip-planning DbSets
        public DbSet<Trip> Trips { get; set; }
        public DbSet<Region> Regions { get; set; }
        public DbSet<Place> Places { get; set; }
        public DbSet<Segment> Segments { get; set; }
        public DbSet<Tag> Tags { get; set; }

        public DbSet<Area> Areas { get; set; }
        public DbSet<Group> Groups { get; set; }
        public DbSet<GroupMember> GroupMembers { get; set; }
        public DbSet<GroupInvitation> GroupInvitations { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.HasPostgresExtension("citext");

            // Configure the Location entity to use PostGIS Point type for Coordinates
            builder.Entity<Location>()
                .Property(l => l.Coordinates)
                .HasColumnType("geography(Point, 4326)"); // Define the column as PostGIS geography type (SRID 4326 is WGS84)

            // Add index on Location Coordinates for faster spatial queries (if frequently queried)
            builder.Entity<Location>()
                .HasIndex(l => l.Coordinates) // Create an index on the Coordinates column
                .HasMethod("GIST") // ?? this forces GiST for faster Gis spatial queries
                .HasDatabaseName("IX_Location_Coordinates");

            builder.Entity<ApiToken>()
                .Property(at => at.UserId)
                .IsRequired();

            builder.Entity<ApiToken>()
                .Property(at => at.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP"); // PostgreSQL will set the current timestamp by default

            // Define a unique constraint on Name and UserId
            builder.Entity<ApiToken>()
                .HasIndex(at => new { at.Name, at.UserId })
                .IsUnique()
                .HasDatabaseName("IX_ApiToken_Name_UserId"); // Optional: Custom index name

            builder.Entity<ApplicationUser>()
                .HasMany(u => u.ApiTokens)
                .WithOne(at => at.User)
                .HasForeignKey(at => at.UserId)
                .OnDelete(DeleteBehavior.Cascade); // Cascade delete when a User is deleted

            builder.Entity<ApplicationUser>()
                .HasIndex(u => u.UserName)
                .IsUnique();

            builder.Entity<ApplicationUser>()
                .Property(u => u.IsActive)
                .HasDefaultValue(true); // Default value for IsActive is true

            // ApplicationUser x Location setup
            builder.Entity<ApplicationUser>()
                .HasMany(u => u.Locations)
                .WithOne()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // IsTimelinePublic defaults to false!
            builder.Entity<ApplicationUser>()
                .Property(u => u.IsTimelinePublic)
                .HasDefaultValue(false); // Specify the default value here

            // Tile Cache Metadata
            // EF to use the RowVersion in order to handle race conditions in code
            builder.Entity<TileCacheMetadata>(b =>
            {
                b.Property(e => e.RowVersion)
                    .HasColumnName("xmin") // hidden pg column
                    .IsRowVersion() // EF’s “use for concurrency” flag
                    .ValueGeneratedOnAddOrUpdate(); // reload it after every update
            });

            builder.Entity<TileCacheMetadata>()
                .Property(t => t.LastAccessed)
                .IsRequired()
                .HasDefaultValueSql(
                    "CURRENT_TIMESTAMP"); // Default value for LastAccessed (can be overridden when accessed)

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
                    v => v.Value, // Convert to string when saving to DB
                    v => new ImportStatus(v) // Convert back from string when reading from DB
                );

            // For CreatedAt, set a default value of CURRENT_TIMESTAMP on creation
            builder.Entity<LocationImport>()
                .Property(li => li.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // For UpdatedAt, we need to update this on every update to the record
            builder.Entity<LocationImport>()
                .Property(li => li.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            builder.Entity<HiddenArea>()
                .HasOne(h => h.User)
                .WithMany(u => u.HiddenAreas) // <- You'll need this nav property on ApplicationUser
                .HasForeignKey(h => h.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Trip planning setup
            // Trip ↔ ApplicationUser (cascade on delete)
            builder.Entity<Trip>()
                .HasOne(t => t.User)
                .WithMany(u => u.Trips)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<Trip>()
                .Property(t => t.UpdatedAt)
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Trip ↔ Region (cascade on delete)
            builder.Entity<Trip>()
                .HasMany(t => t.Regions)
                .WithOne(r => r.Trip)
                .HasForeignKey(r => r.TripId)
                .OnDelete(DeleteBehavior.Cascade);

            // Trip ↔ Segment (cascade on delete)
            builder.Entity<Trip>()
                .HasMany(t => t.Segments)
                .WithOne(s => s.Trip)
                .HasForeignKey(s => s.TripId)
                .OnDelete(DeleteBehavior.Cascade);

            // Region ↔ Place (cascade on delete)
            builder.Entity<Region>()
                .HasMany(r => r.Places)
                .WithOne(p => p.Region)
                .HasForeignKey(p => p.RegionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Ensure geometry columns use PostGIS types
            builder.Entity<Region>()
                .Property(r => r.Center)
                .HasColumnType("geography(Point,4326)");

            builder.Entity<Place>()
                .Property(p => p.Location)
                .HasColumnType("geography(Point,4326)");

            builder.Entity<Segment>()
                .Property(s => s.RouteGeometry)
                .HasColumnType("geography(LineString,4326)");

            // Segment → Place (origin)
            builder.Entity<Segment>()
                .HasOne(s => s.FromPlace)
                .WithMany() // no back-reference needed
                .HasForeignKey(s => s.FromPlaceId)
                .OnDelete(DeleteBehavior.Cascade);

            // Segment → Place (destination)
            builder.Entity<Segment>()
                .HasOne(s => s.ToPlace)
                .WithMany()
                .HasForeignKey(s => s.ToPlaceId)
                .OnDelete(DeleteBehavior.Cascade);

            // Ensure when a Region is deleted, all its Areas go with it
            builder.Entity<Area>()
                .HasOne(a => a.Region)
                .WithMany(r => r.Areas)
                .HasForeignKey(a => a.RegionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Groups
            builder.Entity<Group>()
                .HasIndex(g => new { g.OwnerUserId, g.Name })
                .IsUnique();

            builder.Entity<Group>()
                .Property(g => g.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            builder.Entity<Group>()
                .Property(g => g.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            builder.Entity<Group>()
                .HasOne(g => g.Owner)
                .WithMany(u => u.GroupsOwned)
                .HasForeignKey(g => g.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<Group>()
                .Property(g => g.OrgPeerVisibilityEnabled)
                .HasDefaultValue(false);

            // GroupMember
            builder.Entity<GroupMember>()
                .HasIndex(m => new { m.GroupId, m.UserId })
                .IsUnique();

            // Composite index for common query pattern: WHERE GroupId = x AND Status = y
            builder.Entity<GroupMember>()
                .HasIndex(m => new { m.GroupId, m.Status })
                .HasDatabaseName("IX_GroupMember_GroupId_Status");

            builder.Entity<GroupMember>()
                .Property(m => m.JoinedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            builder.Entity<GroupMember>()
                .HasOne(m => m.Group)
                .WithMany(g => g.Members)
                .HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<GroupMember>()
                .HasOne(m => m.User)
                .WithMany(u => u.GroupMemberships)
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<GroupMember>()
                .Property(m => m.OrgPeerVisibilityAccessDisabled)
                .HasDefaultValue(false);

            // GroupInvitation
            builder.Entity<GroupInvitation>()
                .HasIndex(i => i.Token)
                .IsUnique();

            builder.Entity<GroupInvitation>()
                .Property(i => i.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            builder.Entity<GroupInvitation>()
                .HasOne(i => i.Group)
                .WithMany(g => g.Invitations)
                .HasForeignKey(i => i.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<GroupInvitation>()
                .HasOne(i => i.Inviter)
                .WithMany(u => u.GroupInvitationsSent)
                .HasForeignKey(i => i.InviterUserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<GroupInvitation>()
                .HasOne(i => i.Invitee)
                .WithMany(u => u.GroupInvitationsReceived)
                .HasForeignKey(i => i.InviteeUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Filtered unique index to prevent duplicate pending invitations for the same group and user
            builder.Entity<GroupInvitation>()
                .HasIndex(i => new { i.GroupId, i.InviteeUserId })
                .HasFilter("\"Status\" = 'Pending' AND \"InviteeUserId\" IS NOT NULL")
                .IsUnique()
                .HasDatabaseName("IX_GroupInvitation_GroupId_InviteeUserId_Pending");

            builder.Entity<Tag>(b =>
            {
                b.Property(t => t.Name)
                    .HasMaxLength(64)
                    .HasColumnType("citext");
                b.Property(t => t.Slug)
                    .HasMaxLength(200)
                    .IsRequired();
                b.HasIndex(t => t.Name).IsUnique();
                b.HasIndex(t => t.Slug).IsUnique();
            });

            builder.Entity<Trip>()
                .HasMany(t => t.Tags)
                .WithMany(tg => tg.Trips)
                .UsingEntity<Dictionary<string, object>>(
                    "TripTags",
                    j => j.HasOne<Tag>()
                          .WithMany()
                          .HasForeignKey("TagId")
                          .OnDelete(DeleteBehavior.Cascade),
                    j => j.HasOne<Trip>()
                          .WithMany()
                          .HasForeignKey("TripId")
                          .OnDelete(DeleteBehavior.Cascade),
                    j =>
                    {
                        j.HasKey("TripId", "TagId");
                        j.ToTable("TripTags");
                        j.HasIndex("TagId");
                        j.HasIndex("TripId");
                    });
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
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

            // Trip.UpdatedAt handling
            var tripIdsToStamp = new HashSet<Guid>();

            tripIdsToStamp.UnionWith(
                ChangeTracker.Entries<Trip>()
                    .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
                    .Select(e => e.Entity.Id)
            );

            tripIdsToStamp.UnionWith(
                ChangeTracker.Entries<Region>()
                    .Where(e => e.State != EntityState.Unchanged)
                    .Select(e => e.State == EntityState.Deleted
                        ? e.OriginalValues.GetValue<Guid>(nameof(Region.TripId))
                        : e.Entity.TripId)
            );

            tripIdsToStamp.UnionWith(
                ChangeTracker.Entries<Place>()
                    .Where(e => e.State != EntityState.Unchanged)
                    .Select(e => e.State == EntityState.Deleted
                        ? e.OriginalValues.GetValue<Guid>(nameof(Place.RegionId))
                        : e.Entity.RegionId)
                    .SelectMany(regionId => Regions
                        .Where(r => r.Id == regionId)
                        .Select(r => r.TripId))
            );

            tripIdsToStamp.UnionWith(
                ChangeTracker.Entries<Segment>()
                    .Where(e => e.State != EntityState.Unchanged)
                    .Select(e => e.State == EntityState.Deleted
                        ? e.OriginalValues.GetValue<Guid>(nameof(Segment.TripId))
                        : e.Entity.TripId)
            );

            // added: include Areas in trip UpdatedAt stamping
            tripIdsToStamp.UnionWith(
                ChangeTracker.Entries<Area>() // track added/modified/deleted Areas
                    .Where(e => e.State != EntityState.Unchanged)
                    .Select(e => e.State == EntityState.Deleted
                        // if deleted, get the old RegionId from the original values
                        ? e.OriginalValues.GetValue<Guid>(nameof(Area.RegionId))
                        // otherwise get the new RegionId
                        : e.Entity.RegionId)
                    // map each RegionId back to its TripId
                    .SelectMany(regionId => Regions
                        .Where(r => r.Id == regionId)
                        .Select(r => r.TripId))
            );

            foreach (var tripId in tripIdsToStamp)
            {
                var entry = ChangeTracker.Entries<Trip>()
                    .FirstOrDefault(e => e.Entity.Id == tripId);

                if (entry != null)
                {
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                    if (entry.State == EntityState.Unchanged)
                        entry.State = EntityState.Modified;
                }
                else
                {
                    // unchanged – update trip timestamp anyway
                    var trip = await Trips.FirstOrDefaultAsync(t => t.Id == tripId, cancellationToken);
                    if (trip != null)
                    {
                        trip.UpdatedAt = DateTime.UtcNow;
                        Entry(trip).State = EntityState.Modified;
                    }
                }
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
