using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Snacks.Data;

/// <summary>
///     EF Core design-time context factory for migrations.
///     Used by the EF migration tools to create a DbContext without dependency injection.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SnacksDbContext>
{
    /// <summary>
    ///     Creates a context for use during migration generation.
    ///     Uses a local SQLite database for schema generation.
    /// </summary>
    /// <param name="args">Command-line arguments (unused).</param>
    /// <returns>A configured <see cref="SnacksDbContext"/> instance.</returns>
    public SnacksDbContext CreateDbContext(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var optionsBuilder = new DbContextOptionsBuilder<SnacksDbContext>();
        optionsBuilder.UseSqlite("Data Source=snacks_design.db");
        return new SnacksDbContext(optionsBuilder.Options);
    }
}
