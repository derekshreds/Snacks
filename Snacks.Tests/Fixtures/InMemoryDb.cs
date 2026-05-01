using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snacks.Data;

namespace Snacks.Tests.Fixtures;

/// <summary>
///     Test fixture that hosts a fresh SQLite in-memory database per instance and exposes
///     it as a real <see cref="IDbContextFactory{TContext}"/> so the production
///     <see cref="MediaFileRepository"/> can be exercised against it without mocking.
///
///     Each fixture owns one open <see cref="SqliteConnection"/> — closing the connection
///     drops the database, so the fixture is <see cref="IDisposable"/>.
/// </summary>
internal sealed class InMemoryDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public IDbContextFactory<SnacksDbContext> Factory { get; }

    public InMemoryDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var opts = new DbContextOptionsBuilder<SnacksDbContext>()
            .UseSqlite(_connection)
            .Options;

        Factory = new TestContextFactory(opts);
        using var ctx = Factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public MediaFileRepository CreateRepository() => new(Factory);

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private sealed class TestContextFactory : IDbContextFactory<SnacksDbContext>
    {
        private readonly DbContextOptions<SnacksDbContext> _opts;
        public TestContextFactory(DbContextOptions<SnacksDbContext> opts) { _opts = opts; }
        public SnacksDbContext CreateDbContext() => new(_opts);
    }
}
