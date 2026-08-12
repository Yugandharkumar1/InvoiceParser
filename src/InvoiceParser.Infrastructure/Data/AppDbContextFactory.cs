using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace InvoiceParser.Infrastructure.Data;

/// <summary>
/// Used by 'dotnet ef migrations' at design time so the tool can create an
/// AppDbContext without starting the full web host.
/// Connection string is read from appsettings.json / appsettings.Development.json
/// in the Web project (the startup project), or falls back to the local SQL Express
/// instance used during development.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Walk up from the Infrastructure project to find the Web project's config.
        var basePath = Path.Combine(Directory.GetCurrentDirectory(),
            "..", "InvoiceParser.Web");

        if (!Directory.Exists(basePath))
            basePath = Directory.GetCurrentDirectory();

        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? "Server=(localdb)\\mssqllocaldb;Database=InvoiceParserDb;Trusted_Connection=True;TrustServerCertificate=True;";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sql => sql.CommandTimeout(120));

        return new AppDbContext(optionsBuilder.Options);
    }
}
