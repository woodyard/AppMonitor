using Arkimentum.AppMonitor.Api.Data;
using Arkimentum.AppMonitor.Api.Security;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arkimentum.AppMonitor.Api.Tests;

/// <summary>A clock the tests can move by hand, so "not seen in 7 days" and rate-limit windows are deterministic.</summary>
public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;

    public void Set(DateTimeOffset value) => _now = value;
}

/// <summary>
/// One in-memory SQLite database plus the services under test. SQLite (rather than the InMemory provider) is used on
/// purpose: it is a real relational store, so unique indexes, foreign keys and ExecuteDelete/ExecuteUpdate behave the
/// way they will against Azure SQL.
/// </summary>
public sealed class TestHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestHarness(ServerOptions? options = null, DateTimeOffset? now = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var contextOptions = new DbContextOptionsBuilder<AppMonitorDbContext>()
            .UseSqlite(_connection)
            .Options;

        Db = new AppMonitorDbContext(contextOptions);
        Db.Database.EnsureCreated();

        Options = options ?? new ServerOptions
        {
            ApiClientId = "aaaaaaaa-0000-0000-0000-000000000001",
            AdminClientId = "bbbbbbbb-0000-0000-0000-000000000002",
            OperatorTenantId = OperatorTenant,
            PublicServerUrl = "https://appmon-test.azurewebsites.net",
            PollIntervalSeconds = 600,
        };

        Time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        RateLimiter = new EnrollRateLimiter(Options, Time);
        Archive = new RecordingReportArchive();

        Devices = new DeviceService(Db, Options, RateLimiter, Archive, NullLogger<DeviceService>.Instance, Time);
        Admin = new AdminService(Db, Options, NullLogger<AdminService>.Instance, Time);
        Authenticator = new DeviceAuthenticator(Db);
    }

    public const string OperatorTenant = "11111111-1111-1111-1111-111111111111";
    public const string CustomerTenant = "22222222-2222-2222-2222-222222222222";
    public const string OtherTenant = "33333333-3333-3333-3333-333333333333";

    public AppMonitorDbContext Db { get; }
    public ServerOptions Options { get; }
    public FakeTimeProvider Time { get; }
    public EnrollRateLimiter RateLimiter { get; }
    public RecordingReportArchive Archive { get; }
    public DeviceService Devices { get; }
    public AdminService Admin { get; }
    public DeviceAuthenticator Authenticator { get; }

    public static AdminPrincipal GlobalAdmin => new(OperatorTenant, "ops@arkimentum.dk", "Ops", "oid-ops",
        [AdminPrincipal.RoleGlobalAdmin, AdminPrincipal.RoleAdmin]);

    public static AdminPrincipal CustomerAdmin => new(CustomerTenant, "it@contoso.com", "Contoso IT", "oid-contoso",
        [AdminPrincipal.RoleAdmin]);

    public static AdminPrincipal OtherCustomerAdmin => new(OtherTenant, "it@fabrikam.com", "Fabrikam IT", "oid-fabrikam",
        [AdminPrincipal.RoleAdmin]);

    /// <summary>A principal that authenticated but was never assigned an app role.</summary>
    public static AdminPrincipal RolelessUser => new(CustomerTenant, "guest@contoso.com", "Guest", "oid-guest", []);

    /// <summary>Creates an organization directly (bypassing the admin API) and returns it with its plaintext key.</summary>
    public async Task<(Organization Organization, string EnrollmentKey)> SeedOrganizationAsync(
        string name = "Contoso", string? tenantId = CustomerTenant)
    {
        var key = Secrets.NewEnrollmentKey();
        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = name,
            EntraTenantId = tenantId,
            EnrollmentKeyHash = Secrets.Hash(key),
            EnrollmentKeyRotatedUtc = Time.GetUtcNow(),
            CreatedUtc = Time.GetUtcNow(),
            IsActive = true,
        };
        Db.Organizations.Add(organization);
        await Db.SaveChangesAsync();
        return (organization, key);
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>Captures what would have gone to Blob Storage so the report tests can assert on the archive path.</summary>
public sealed class RecordingReportArchive : IReportArchive
{
    public List<(Guid OrganizationId, Guid DeviceId, DateTimeOffset ReportedUtc, string Json)> Entries { get; } = [];

    public Task ArchiveAsync(Guid organizationId, Guid deviceId, DateTimeOffset reportedUtc, string rawJson, CancellationToken ct)
    {
        Entries.Add((organizationId, deviceId, reportedUtc, rawJson));
        return Task.CompletedTask;
    }
}

/// <summary>Stands in for Entra ID in the auth tests: hands back whatever principal the test dictates.</summary>
public sealed class FakeAdminTokenValidator : IAdminTokenValidator
{
    public AdminPrincipal? Principal { get; set; }
    public string? Error { get; set; } = "Missing bearer token.";
    public string? LastHeader { get; private set; }

    public Task<AdminTokenResult> ValidateAsync(string? authorizationHeader, CancellationToken ct)
    {
        LastHeader = authorizationHeader;
        return Task.FromResult(Principal is null
            ? AdminTokenResult.Fail(Error ?? "The access token is not valid.")
            : AdminTokenResult.Ok(Principal));
    }
}
