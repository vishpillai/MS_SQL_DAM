using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SqlDam.Web.Data;
using SqlDam.Web.Models;
using SqlDam.Web.Services;
using Xunit;

namespace SqlDam.Tests;

public sealed class ActivityEventPersistenceTests
{
    [Fact]
    public void PersistBatchAndCursors_CommitsEventsAndCursorTogether()
    {
        using var store = new TestStore();
        var activityEvent = store.CreateEvent();
        var cursors = new[] { (Mechanism: "xe", FileName: "events.xel", Offset: 128L) };

        store.EventService.PersistBatchAndCursors(
            [activityEvent], TenantConstants.DefaultTenantId, store.SourceId, cursors);

        Assert.Single(store.EventService.GetRecent(10, tenantId: TenantConstants.DefaultTenantId));
        Assert.Equal(("events.xel", 128L), store.EventService.GetCursor(store.SourceId, "xe"));
    }

    [Fact]
    public void PersistBatchAndCursors_RollsBackEventsWhenCursorWriteFails()
    {
        using var store = new TestStore();
        var activityEvent = store.CreateEvent();
        var invalidCursors = new[] { (Mechanism: (string)null!, FileName: "events.xel", Offset: 128L) };

        Assert.Throws<InvalidOperationException>(() => store.EventService.PersistBatchAndCursors(
            [activityEvent], TenantConstants.DefaultTenantId, store.SourceId, invalidCursors));

        Assert.Empty(store.EventService.GetRecent(10, tenantId: TenantConstants.DefaultTenantId));
        Assert.Null(store.EventService.GetCursor(store.SourceId, "xe").FileName);
    }

    private sealed class TestStore : IDisposable
    {
        private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "SqlDam.Tests", Guid.NewGuid().ToString("N"));
        private readonly PhysicalFileProvider _fileProvider;
        private readonly LocalDatabase _database;

        public Guid SourceId { get; } = Guid.NewGuid();
        public ActivityEventService EventService { get; }

        public TestStore()
        {
            Directory.CreateDirectory(_rootPath);
            _fileProvider = new PhysicalFileProvider(_rootPath);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:DataDirectory"] = Path.Combine(_rootPath, "data")
                })
                .Build();
            var environment = new TestHostEnvironment(_rootPath, _fileProvider);
            _database = new LocalDatabase(configuration, environment);
            var logStore = new SqlServerLogStore(configuration);
            EventService = new ActivityEventService(_database, logStore);
            InsertSource();
        }

        public ActivityEvent CreateEvent() => new()
        {
            TenantId = TenantConstants.DefaultTenantId,
            SourceId = SourceId,
            SourceName = "Test source",
            EventName = "login_success",
            Category = ActivityCategory.LoginSuccess,
            TimestampUtc = DateTime.UtcNow,
            Summary = "Test event",
            DetailXml = "<event />"
        };

        private void InsertSource()
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sources
                    (id, tenant_id, name, server_name, auth_mode, enabled,
                     poll_interval_seconds, created_at_utc, last_status)
                VALUES
                    ($id, $tenantId, 'Test source', 'localhost', 'SqlLogin', 1, 30, $created, 'Not polled yet')
                """;
            command.Parameters.AddWithValue("$id", SourceId.ToString());
            command.Parameters.AddWithValue("$tenantId", TenantConstants.DefaultTenantId.ToString());
            command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _fileProvider.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_rootPath)) Directory.Delete(_rootPath, recursive: true);
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath, IFileProvider fileProvider) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SqlDam.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = fileProvider;
    }
}