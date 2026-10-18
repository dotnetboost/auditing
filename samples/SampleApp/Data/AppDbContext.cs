using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SampleApp.Domain;

namespace SampleApp.Data;

/// <summary>
/// The application's own context. The audit tables live in it, which is what lets a change and its
/// trail commit in one transaction.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    public DbSet<StockCheck> StockChecks => Set<StockCheck>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Name).HasMaxLength(200).IsRequired();
            product.Property(p => p.Category).HasMaxLength(100).IsRequired();
            product.Property(p => p.Price).HasPrecision(18, 2);
            product.Property(p => p.SupplierCost).HasPrecision(18, 2);
        });

        // The whole opt-in: two tables added to this model. Nothing changes about Product or
        // StockCheck, and auditing something new later costs no migration.
        modelBuilder.ApplyAuditing();

        base.OnModelCreating(modelBuilder);
    }
}
