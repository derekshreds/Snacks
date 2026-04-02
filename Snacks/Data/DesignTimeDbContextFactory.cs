using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Snacks.Data
{
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SnacksDbContext>
    {
        public SnacksDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<SnacksDbContext>();
            optionsBuilder.UseSqlite("Data Source=snacks_design.db");
            return new SnacksDbContext(optionsBuilder.Options);
        }
    }
}
