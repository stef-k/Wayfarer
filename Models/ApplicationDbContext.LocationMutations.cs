using Microsoft.EntityFrameworkCore;

namespace Wayfarer.Models;

/// <summary>Protected Location operation storage is independent of target-row lifetime.</summary>
public partial class ApplicationDbContext
{
    /// <summary>Finite evidence; required account ownership supplies erasure cascade without a Location foreign key.</summary>
    public DbSet<LocationMutationOperation> LocationMutationOperations { get; set; }
}
