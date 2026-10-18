using DotNetBoost.Auditing.API;
using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Attributes;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace DotNetBoost.Auditing.ApiTests;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }

    [AuditRedact]
    public string SupplierSecret { get; set; } = "";
}

public class ApiDbContext(DbContextOptions<ApiDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyAuditing();
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// Authenticates when the request carries <c>X-Test-User</c>, so a test can be anonymous,
/// authenticated, or authenticated without the scope a policy wants.
/// </summary>
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user) || user.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user[0]!),
            new(ClaimTypes.Name, "Display Name")
        ];

        // Several scopes, comma-separated: the destructive endpoints require the read policy AND
        // the destructive one, so a test needs a caller that satisfies both.
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scopes) && scopes.Count > 0)
        {
            foreach (var scope in scopes[0]!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                claims.Add(new Claim("scope", scope));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket    = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class ApiHarness : IAsyncDisposable
{
    private SqliteConnection _connection = null!;
    private IHost _host = null!;

    public HttpClient Client { get; private set; } = null!;

    public static async Task<ApiHarness> StartAsync(
        Action<AuditEndpointOptions>? endpoints = null,
        Action<AuditingOptions>? options = null)
    {
        var harness = new ApiHarness();

        harness._connection = new SqliteConnection("DataSource=:memory:");
        harness._connection.Open();

        var connection = harness._connection;

        harness._host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();

                    services.AddAuthentication(TestAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);

                    services.AddAuthorization(o =>
                    {
                        o.AddPolicy("AuditReader", p => p.RequireClaim("scope", "audit.read"));
                        o.AddPolicy("AuditAdmin", p => p.RequireClaim("scope", "audit.admin"));
                    });

                    services.AddDbContext<ApiDbContext>((sp, o) => o.UseSqlite(connection).UseAuditing(sp));

                    services.AddAuditing(o => options?.Invoke(o))
                            .UseEntityFrameworkCore<ApiDbContext>()
                            .UseHttpContextActor()
                            .DeclareEntity<Product>()
                            .Build();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();

                    app.UseEndpoints(route =>
                    {
                        route.MapAuditEndpoints(endpoints);

                        // Creates a product inside a request, so the HttpContext-backed actor
                        // accessor has a request to read. Anonymous on purpose: the trail's own
                        // endpoints are the ones that must be closed, not the application's.
                        route.MapPost("/test/products", async (ProductRequest body, ApiDbContext db, CancellationToken ct) =>
                        {
                            var product = new Product
                            {
                                Name           = body.Name,
                                Price          = body.Price,
                                SupplierSecret = body.SupplierSecret ?? ""
                            };

                            db.Products.Add(product);
                            await db.SaveChangesAsync(ct);
                            return Results.Ok(product.Id);
                        });

                        route.MapPost("/test/products/{id:int}/price", async (int id, decimal price, ApiDbContext db, CancellationToken ct) =>
                        {
                            var product = await db.Products.FindAsync([id], ct);
                            product!.Price = price;
                            await db.SaveChangesAsync(ct);
                            return Results.NoContent();
                        });
                    });
                }))
            .StartAsync();

        using var scope = harness._host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Database.EnsureCreatedAsync();

        harness.Client = harness._host.GetTestClient();
        return harness;
    }

    /// <summary>Creates a product through HTTP, as the named user when one is given.</summary>
    public async Task<int> CreateProductAsync(
        string name = "Widget",
        decimal price = 9.99m,
        string? secret = null,
        string? asUser = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/test/products")
        {
            Content = JsonContent.Create(new ProductRequest(name, price, secret))
        };

        if (asUser is not null)
            request.Headers.Add("X-Test-User", asUser);

        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<int>();
    }

    /// <summary>Changes a price through HTTP, as the named user when one is given.</summary>
    public async Task ChangePriceAsync(int id, decimal price, string? asUser = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/test/products/{id}/price?price={price}");

        if (asUser is not null)
            request.Headers.Add("X-Test-User", asUser);

        (await Client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    /// <summary>A GET against the audit endpoints, authenticated unless told otherwise.</summary>
    public Task<HttpResponseMessage> GetAsync(string url, string? asUser = "reader", string? scope = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (asUser is not null)
            request.Headers.Add("X-Test-User", asUser);

        if (scope is not null)
            request.Headers.Add("X-Test-Scope", scope);

        return Client.SendAsync(request);
    }

    /// <summary>A DELETE against the audit endpoints.</summary>
    public Task<HttpResponseMessage> DeleteAsync(string url, string? asUser = "admin", string? scope = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);

        if (asUser is not null)
            request.Headers.Add("X-Test-User", asUser);

        if (scope is not null)
            request.Headers.Add("X-Test-Scope", scope);

        return Client.SendAsync(request);
    }

    /// <summary>A POST against the audit endpoints.</summary>
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, string? asUser = "admin", string? scope = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = body is null ? null : JsonContent.Create(body)
        };

        if (asUser is not null)
            request.Headers.Add("X-Test-User", asUser);

        if (scope is not null)
            request.Headers.Add("X-Test-Scope", scope);

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        await _connection.DisposeAsync();
    }

    public sealed record ProductRequest(string Name, decimal Price, string? SupplierSecret);
}
