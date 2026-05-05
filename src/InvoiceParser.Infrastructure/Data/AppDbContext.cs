using InvoiceParser.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceParser.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Charge> Charges => Set<Charge>();
    public DbSet<Usage> Usages => Set<Usage>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<VendorParsingRule> VendorParsingRules => Set<VendorParsingRule>();
    public DbSet<InvoiceFeedback> InvoiceFeedbacks => Set<InvoiceFeedback>();
    public DbSet<LineFeedback> LineFeedbacks => Set<LineFeedback>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(e =>
        {
            e.HasMany(i => i.Charges)
             .WithOne(c => c.Invoice)
             .HasForeignKey(c => c.InvoiceId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
