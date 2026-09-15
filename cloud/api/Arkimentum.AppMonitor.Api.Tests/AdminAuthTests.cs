using System.Security.Claims;
using Arkimentum.AppMonitor.Api.Contracts;
using Arkimentum.AppMonitor.Api.Security;
using Arkimentum.AppMonitor.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arkimentum.AppMonitor.Api.Tests;

/// <summary>
/// Authentication and authorisation, exercised through a fake token validator so no Entra ID tenant is needed. The
/// claim-mapping half is tested against the real EntraAdminTokenValidator (which is where "tid" becomes a principal).
/// </summary>
public sealed class AdminAuthTests : IDisposable
{
    private readonly TestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    // ------------------------------------------------------------------------------------------------ fake validator

    [Fact]
    public async Task AFakeValidatorWithoutAPrincipal_FailsWithTheReason()
    {
        var validator = new FakeAdminTokenValidator { Principal = null, Error = "The access token has expired." };

        var result = await validator.ValidateAsync("Bearer whatever", default);

        Assert.Null(result.Principal);
        Assert.Equal("The access token has expired.", result.Error);
        Assert.Equal("Bearer whatever", validator.LastHeader);
    }

    [Fact]
    public async Task APrincipalFromTheFakeValidator_ReachesItsOwnOrganizationOnly()
    {
        var (contoso, _) = await _harness.SeedOrganizationAsync("Contoso", TestHarness.CustomerTenant);
        var validator = new FakeAdminTokenValidator { Principal = TestHarness.CustomerAdmin };

        var result = await validator.ValidateAsync("Bearer token", default);
        Assert.NotNull(result.Principal);

        Assert.Equal(200, (await _harness.Admin.GetConfigAsync(result.Principal, contoso.Id, default)).Status);
        Assert.Equal(403, (await _harness.Admin.GetConfigAsync(
            TestHarness.OtherCustomerAdmin, contoso.Id, default)).Status);
    }

    // ------------------------------------------------------------------------------------------------ global admin gate

    [Theory]
    [InlineData(TestHarness.OperatorTenant, new[] { AdminPrincipal.RoleGlobalAdmin }, true)]
    [InlineData(TestHarness.OperatorTenant, new[] { AdminPrincipal.RoleAdmin }, false)]
    [InlineData(TestHarness.CustomerTenant, new[] { AdminPrincipal.RoleGlobalAdmin }, false)]
    [InlineData(TestHarness.CustomerTenant, new string[0], false)]
    public void GlobalAdmin_RequiresBothTheRoleAndTheOperatorTenant(string tenantId, string[] roles, bool expected)
    {
        var principal = new AdminPrincipal(tenantId, "someone@example.com", null, null, roles);
        Assert.Equal(expected, _harness.Admin.IsGlobalAdmin(principal));
    }

    [Fact]
    public void GlobalAdmin_IsNeverGrantedWhenNoOperatorTenantIsConfigured()
    {
        using var harness = new TestHarness(new ServerOptions { OperatorTenantId = "" });
        var principal = new AdminPrincipal(TestHarness.OperatorTenant, "ops@arkimentum.dk", null, null, [AdminPrincipal.RoleGlobalAdmin]);

        Assert.False(harness.Admin.IsGlobalAdmin(principal));
    }

    [Fact]
    public void RoleComparison_IsCaseInsensitive()
    {
        var principal = new AdminPrincipal(TestHarness.OperatorTenant, "ops@arkimentum.dk", null, null, ["appmonitor.globaladmin"]);
        Assert.True(principal.HasRole(AdminPrincipal.RoleGlobalAdmin));
        Assert.True(_harness.Admin.IsGlobalAdmin(principal));
    }

    // ------------------------------------------------------------------------------------------------ claim mapping

    [Fact]
    public void ClaimsAreMappedToAPrincipal()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("tid", TestHarness.CustomerTenant),
            new Claim("preferred_username", "it@contoso.com"),
            new Claim("name", "Contoso IT"),
            new Claim("oid", "0000-oid"),
            new Claim("roles", AdminPrincipal.RoleAdmin),
            new Claim("roles", AdminPrincipal.RoleGlobalAdmin),
        ]);

        var principal = EntraAdminTokenValidator.FromClaims(identity);

        Assert.NotNull(principal);
        Assert.Equal(TestHarness.CustomerTenant, principal.TenantId);
        Assert.Equal("it@contoso.com", principal.UserPrincipalName);
        Assert.Equal("Contoso IT", principal.DisplayName);
        Assert.Equal("0000-oid", principal.ObjectId);
        Assert.Equal(2, principal.Roles.Count);
    }

    [Fact]
    public void ATokenWithoutATenantClaim_HasNoPrincipal() =>
        Assert.Null(EntraAdminTokenValidator.FromClaims(new ClaimsIdentity([new Claim("preferred_username", "x@y.z")])));

    [Fact]
    public void AnAppOnlyTokenFallsBackToTheApplicationId()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("tid", TestHarness.OperatorTenant),
            new Claim("appid", "aaaa-bbbb"),
            new Claim("roles", AdminPrincipal.RoleGlobalAdmin),
        ]);

        Assert.Equal("aaaa-bbbb", EntraAdminTokenValidator.FromClaims(identity)!.UserPrincipalName);
    }

    // ------------------------------------------------------------------------------------------------ development bypass

    [Fact]
    public async Task TheDevelopmentValidator_ParsesItsOwnTokenFormat()
    {
        var validator = new DevelopmentAdminTokenValidator(NullLogger<DevelopmentAdminTokenValidator>.Instance);

        var result = await validator.ValidateAsync(
            $"Bearer dev:{TestHarness.OperatorTenant}:ops@arkimentum.dk:{AdminPrincipal.RoleGlobalAdmin},{AdminPrincipal.RoleAdmin}", default);

        Assert.NotNull(result.Principal);
        Assert.Equal(TestHarness.OperatorTenant, result.Principal.TenantId);
        Assert.True(_harness.Admin.IsGlobalAdmin(result.Principal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer eyJ0eXAiOiJKV1Qi")]
    [InlineData("Bearer dev:only-one-part")]
    public async Task TheDevelopmentValidator_RejectsAnythingElse(string? header)
    {
        var validator = new DevelopmentAdminTokenValidator(NullLogger<DevelopmentAdminTokenValidator>.Instance);
        Assert.Null((await validator.ValidateAsync(header, default)).Principal);
    }

    // ------------------------------------------------------------------------------------------------ device scheme

    [Theory]
    [InlineData("Device 11112222-3333-4444-5555-666677778888:abc", true)]
    [InlineData("device 11112222-3333-4444-5555-666677778888:abc", true)]
    [InlineData("DEVICE 11112222-3333-4444-5555-666677778888:abc:def", true)]
    [InlineData("Bearer 11112222-3333-4444-5555-666677778888:abc", false)]
    [InlineData("Device 11112222-3333-4444-5555-666677778888", false)]
    [InlineData("Device :abc", false)]
    [InlineData("11112222-3333-4444-5555-666677778888:abc", false)]
    public void DeviceHeaderParsing(string header, bool expected) =>
        Assert.Equal(expected, DeviceAuthenticator.TryParse(header, out _, out _));

    [Fact]
    public void ADeviceKeyIs256BitsOfRandomnessAndStoredOnlyAsASha256()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => Secrets.NewKey()).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.Equal(43, k.Length));               // base64url of 32 bytes
        Assert.All(keys, k => Assert.Equal(32, Secrets.Hash(k).Length));
        Assert.True(Secrets.Verify(keys[0], Secrets.Hash(keys[0])));
        Assert.False(Secrets.Verify(keys[0], Secrets.Hash(keys[1])));
        Assert.False(Secrets.Verify(keys[0], null));
        Assert.False(Secrets.Verify(keys[0], new byte[16]));
    }

    [Fact]
    public void AnEnrollmentKeyCarriesTheEkLivePrefixOnTopOfTheSame256Bits()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => Secrets.NewEnrollmentKey()).ToList();

        Assert.Equal("ek_live_", Secrets.EnrollmentKeyPrefix);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.StartsWith(Secrets.EnrollmentKeyPrefix, k, StringComparison.Ordinal));
        Assert.All(keys, k => Assert.Equal(Secrets.EnrollmentKeyPrefix.Length + 43, k.Length));   // 8 + 43 = 51
        Assert.All(keys, k => Assert.Equal(32, Secrets.Hash(k).Length));
        Assert.True(Secrets.Verify(keys[0], Secrets.Hash(keys[0])));

        // The prefix is decoration, not structure: it is part of the secret and is hashed with the rest.
        Assert.False(Secrets.Verify(keys[0].Substring(Secrets.EnrollmentKeyPrefix.Length), Secrets.Hash(keys[0])));
    }

    [Fact]
    public async Task AnEnrollmentKeyIssuedBeforeThePrefixExisted_StillWorks()
    {
        // Hash whatever is presented: an organization whose stored hash was taken over a bare base64url key
        // must keep enrolling devices after the prefix was introduced.
        var legacyKey = Secrets.NewKey();
        Assert.False(legacyKey.StartsWith(Secrets.EnrollmentKeyPrefix, StringComparison.Ordinal));

        var organization = new Data.Organization
        {
            Id = Guid.NewGuid(),
            Name = "Legacy A/S",
            EntraTenantId = TestHarness.CustomerTenant,
            EnrollmentKeyHash = Secrets.Hash(legacyKey),
            CreatedUtc = _harness.Time.GetUtcNow(),
            IsActive = true,
        };
        _harness.Db.Organizations.Add(organization);
        await _harness.Db.SaveChangesAsync();

        var enrolled = await _harness.Devices.EnrollAsync(new EnrollRequest
        {
            OrganizationId = organization.Id,
            EnrollmentKey = legacyKey,
            DeviceName = "LEGACY-PC",
            MachineGuid = "legacy-guid",
        }, "203.0.113.7", default);

        Assert.True(enrolled.IsSuccess);

        // Rotating hands out a prefixed key, and the legacy one stops working.
        var rotated = await _harness.Admin.RotateEnrollmentKeyAsync(TestHarness.CustomerAdmin, organization.Id, default);
        Assert.StartsWith(Secrets.EnrollmentKeyPrefix, rotated.Value!.EnrollmentKey!, StringComparison.Ordinal);
        Assert.Equal(401, (await _harness.Devices.EnrollAsync(new EnrollRequest
        {
            OrganizationId = organization.Id,
            EnrollmentKey = legacyKey,
            DeviceName = "LEGACY-PC-2",
            MachineGuid = "legacy-guid-2",
        }, "203.0.113.7", default)).Status);
    }

    // ------------------------------------------------------------------------------------------------ auth-config

    [Fact]
    public void AuthConfig_IsBuiltFromTheApplicationSettings()
    {
        var options = ServerOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:ClientId"] = "aaaaaaaa-0000-0000-0000-000000000001",
                ["AzureAd:Audience"] = "api://aaaaaaaa-0000-0000-0000-000000000001",
                ["AzureAd:TenantIdMode"] = "organizations",
                ["AdminClientId"] = "bbbbbbbb-0000-0000-0000-000000000002",
                ["OperatorTenantId"] = TestHarness.OperatorTenant,
                ["PublicServerUrl"] = "https://appmon-prod-api.azurewebsites.net/",
                ["PollIntervalSeconds"] = "1800",
            })
            .Build());

        Assert.Equal("https://login.microsoftonline.com/organizations", options.Authority);
        Assert.Equal("api://aaaaaaaa-0000-0000-0000-000000000001/AppMonitor.Admin", options.ScopeUri);
        Assert.Equal("https://appmon-prod-api.azurewebsites.net", options.PublicServerUrl);
        Assert.Equal(1800, options.PollIntervalSeconds);
        Assert.Contains("api://aaaaaaaa-0000-0000-0000-000000000001", options.ValidAudiences);
        Assert.Contains("aaaaaaaa-0000-0000-0000-000000000001", options.ValidAudiences);

        var response = new AuthConfigResponse
        {
            ClientId = options.AdminClientId,
            Authority = options.Authority,
            Scope = options.ScopeUri,
        };
        Assert.Equal("bbbbbbbb-0000-0000-0000-000000000002", response.ClientId);
        Assert.Equal(CloudRoutes.ApiVersion, response.ApiVersion);
    }
}
