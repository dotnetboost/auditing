using DotNetBoost.Auditing.API;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNetBoost.Auditing.ApiTests;

/// <summary>
/// Nothing used to say which entities the engine knows about, so a client filtering a trail by
/// <c>entityName</c> was really filtering by a name somebody had typed out of the source — and
/// which properties are redacted, the one thing a UI has to render differently, could only be
/// learned by waiting for a change to one of them.
/// <para>
/// Neither endpoint returns anything recorded. Several of these tests exist to keep it that way.
/// </para>
/// </summary>
public class DiscoveryEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<List<AuditEntityDescriptor>> EntitiesAsync(ApiHarness api, string? scope = null)
    {
        var response = await api.GetAsync("/api/audit/entities", scope: scope);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<List<AuditEntityDescriptor>>(Json))!;
    }

    private static async Task<AuditEntitySchema> SchemaAsync(ApiHarness api, string entityName)
    {
        var response = await api.GetAsync($"/api/audit/entities/{entityName}");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<AuditEntitySchema>(Json))!;
    }

    [Fact]
    public async Task The_list_contains_every_declared_entity_under_its_stored_name()
    {
        await using var api = await ApiHarness.StartAsync();

        var entities = await EntitiesAsync(api);
        var names    = entities.Select(e => e.Name).ToList();

        Assert.Contains("Product", names);
        Assert.Contains("SessionPing", names);

        // The stored name, not the class name: [AuditEntity("Invoice")] is what the trail is
        // addressed by, and a client filtering by the class name would find nothing.
        Assert.Contains("Invoice", names);
        Assert.DoesNotContain(nameof(InvoiceRecord), names);
        Assert.Equal(nameof(InvoiceRecord), entities.Single(e => e.Name == "Invoice").ClrType);
    }

    [Fact]
    public async Task The_list_says_which_entities_are_actually_audited()
    {
        await using var api = await ApiHarness.StartAsync();

        var entities = await EntitiesAsync(api);

        Assert.True(entities.Single(e => e.Name == "Product").Audited);

        // [AuditIgnore] on the class. Reported rather than hidden: "this entity is deliberately
        // not in the trail" is the answer to a question somebody is otherwise going to ask by
        // reading the source.
        var ignored = entities.Single(e => e.Name == "SessionPing");
        Assert.False(ignored.Audited);
        Assert.True(ignored.Ignored);
    }

    [Fact]
    public async Task Opt_in_mode_shows_up_as_an_entity_that_is_not_audited()
    {
        await using var api = await ApiHarness.StartAsync(options: o => o.RequireOptIn = true);

        var product = (await EntitiesAsync(api)).Single(e => e.Name == "Product");

        // Nothing about Product changed; the allow-list did. An operator wondering why a trail is
        // empty gets the answer here instead of from the absence of entries.
        Assert.False(product.Audited);
        Assert.False(product.OptedIn);
        Assert.False(product.Ignored);
    }

    [Fact]
    public async Task The_kill_switch_shows_up_as_nothing_being_audited()
    {
        await using var api = await ApiHarness.StartAsync(options: o => o.Enabled = false);

        var entities = await EntitiesAsync(api);

        Assert.All(entities, e => Assert.False(e.Audited));
    }

    [Fact]
    public async Task Retention_separates_an_entitys_own_from_the_configured_default()
    {
        await using var api = await ApiHarness.StartAsync(options: o => o.Retention = TimeSpan.FromDays(365));

        var entities = await EntitiesAsync(api);

        var invoice = entities.Single(e => e.Name == "Invoice");
        Assert.Equal(30d, invoice.RetentionDays);
        Assert.True(invoice.HasOwnRetention);

        // Product has no [AuditRetention], so it is kept for the configured default — and changing
        // that default moves this one and not the invoice's.
        var product = entities.Single(e => e.Name == "Product");
        Assert.Equal(365d, product.RetentionDays);
        Assert.False(product.HasOwnRetention);
    }

    [Fact]
    public async Task No_configured_retention_reads_as_null_rather_than_zero()
    {
        await using var api = await ApiHarness.StartAsync();

        var product = (await EntitiesAsync(api)).Single(e => e.Name == "Product");

        // null means a prune leaves its entries alone. Zero would mean they expire immediately,
        // which is the opposite, so the two must not be confused.
        Assert.Null(product.RetentionDays);
        Assert.False(product.HasOwnRetention);
    }

    [Fact]
    public async Task A_schema_names_properties_the_way_a_recorded_change_does()
    {
        await using var api = await ApiHarness.StartAsync();

        var id = await api.CreateProductAsync(asUser: "alice");
        await api.ChangePriceAsync(id, 19.99m, asUser: "alice");

        var schema = await SchemaAsync(api, "Product");

        var page = await api.GetAsync($"/api/audit?entityName=Product&entityId={id}");
        var body = await page.Content.ReadFromJsonAsync<AuditPageResponse>(Json);

        var recorded  = body!.Entries.SelectMany(e => e.Changes).Select(c => c.PropertyName).Distinct();
        var described = schema.Properties.Select(p => p.Name).ToList();

        Assert.All(recorded, name => Assert.Contains(name, described));
    }

    [Fact]
    public async Task A_schema_reports_the_rule_each_property_falls_under()
    {
        await using var api = await ApiHarness.StartAsync();

        var properties = (await SchemaAsync(api, "Product"))
            .Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);

        Assert.Equal("Record", properties["Name"].Rule);
        Assert.Equal("String", properties["Name"].Type);

        // The one a UI has to render differently: a change to it arrives with both values null and
        // isRedacted set, and "not disclosed" is not the same as "was empty".
        Assert.Equal("Redact", properties["SupplierSecret"].Rule);
    }

    [Fact]
    public async Task A_schema_lists_ignored_properties_as_ignored_rather_than_omitting_them()
    {
        await using var api = await ApiHarness.StartAsync();

        var properties = (await SchemaAsync(api, "Invoice"))
            .Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);

        Assert.Equal("Ignore", properties["LastSeenAt"].Rule);
        Assert.Equal("Record", properties["Number"].Rule);

        // A nullable value type is reported by its underlying type, with the nullability said
        // separately.
        Assert.Equal("Decimal", properties["Total"].Type);
        Assert.True(properties["Total"].Nullable);
        Assert.False(properties["Number"].Nullable);
    }

    [Fact]
    public async Task An_undeclared_entity_is_404_rather_than_an_empty_description()
    {
        await using var api = await ApiHarness.StartAsync();

        var response = await api.GetAsync("/api/audit/entities/NoSuchEntity");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Neither_endpoint_returns_anything_recorded()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync(name: "Widget", price: 24.99m, secret: "s3cret", asUser: "alice");

        var list   = await (await api.GetAsync("/api/audit/entities")).Content.ReadAsStringAsync();
        var schema = await (await api.GetAsync("/api/audit/entities/Product")).Content.ReadAsStringAsync();

        foreach (var body in new[] { list, schema })
        {
            Assert.DoesNotContain("Widget", body, StringComparison.Ordinal);
            Assert.DoesNotContain("24.99", body, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cret", body, StringComparison.Ordinal);
            Assert.DoesNotContain("alice", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Discovery_is_covered_by_the_same_authorization_as_the_reads()
    {
        await using var api = await ApiHarness.StartAsync(o => o.Policy = "AuditReader");

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await api.GetAsync("/api/audit/entities", asUser: null)).StatusCode);

        // Authenticated but without the claim the policy wants.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await api.GetAsync("/api/audit/entities")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await api.GetAsync("/api/audit/entities/Product")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await api.GetAsync("/api/audit/entities", scope: "audit.read")).StatusCode);
    }

    [Fact]
    public async Task Discovery_can_be_turned_off()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapDiscovery = false);

        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/audit/entities")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/audit/entities/Product")).StatusCode);

        // The reads are untouched.
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/audit")).StatusCode);
    }
}
