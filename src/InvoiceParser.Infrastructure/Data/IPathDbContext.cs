using InvoiceParser.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceParser.Infrastructure.Data;

public class IPathDbContext : DbContext
{
    public IPathDbContext(DbContextOptions<IPathDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Carrier> Carriers => Set<Carrier>();
}
