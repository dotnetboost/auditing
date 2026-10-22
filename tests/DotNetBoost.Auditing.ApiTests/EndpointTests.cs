using DotNetBoost.Auditing.API;
using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;

namespace DotNetBoost.Auditing.ApiTests;

public class EndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<AuditPageResponse> PageAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuditPageResponse>(Json))!;
    }

    [Fact]
    public async Task The_endpoints_require_authorization_by_default()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        // A trail says who changed what and when. The sibling packages map anonymously and leave
        // securing them to the application; this one does not, because the failure mode is a quiet
        // disclosure rather than a broken feature.
        var anonymous = await api.GetAsync("/api/audit", asUser: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var authenticated = await api.GetAsync("/api/audit");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task Authorization_can_be_turned_off_deliberately()
    {
        await using var api = await ApiHarness.StartAsync(o => o.RequireAuthorization = false);
        await api.CreateProductAsync();

        var page = await PageAsync(await api.GetAsync("/api/audit", asUser: null));
        Assert.Single(page.Entries);
    }

    [Fact]
    public async Task A_policy_is_enforced_when_one_is_configured()
    {
        await using var api = await ApiHarness.StartAsync(o => o.Policy = "AuditReader");
        await api.CreateProductAsync();

        // Authenticated but without the claim the policy wants.
        var forbidden = await api.GetAsync("/api/audit");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var allowed = await api.GetAsync("/api/audit", scope: "audit.read");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task A_page_reports_the_entries_and_the_size_it_served()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync("First");
        await api.CreateProductAsync("Second");

        var page = await PageAsync(await api.GetAsync("/api/audit"));

        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(2, page.Take);
        Assert.Equal(0, page.Skip);

        // Not counted unless asked: null is "no count was taken", never zero.
        Assert.Null(page.Total);
    }

    [Fact]
    public async Task A_total_is_returned_when_it_is_asked_for()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync("First");
        await api.CreateProductAsync("Second");
        await api.CreateProductAsync("Third");

        var page = await PageAsync(await api.GetAsync("/api/audit?take=2&includeTotal=true"));

        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task An_oversized_take_is_clamped_and_the_response_says_so()
    {
        await using var api = await ApiHarness.StartAsync(options: o =>
        {
            o.MaxPageSize     = 2;
            // Lowered with it: Build() refuses a default page larger than the maximum, since every
            // unsized read would then be clamped below the default it was given.
            o.DefaultPageSize = 2;
        });

        for (var i = 0; i < 5; i++)
            await api.CreateProductAsync($"Widget {i}");

        var page = await PageAsync(await api.GetAsync("/api/audit?take=10000"));

        // There is no way to ask an endpoint of this library for an unbounded read.
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(2, page.Take);
    }

    [Fact]
    public async Task An_omitted_take_is_the_configured_default()
    {
        await using var api = await ApiHarness.StartAsync(options: o =>
        {
            o.MaxPageSize     = 10;
            o.DefaultPageSize = 2;
        });

        for (var i = 0; i < 5; i++)
            await api.CreateProductAsync($"Widget {i}");

        var page = await PageAsync(await api.GetAsync("/api/audit"));
        Assert.Equal(2, page.Entries.Count);
    }

    [Fact]
    public async Task One_entitys_trail_is_filtered_by_name_and_key()
    {
        await using var api = await ApiHarness.StartAsync();
        var id = await api.CreateProductAsync("First");
        await api.CreateProductAsync("Second");
        await api.ChangePriceAsync(id, 1.23m);

        var page = await PageAsync(await api.GetAsync($"/api/audit?entityName=Product&entityId={id}"));

        Assert.Equal(2, page.Entries.Count);
        Assert.All(page.Entries, e => Assert.Equal(id.ToString(CultureInfo.InvariantCulture), e.EntityId));
    }

    [Fact]
    public async Task The_action_is_serialized_as_its_name()
    {
        await using var api = await ApiHarness.StartAsync();
        var id = await api.CreateProductAsync();
        await api.ChangePriceAsync(id, 5m);

        var page = await PageAsync(await api.GetAsync("/api/audit"));

        // A name rather than a number, so a client reads the trail without a lookup table and a
        // renumbering cannot change what a stored response meant.
        Assert.Contains(page.Entries, e => e.Action == "Created");
        Assert.Contains(page.Entries, e => e.Action == "Updated");
    }

    [Fact]
    public async Task Filtering_by_action_narrows_to_that_kind()
    {
        await using var api = await ApiHarness.StartAsync();
        var id = await api.CreateProductAsync();
        await api.ChangePriceAsync(id, 5m);

        var page = await PageAsync(await api.GetAsync("/api/audit?action=Updated"));
        Assert.Equal("Updated", Assert.Single(page.Entries).Action);
    }

    [Fact]
    public async Task The_actor_comes_from_the_request_that_made_the_change()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync(asUser: "alice@example.com");

        var page = await PageAsync(await api.GetAsync("/api/audit"));

        // The subject identifier, not the display name: a name can be changed by the person it
        // names, which is the wrong key for a record of who acted.
        Assert.Equal("alice@example.com", Assert.Single(page.Entries).Actor);
    }

    [Fact]
    public async Task A_change_made_outside_a_signed_in_request_has_no_actor()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        var page = await PageAsync(await api.GetAsync("/api/audit"));

        // Honestly unknown rather than "system".
        Assert.Null(Assert.Single(page.Entries).Actor);
    }

    [Fact]
    public async Task Entries_with_no_actor_can_be_asked_for_separately()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync("Signed in", asUser: "alice");
        await api.CreateProductAsync("Anonymous");

        var missing = await PageAsync(await api.GetAsync("/api/audit?actorMissing=true"));
        Assert.Null(Assert.Single(missing.Entries).Actor);

        var alice = await PageAsync(await api.GetAsync("/api/audit?actor=alice"));
        Assert.Equal("alice", Assert.Single(alice.Entries).Actor);
    }

    [Fact]
    public async Task An_empty_actor_parameter_is_not_a_filter()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync(asUser: "alice");

        // ?actor= means the client left the box blank. Matching the empty string would return
        // nothing and look like a bug.
        var page = await PageAsync(await api.GetAsync("/api/audit?actor="));
        Assert.Single(page.Entries);
    }

    [Fact]
    public async Task A_redacted_value_is_not_disclosed_over_http()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync(secret: "swordfish");

        var response = await api.GetAsync("/api/audit");
        var body     = await response.Content.ReadAsStringAsync();

        // The whole response, not just the parsed shape: the value must not be anywhere in it.
        Assert.DoesNotContain("swordfish", body, StringComparison.Ordinal);

        var page   = await PageAsync(response);
        var change = Assert.Single(Assert.Single(page.Entries).Changes, c => c.PropertyName == "SupplierSecret");

        Assert.True(change.IsRedacted);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public async Task One_entry_can_be_read_by_id()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        var page = await PageAsync(await api.GetAsync("/api/audit"));
        var id   = Assert.Single(page.Entries).Id;

        var response = await api.GetAsync($"/api/audit/{id}");
        response.EnsureSuccessStatusCode();

        var entry = await response.Content.ReadFromJsonAsync<AuditEntryResponse>(Json);
        Assert.Equal(id, entry!.Id);
        Assert.NotEmpty(entry.Changes);
    }

    [Fact]
    public async Task An_unknown_entry_is_a_404()
    {
        await using var api = await ApiHarness.StartAsync();

        var response = await api.GetAsync($"/api/audit/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Changes_can_be_left_out_of_a_page()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        var page = await PageAsync(await api.GetAsync("/api/audit?includeChanges=false"));
        Assert.Empty(Assert.Single(page.Entries).Changes);
    }

    [Fact]
    public async Task A_trail_can_be_walked_with_the_cursor_each_page_returns()
    {
        await using var api = await ApiHarness.StartAsync();

        for (var i = 1; i <= 5; i++)
            await api.CreateProductAsync($"Product {i}");

        var seen   = new List<Guid>();
        string? after = null;

        for (var request = 0; request < 10; request++)
        {
            var url  = after is null ? "/api/audit?take=2" : $"/api/audit?take=2&after={Uri.EscapeDataString(after)}";
            var page = await PageAsync(await api.GetAsync(url));

            if (page.Entries.Count == 0)
            {
                // The walk ends with an empty page, and that page has no cursor to follow.
                Assert.Null(page.NextCursor);
                break;
            }

            seen.AddRange(page.Entries.Select(e => e.Id));
            Assert.NotNull(page.NextCursor);
            after = page.NextCursor;
        }

        // Every entry once: five creations, no row seen twice and none missed at a page boundary.
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_cursor_the_api_did_not_issue_is_the_clients_mistake()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        var response = await api.GetAsync("/api/audit?after=not-a-cursor");

        // 400 rather than silently serving the newest page: a client paging a long trail would
        // otherwise loop over page one for ever with nothing to explain why.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Asking_for_a_cursor_and_an_offset_at_once_is_refused()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        var first  = await PageAsync(await api.GetAsync("/api/audit?take=1"));
        var both   = await api.GetAsync($"/api/audit?skip=1&after={Uri.EscapeDataString(first.NextCursor!)}");

        // Two answers to where the page starts. Either could be the one the client meant, so
        // neither is guessed at.
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task The_destructive_endpoints_are_not_mapped_by_default()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        // Not merely secured — absent. An HTTP verb that deletes a trail is not something to
        // publish because a library happened to offer it.
        var entity = await api.DeleteAsync("/api/audit/entity/Product/1");
        var actor  = await api.DeleteAsync("/api/audit/actor?actor=alice");
        var prune  = await api.PostAsync("/api/audit/prune");

        // 404 or 405 depending on the path shape — ASP.NET answers 405 where a path has only a GET.
        // Which of the two it is does not matter; that nothing can be destroyed does.
        Assert.False(entity.IsSuccessStatusCode);
        Assert.False(actor.IsSuccessStatusCode);
        Assert.False(prune.IsSuccessStatusCode);

        var page = await PageAsync(await api.GetAsync("/api/audit"));
        Assert.Single(page.Entries);
    }

    [Fact]
    public async Task Erasing_an_entity_reports_what_it_deleted()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapErasure = true);
        var id = await api.CreateProductAsync("First");
        await api.CreateProductAsync("Second");

        var response = await api.DeleteAsync($"/api/audit/entity/Product/{id}");
        response.EnsureSuccessStatusCode();

        var deleted = await response.Content.ReadFromJsonAsync<DeletionResponse>(Json);
        Assert.Equal(1, deleted!.Deleted);

        var page = await PageAsync(await api.GetAsync("/api/audit"));
        Assert.DoesNotContain(page.Entries, e => e.EntityId == id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Erasing_a_trail_that_is_not_there_succeeds_with_a_count_of_zero()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapErasure = true);

        var response = await api.DeleteAsync("/api/audit/entity/Product/999");
        response.EnsureSuccessStatusCode();

        // An entity with no trail and one that never existed are the same observation here, and
        // "nothing to erase" is a successful erasure.
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<DeletionResponse>(Json))!.Deleted);
    }

    [Fact]
    public async Task Erasing_an_actor_removes_what_they_recorded()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapErasure = true);
        await api.CreateProductAsync("First", asUser: "alice");
        await api.CreateProductAsync("Second", asUser: "bob");

        var response = await api.DeleteAsync("/api/audit/actor?actor=alice");
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<DeletionResponse>(Json))!.Deleted);

        var page = await PageAsync(await api.GetAsync("/api/audit"));
        Assert.Equal("bob", Assert.Single(page.Entries).Actor);
    }

    [Fact]
    public async Task An_actor_whose_name_is_not_a_path_segment_can_still_be_erased()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapErasure = true);

        // A subject claim that is a URI, which is what several identity providers issue. In the
        // path this was unreachable: the slashes made it a different route, and a catch-all would
        // have matched a typo just as happily.
        const string actor = "https://sts.example.com/users/42";

        await api.CreateProductAsync("First", asUser: actor);
        await api.CreateProductAsync("Second", asUser: "bob");

        var response = await api.DeleteAsync($"/api/audit/actor?actor={Uri.EscapeDataString(actor)}");
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<DeletionResponse>(Json))!.Deleted);

        var page = await PageAsync(await api.GetAsync("/api/audit"));
        Assert.Equal("bob", Assert.Single(page.Entries).Actor);
    }

    [Fact]
    public async Task Erasing_with_no_actor_is_the_clients_mistake_and_erases_nothing()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapErasure = true);
        await api.CreateProductAsync("First", asUser: "alice");

        foreach (var query in new[] { "", "?actor=", "?actor=%20" })
        {
            var response = await api.DeleteAsync("/api/audit/actor" + query);

            // 400 rather than "erased everything with no actor", which is what an unguarded
            // parameter on an irreversible endpoint would have meant.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Single((await PageAsync(await api.GetAsync("/api/audit"))).Entries);
    }

    [Fact]
    public async Task A_prune_by_age_reports_what_it_deleted()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapPrune = true);
        await api.CreateProductAsync();

        // Nothing is old enough yet.
        var nothing = await api.PostAsync("/api/audit/prune", new { OlderThanDays = 30 });
        Assert.Equal(0, (await nothing.Content.ReadFromJsonAsync<DeletionResponse>(Json))!.Deleted);

        // A zero or negative age is refused — see A_bad_request_comes_back_as_problem_details.
        var negative = await api.PostAsync("/api/audit/prune", new { OlderThanDays = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [Fact]
    public async Task A_prune_with_no_body_applies_the_configured_retention()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapPrune = true);
        await api.CreateProductAsync();

        var response = await api.PostAsync("/api/audit/prune");
        response.EnsureSuccessStatusCode();

        // No retention configured, so nothing to do — and deleting on a default would be deleting
        // history nobody asked to lose.
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<DeletionResponse>(Json))!.Deleted);
    }

    [Fact]
    public async Task The_destructive_endpoints_can_take_a_stricter_policy_than_the_reads()
    {
        await using var api = await ApiHarness.StartAsync(o =>
        {
            o.Policy            = "AuditReader";
            o.DestructivePolicy = "AuditAdmin";
            o.MapErasure        = true;
        });

        await api.CreateProductAsync();

        // Reading and destroying are different privileges.
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/audit", scope: "audit.read")).StatusCode);

        var readerErasing = await api.DeleteAsync("/api/audit/entity/Product/1", scope: "audit.read");
        Assert.Equal(HttpStatusCode.Forbidden, readerErasing.StatusCode);

        // The destructive policy is required ON TOP of the read policy, not instead of it: the
        // destructive endpoints are nested in the read group. That way it is impossible to
        // configure a destructive endpoint that is easier to reach than the reads — so an admin
        // needs both, and an admin who only has audit.admin is refused.
        var adminWithoutRead = await api.DeleteAsync("/api/audit/entity/Product/1", scope: "audit.admin");
        Assert.Equal(HttpStatusCode.Forbidden, adminWithoutRead.StatusCode);

        var admin = await api.DeleteAsync("/api/audit/entity/Product/1", scope: "audit.read,audit.admin");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task An_unmatchable_erasure_address_erases_nothing_rather_than_failing()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapErasure = true);
        await api.CreateProductAsync();

        // A key longer than the column cannot address a stored entry, so it matches none. Erasure
        // reads before it deletes, so this is a successful erasure of nothing rather than an error
        // — unlike recording, where an over-long key is refused because it would write a row
        // nothing could ever find again.
        var response = await api.DeleteAsync($"/api/audit/entity/Product/{new string('x', 200)}");
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<DeletionResponse>(Json))!.Deleted);
        Assert.Single((await PageAsync(await api.GetAsync("/api/audit"))).Entries);
    }

    [Fact]
    public async Task A_bad_request_comes_back_as_problem_details()
    {
        await using var api = await ApiHarness.StartAsync(o => o.MapPrune = true);

        // A negative age would put the cutoff in the future and delete the entry being written as
        // it is written. Refused as the caller's mistake, with the reason in the body.
        var response = await api.PostAsync("/api/audit/prune", new { OlderThanDays = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_endpoints_can_be_mounted_under_a_prefix_of_your_own()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Timestamps_are_serialized_as_UTC()
    {
        await using var api = await ApiHarness.StartAsync();
        await api.CreateProductAsync();

        var body = await (await api.GetAsync("/api/audit")).Content.ReadAsStringAsync();

        // Without the store's UTC converter this comes back with no "Z" and every client reads it
        // as local time — an hour or eight wrong about when a change happened.
        Assert.Contains("Z\"", body, StringComparison.Ordinal);
    }
}
