using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LeontecSyncLogSystem.Data
{
    /// <summary>
    /// Design-time factory used by the EF Core tools (<c>dotnet ef migrations add</c> /
    /// <c>database update</c>). Lets the tooling build an <see cref="AppDbContext"/> without booting
    /// the WinForms host. Uses an explicit MySQL server version so creating a migration does NOT
    /// require a live database connection (only <c>database update</c> connects at runtime).
    /// </summary>
    public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var connectionString =
                Environment.GetEnvironmentVariable("LEONTEC_DB")
                ?? "Server=localhost;Port=3306;Database=leontec_sync;User=root;Password=;";

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)))
                .UseSnakeCaseNamingConvention()
                .Options;

            return new AppDbContext(options);
        }
    }
}
