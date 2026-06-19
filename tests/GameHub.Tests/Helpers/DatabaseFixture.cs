using GameHub.Core.Data;
using GameHub.Ingest.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Tests.Helpers;

/// <summary>
/// Provides in-memory SQLite database contexts for integration tests.
/// Each call to CreateCoreContext/CreateIngestContext returns a fresh, empty DB.
///
/// IMPORTANT: SQLite in-memory databases are destroyed when the connection closes.
/// The connection is kept open and returned so tests can control its lifetime.
/// </summary>
public static class DatabaseFixture
{
    /// <summary>
    /// Creates a fresh CoreContext backed by an in-memory SQLite database.
    /// The caller must dispose both the context and the connection when done.
    /// </summary>
    public static (CoreContext Db, SqliteConnection Connection) CreateCoreContext(
        ITenantContext? tenantContext = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CoreContext>()
            .UseSqlite(connection)
            .Options;

        var db = new CoreContext(options, tenantContext ?? new DefaultTenantContext());
        db.Database.EnsureCreated();
        return (db, connection);
    }

    /// <summary>
    /// Creates a fresh IngestContext backed by an in-memory SQLite database.
    /// The caller must dispose both the context and the connection when done.
    /// </summary>
    public static (IngestContext Db, SqliteConnection Connection) CreateIngestContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IngestContext>()
            .UseSqlite(connection)
            .Options;

        var db = new IngestContext(options);
        db.Database.EnsureCreated();
        return (db, connection);
    }
}
