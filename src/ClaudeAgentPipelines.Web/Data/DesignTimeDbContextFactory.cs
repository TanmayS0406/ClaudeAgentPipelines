using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClaudeAgentPipelines.Data;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the context without starting the app or reaching a database. The
/// connection string here is only used by EF tooling; the app reads ConnectionStrings:Default.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost\\SQLEXPRESS;Database=ClaudeAgentPipelines;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options);
}
