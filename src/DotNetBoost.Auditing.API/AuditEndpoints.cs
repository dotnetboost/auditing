using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DotNetBoost.Auditing.API
{
    /// <summary>One property's change, as the endpoints return it.</summary>
    /// <param name="PropertyName">The property, as named on the class.</param>
    /// <param name="OldValue">Its value before, or <c>null</c> — see <paramref name="IsRedacted"/>.</param>
    /// <param name="NewValue">Its value after, or <c>null</c>.</param>
    /// <param name="IsRedacted">
    /// Whether the values were withheld because the model marks the property sensitive. When this
    /// is set, the two nulls mean "not disclosed" rather than "was empty", and a client should say
    /// so rather than render a blank.
    /// </param>
    /// <param name="IsTruncated">Whether either value was cut short at the configured limit.</param>
    public sealed record AuditChangeResponse(
        string PropertyName,
        string? OldValue,
        string? NewValue,
        bool IsRedacted,
        bool IsTruncated)
    {
        /// <summary>Maps a stored row to its wire shape.</summary>
        /// <param name="change">The stored property change.</param>
        /// <returns>The response shape.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="change"/> is <c>null</c>.</exception>
        public static AuditChangeResponse From(AuditPropertyChange change)
        {
            ArgumentNullException.ThrowIfNull(change);

            return new AuditChangeResponse(
                change.PropertyName,
                change.OldValue,
                change.NewValue,
                change.IsRedacted,
                change.IsTruncated);
        }
    }

    /// <summary>One recorded change, as the endpoints return it.</summary>
    /// <param name="Id">The entry's identity.</param>
    /// <param name="EntityName">The entity's stored name, e.g. <c>Product</c>.</param>
    /// <param name="EntityId">That entity's key.</param>
    /// <param name="Action">
    /// <c>Created</c>, <c>Updated</c> or <c>Deleted</c>. Serialized as its name rather than its
    /// number, so a client reads the trail without a lookup table and a renumbering cannot silently
    /// change what a stored response meant.
    /// </param>
    /// <param name="Actor">
    /// Who made the change, or <c>null</c> when it was never captured. <c>null</c> is not a claim
    /// about anybody — do not render it as "system".
    /// </param>
    /// <param name="CorrelationId">Ties this entry to the others of the same edit.</param>
    /// <param name="OccurredAt">When it happened. Always UTC.</param>
    /// <param name="Metadata">
    /// Whatever the application stored alongside the entry, as the string it stored. Passed through
    /// rather than parsed: the library never looked inside it, and an endpoint that started parsing
    /// it would be the first thing to break when an application put something else there.
    /// </param>
    /// <param name="Changes">
    /// The properties that moved. Empty when the read asked for entries without them, and
    /// legitimately empty for a deletion recorded without its values.
    /// </param>
    public sealed record AuditEntryResponse(
        Guid Id,
        string EntityName,
        string EntityId,
        string Action,
        string? Actor,
        string CorrelationId,
        DateTime OccurredAt,
        string? Metadata,
        IReadOnlyList<AuditChangeResponse> Changes)
    {
        /// <summary>Maps a stored entry to its wire shape.</summary>
        /// <param name="entry">The stored entry.</param>
        /// <returns>The response shape.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <c>null</c>.</exception>
        public static AuditEntryResponse From(AuditEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            return new AuditEntryResponse(
                entry.Id,
                entry.EntityName,
                entry.EntityId,
                entry.Action.ToString(),
                entry.Actor,
                entry.CorrelationId,
                entry.OccurredAt,
                entry.Metadata,
                [.. entry.Changes.Select(AuditChangeResponse.From)]);
        }
    }

    /// <summary>A page of a trail, as the endpoints return it.</summary>
    /// <param name="Entries">The matching entries, newest first.</param>
    /// <param name="Total">
    /// How many entries matched in total, or <c>null</c> when the read did not ask for a count.
    /// <c>null</c> means <b>not counted</b> and never zero — a client must not paginate against it.
    /// </param>
    /// <param name="Skip">The offset served.</param>
    /// <param name="Take">
    /// The page size served, <b>after clamping</b>. A client that asked for more gets the most it
    /// may have, and this is how it finds out.
    /// </param>
    public sealed record AuditPageResponse(
        IReadOnlyList<AuditEntryResponse> Entries,
        long? Total,
        int Skip,
        int Take)
    {
        /// <summary>Maps a page to its wire shape.</summary>
        /// <param name="page">The page.</param>
        /// <param name="skip">The offset served.</param>
        /// <returns>The response shape.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="page"/> is <c>null</c>.</exception>
        public static AuditPageResponse From(AuditPage page, int skip)
        {
            ArgumentNullException.ThrowIfNull(page);

            return new AuditPageResponse(
                [.. page.Entries.Select(AuditEntryResponse.From)],
                page.Total,
                skip,
                page.Entries.Count);
        }
    }

    /// <summary>Body of <c>POST {prefix}/prune</c>.</summary>
    public sealed record PruneRequest
    {
        /// <summary>
        /// Delete entries older than this many days, whatever the configured retention. Omit to
        /// prune by the configured and declared retentions instead.
        /// </summary>
        public int? OlderThanDays { get; init; }

        /// <summary>Only this entity's entries, or omit for every entity.</summary>
        public string? EntityName { get; init; }

        /// <summary>How many entries to delete per round trip. Default 1000.</summary>
        public int BatchSize { get; init; } = 1000;
    }

    /// <summary>What a prune or an erasure did.</summary>
    /// <param name="Deleted">How many entries were deleted.</param>
    public sealed record DeletionResponse(int Deleted);

    /// <summary>How the generated endpoints are secured and which of them exist.</summary>
    public sealed class AuditEndpointOptions
    {
        /// <summary>
        /// Whether the endpoints require an authenticated caller. <b>Default <c>true</c>.</b>
        /// <para>
        /// The sibling packages map their endpoints anonymously and leave securing them to the
        /// application. This one does not, because of what the data is: a trail says who changed
        /// what and when, which is a map of an organisation's activity and, through
        /// <see cref="AuditEntry.Actor"/>, personal data about its staff. The failure mode of
        /// getting that wrong is not a broken feature, it is a quiet disclosure — so the default is
        /// the safe one and opting out has to be written down.
        /// </para>
        /// </summary>
        public bool RequireAuthorization { get; set; } = true;

        /// <summary>
        /// An authorization policy to require instead of mere authentication — <c>"AuditReader"</c>.
        /// Ignored when <see cref="RequireAuthorization"/> is off.
        /// </summary>
        public string? Policy { get; set; }

        /// <summary>
        /// Whether to map the erasure endpoints — <c>DELETE {prefix}/entity/…</c> and
        /// <c>DELETE {prefix}/actor/…</c>. <b>Default <c>false</c>.</b>
        /// <para>
        /// Not mapped rather than merely secured. Erasure is the one irreversible thing the library
        /// does, it exists for subject-access deletion requests rather than for routine use, and an
        /// HTTP verb that deletes a trail is not something to publish because a library happened to
        /// offer it.
        /// </para>
        /// </summary>
        public bool MapErasure { get; set; }

        /// <summary>
        /// Whether to map <c>POST {prefix}/prune</c>. <b>Default <c>false</c></b>, for the same
        /// reason as <see cref="MapErasure"/>: a prune deletes history, and most applications should
        /// schedule <see cref="IAuditPruner"/> rather than expose it.
        /// </summary>
        public bool MapPrune { get; set; }

        /// <summary>
        /// An <b>additional</b> policy for the erasure and prune endpoints, when they are mapped —
        /// reading a trail and destroying one are different privileges.
        /// <para>
        /// It is required <i>on top of</i> <see cref="Policy"/>, not instead of it: the destructive
        /// endpoints are nested inside the read group, so a caller must satisfy both. That is the
        /// safe direction — it is impossible to configure a destructive endpoint that is easier to
        /// reach than the reads, and any authorization an application adds to the group this method
        /// returns covers every endpoint in it rather than only the harmless ones.
        /// </para>
        /// <para>
        /// So grant the destructive role the read policy as well. If the two must be genuinely
        /// disjoint, map the endpoints twice under different prefixes with different options.
        /// </para>
        /// </summary>
        public string? DestructivePolicy { get; set; }
    }

    /// <summary>Minimal-API endpoints over a trail.</summary>
    public static class AuditEndpoints
    {
        /// <summary>
        /// Maps the read endpoints under <c>/api/audit</c>, and the destructive ones if asked:
        /// <code>
        /// app.MapAuditEndpoints();                                  // reads, authorized
        /// app.MapAuditEndpoints(o =&gt; o.Policy = "AuditReader");     // reads, under a policy
        /// </code>
        /// <list type="bullet">
        /// <item><description><c>GET {prefix}</c> — a filtered, paged page of the trail, newest first.</description></item>
        /// <item><description><c>GET {prefix}/{id}</c> — one entry with its property changes.</description></item>
        /// <item><description><c>DELETE {prefix}/entity/{entityName}/{entityId}</c> — erase one entity's trail. Opt-in.</description></item>
        /// <item><description><c>DELETE {prefix}/actor/{actor}</c> — erase one actor's entries. Opt-in.</description></item>
        /// <item><description><c>POST {prefix}/prune</c> — apply retention. Opt-in.</description></item>
        /// </list>
        /// <para>
        /// Every read is bounded by the engine: <c>take</c> is clamped to the configured maximum and
        /// an omitted <c>take</c> is the configured default, never "all of them". The page reports
        /// the size it actually served.
        /// </para>
        /// </summary>
        /// <param name="endpoints">The route builder.</param>
        /// <param name="configure">Security and which endpoints exist. Optional.</param>
        /// <returns>The route group, so an application can add its own metadata or filters.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <c>null</c>.</exception>
        public static RouteGroupBuilder MapAuditEndpoints(
            this IEndpointRouteBuilder endpoints,
            Action<AuditEndpointOptions>? configure = null) =>
            endpoints.MapAuditEndpoints("/api/audit", configure);

        /// <summary>
        /// As <see cref="MapAuditEndpoints(IEndpointRouteBuilder, Action{AuditEndpointOptions})"/>,
        /// under a prefix of your own.
        /// </summary>
        /// <param name="endpoints">The route builder.</param>
        /// <param name="prefix">The base path, e.g. <c>/admin/audit</c>.</param>
        /// <param name="configure">Security and which endpoints exist. Optional.</param>
        /// <returns>The route group.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="prefix"/> is empty.</exception>
        public static RouteGroupBuilder MapAuditEndpoints(
            this IEndpointRouteBuilder endpoints,
            string prefix,
            Action<AuditEndpointOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

            var options = new AuditEndpointOptions();
            configure?.Invoke(options);

            var group = endpoints.MapGroup(prefix.TrimEnd('/'))
                .WithTags("Audit")
                .AddEndpointFilter<AuditProblemFilter>();

            if (options.RequireAuthorization)
            {
                if (options.Policy is { Length: > 0 } policy)
                    group.RequireAuthorization(policy);
                else
                    group.RequireAuthorization();
            }

            group.MapGet("/", QueryAsync)
                .WithSummary("A page of the trail, filtered, newest first. Always bounded.")
                .ProducesProblem(StatusCodes.Status400BadRequest);

            group.MapGet("/{id:guid}", GetAsync)
                .WithSummary("One entry with its property changes.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            if (options.MapErasure)
            {
                var erasure = Destructive(group, options);

                erasure.MapDelete("/entity/{entityName}/{entityId}", EraseEntityAsync)
                    .WithSummary("Erases one entity's entire trail. Irreversible.")
                    .ProducesProblem(StatusCodes.Status400BadRequest);

                erasure.MapDelete("/actor/{actor}", EraseActorAsync)
                    .WithSummary("Erases every entry recorded against one actor. Irreversible.")
                    .ProducesProblem(StatusCodes.Status400BadRequest);
            }

            if (options.MapPrune)
            {
                Destructive(group, options)
                    .MapPost("/prune", PruneAsync)
                    .WithSummary("Deletes entries past their retention. Irreversible.")
                    .ProducesProblem(StatusCodes.Status400BadRequest);
            }

            return group;
        }

        /// <summary>
        /// A sub-group for the endpoints that delete, so a stricter policy can cover them without
        /// loosening the reads.
        /// </summary>
        private static RouteGroupBuilder Destructive(RouteGroupBuilder group, AuditEndpointOptions options)
        {
            var destructive = group.MapGroup(string.Empty);

            if (options.RequireAuthorization && options.DestructivePolicy is { Length: > 0 } policy)
                destructive.RequireAuthorization(policy);

            return destructive;
        }

        /// <summary>
        /// <c>GET {prefix}</c>. The filters are separate query parameters rather than a bound
        /// object so that each one is named in the OpenAPI document.
        /// </summary>
        private static async Task<Ok<AuditPageResponse>> QueryAsync(
            IAuditReader reader,
            string? entityName,
            string? entityId,
            AuditAction? action,
            string? actor,
            bool? actorMissing,
            string? correlationId,
            DateTime? from,
            DateTime? to,
            int? skip,
            int? take,
            bool? includeTotal,
            bool? includeChanges,
            CancellationToken ct)
        {
            var query = new AuditQuery
            {
                EntityName     = Trimmed(entityName),
                EntityId       = Trimmed(entityId),
                Action         = action,
                Actor          = Trimmed(actor),
                ActorMissing   = actorMissing ?? false,
                CorrelationId  = Trimmed(correlationId),
                From           = from,
                To             = to,
                Skip           = skip ?? 0,
                // Zero means "the configured default", which the reader applies. Passing it through
                // rather than inventing one here keeps one definition of the page size.
                Take           = take ?? 0,
                IncludeTotal   = includeTotal ?? false,
                IncludeChanges = includeChanges ?? true
            };

            var page = await reader.QueryAsync(query, ct).ConfigureAwait(false);
            return TypedResults.Ok(AuditPageResponse.From(page, query.Skip < 0 ? 0 : query.Skip));
        }

        /// <summary><c>GET {prefix}/{id}</c>.</summary>
        private static async Task<Results<Ok<AuditEntryResponse>, NotFound>> GetAsync(
            IAuditReader reader,
            Guid id,
            CancellationToken ct)
        {
            var entry = await reader.GetAsync(id, ct).ConfigureAwait(false);

            return entry is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(AuditEntryResponse.From(entry));
        }

        /// <summary><c>DELETE {prefix}/entity/{entityName}/{entityId}</c>.</summary>
        private static async Task<Ok<DeletionResponse>> EraseEntityAsync(
            IAuditPruner pruner,
            string entityName,
            string entityId,
            CancellationToken ct)
        {
            var deleted = await pruner.EraseForAsync(entityName, entityId, ct).ConfigureAwait(false);

            // 200 with a count of zero rather than 404: an entity with no trail and an entity that
            // never existed are the same observation here, and "nothing to erase" is a successful
            // erasure.
            return TypedResults.Ok(new DeletionResponse(deleted));
        }

        /// <summary><c>DELETE {prefix}/actor/{actor}</c>.</summary>
        private static async Task<Ok<DeletionResponse>> EraseActorAsync(
            IAuditPruner pruner,
            string actor,
            CancellationToken ct)
        {
            var deleted = await pruner.EraseForActorAsync(actor, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeletionResponse(deleted));
        }

        /// <summary><c>POST {prefix}/prune</c>.</summary>
        private static async Task<Ok<DeletionResponse>> PruneAsync(
            IAuditPruner pruner,
            PruneRequest? request,
            CancellationToken ct)
        {
            var body      = request ?? new PruneRequest();
            var batchSize = body.BatchSize;

            var deleted = body.OlderThanDays is { } days
                ? await pruner
                    .PruneOlderThanAsync(TimeSpan.FromDays(days), Trimmed(body.EntityName), batchSize, ct)
                    .ConfigureAwait(false)
                : await pruner.PruneAsync(batchSize, ct).ConfigureAwait(false);

            return TypedResults.Ok(new DeletionResponse(deleted));
        }

        /// <summary>
        /// A query string's empty parameter is absent, not a filter. <c>?actor=</c> means the client
        /// left the box blank, and matching entries whose actor is the empty string would return
        /// nothing and look like a bug — <c>actorMissing=true</c> is how that question is asked.
        /// </summary>
        private static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
