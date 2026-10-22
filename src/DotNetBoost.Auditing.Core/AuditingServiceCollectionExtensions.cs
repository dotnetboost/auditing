using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Entry point for registering DotNetBoost.Auditing.</summary>
    public static class AuditingServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the engine and returns a builder for further configuration. Follow it with a
        /// store and <c>Build()</c>.
        /// </summary>
        /// <param name="services">The services to add to.</param>
        /// <returns>The builder.</returns>
        public static AuditingBuilder AddAuditing(this IServiceCollection services) =>
            services.AddAuditing(static _ => { });

        /// <summary>
        /// Registers the engine, applies <paramref name="configure"/> to its options, and returns a
        /// builder for further configuration.
        /// <code>
        /// services.AddAuditing(o =&gt; o.Retention = TimeSpan.FromDays(365))
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .DeclareEntitiesFrom(typeof(Product).Assembly)
        ///         .Build();
        /// </code>
        /// </summary>
        /// <param name="services">The services to add to.</param>
        /// <param name="configure">Applied to the options.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
        public static AuditingBuilder AddAuditing(this IServiceCollection services, Action<AuditingOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.TryAddSingleton(TimeProvider.System);

            // Singletons: neither holds state about a request. The EF Core package replaces the key
            // accessor with a scoped one over the DbContext, which is why this is TryAdd rather
            // than Add.
            services.TryAddSingleton<IAuditActorAccessor, NullAuditActorAccessor>();
            services.TryAddSingleton<IEntityKeyAccessor, ConventionEntityKeyAccessor>();

            // Told when a change is deliberately not recorded. The default does nothing, so an
            // application that does not ask for it pays a virtual call per suppressed write — and
            // one that does ask gets the only signal there is that a trail has stopped covering
            // something. DotNetBoost.Auditing.API has the ILogger-backed one.
            services.TryAddSingleton<IAuditObserver, NullAuditObserver>();

            // Three counters, on the meter named by AuditingTelemetry.MeterName. GetService rather
            // than GetRequiredService: an ASP.NET Core host has an IMeterFactory registered and a
            // console application that only called AddAuditing() does not, and a library that
            // refused to start over telemetry nobody subscribed to would deserve the bug report.
            services.TryAddSingleton(sp => new AuditMetrics(sp.GetService<IMeterFactory>()));

            // An empty registry until Build() replaces it, so an engine built without declarations
            // applies no model rules by name rather than failing to resolve.
            services.TryAddSingleton(AuditModelRegistry.Empty);

            // Scoped: the store is (an EF Core DbContext, a Dapper connection), and the engine is
            // only as long-lived as the store behind it.
            // The three engines are registered as themselves, not behind interfaces: they are
            // internal, and IAuditManager is the library's whole public surface.
            services.TryAddScoped<AuditRecorder>();
            services.TryAddScoped<AuditReader>();
            services.TryAddScoped<AuditPruner>();
            services.TryAddScoped<IAuditManager, AuditManager>();

            var builder = new AuditingBuilder(services);
            configure(builder.Options);
            return builder;
        }
    }

    /// <summary>Builder methods for engine behaviour.</summary>
    public static class AuditingBuilderExtensions
    {
        /// <summary>
        /// Declares every entity in <paramref name="assembly"/> that carries one of the library's
        /// attributes, so the engine can apply their rules to writes addressed by name and a prune
        /// can find their retentions.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="assembly">The assembly to scan.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
        public static AuditingBuilder DeclareEntitiesFrom(this AuditingBuilder builder, Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(assembly);

            builder.DeclareAssembly(assembly);
            return builder;
        }

        /// <summary>Declares one entity.</summary>
        /// <typeparam name="TEntity">The entity type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public static AuditingBuilder DeclareEntity<TEntity>(this AuditingBuilder builder)
            where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.DeclareType(typeof(TEntity));
            return builder;
        }

        /// <summary>Declares several entities.</summary>
        /// <param name="builder">The builder.</param>
        /// <param name="entityTypes">The entity types.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
        public static AuditingBuilder DeclareEntities(this AuditingBuilder builder, params Type[] entityTypes)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(entityTypes);

            foreach (var type in entityTypes)
            {
                ArgumentNullException.ThrowIfNull(type);
                builder.DeclareType(type);
            }

            return builder;
        }

        /// <summary>
        /// Keeps entries for <paramref name="retention"/>, for entities with no
        /// <c>[AuditRetention]</c> of their own. Nothing is deleted until a prune runs.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="retention">How long to keep entries.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public static AuditingBuilder WithRetention(this AuditingBuilder builder, TimeSpan retention)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Options.Retention = retention;
            return builder;
        }

        /// <summary>Cuts recorded values short at <paramref name="characters"/>.</summary>
        /// <param name="builder">The builder.</param>
        /// <param name="characters">The limit.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public static AuditingBuilder WithMaxValueLength(this AuditingBuilder builder, int characters)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Options.MaxValueLength = characters;
            return builder;
        }

        /// <summary>Sets the default and largest page a read may return.</summary>
        /// <param name="builder">The builder.</param>
        /// <param name="defaultSize">The page size used when a query does not set one.</param>
        /// <param name="maxSize">The largest page a query may ask for.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public static AuditingBuilder WithPageSize(this AuditingBuilder builder, int defaultSize, int maxSize)
        {
            ArgumentNullException.ThrowIfNull(builder);

            // Max first: the setters validate independently, but Validate() refuses a default above
            // the max, and raising the max before lowering the default keeps every intermediate
            // state legal whichever way the caller is moving them.
            builder.Options.MaxPageSize     = maxSize;
            builder.Options.DefaultPageSize = defaultSize;
            return builder;
        }

        /// <summary>
        /// Records only entities marked <c>[Audited]</c> — an allow-list instead of the default
        /// "everything that is not ignored".
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public static AuditingBuilder RequireOptIn(this AuditingBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Options.RequireOptIn = true;
            return builder;
        }

        /// <summary>
        /// Records creations and deletions without their values — the entry says a row appeared or
        /// went, and nothing about what it said.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        /// <remarks>
        /// Think hardest about the deletion half: after the row is gone, the entry is the only copy
        /// of what it said.
        /// </remarks>
        public static AuditingBuilder WithoutCreatedAndDeletedValues(this AuditingBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Options.RecordCreatedValues = false;
            builder.Options.RecordDeletedValues = false;
            return builder;
        }

        /// <summary>
        /// Stops recording, leaving reads working. The kill switch, for an incident — the services
        /// stay resolvable and every write returns <c>null</c>.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public static AuditingBuilder DisableRecording(this AuditingBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Options.Enabled = false;
            return builder;
        }

        /// <summary>Uses <typeparamref name="TAccessor"/> to capture who is acting.</summary>
        /// <typeparam name="TAccessor">The accessor implementation.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public static AuditingBuilder UseActorAccessor<TAccessor>(this AuditingBuilder builder)
            where TAccessor : class, IAuditActorAccessor
        {
            ArgumentNullException.ThrowIfNull(builder);

            // Scoped, not singleton: an accessor worth registering reads something per-request.
            builder.Services.Replace(ServiceDescriptor.Scoped<IAuditActorAccessor, TAccessor>());
            return builder;
        }

        /// <summary>
        /// Uses <typeparamref name="TObserver"/> to watch what the engine decides not to record.
        /// <code>
        /// services.AddAuditing()
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .UseObserver&lt;MyMetricsObserver&gt;()
        ///         .Build();
        /// </code>
        /// <para>
        /// The default does nothing. An ASP.NET application usually wants
        /// <c>.UseLoggingObserver()</c> from <c>DotNetBoost.Auditing.API</c> instead of writing
        /// one; this overload is for sending the same signal somewhere else — a counter, a health
        /// check, an alert.
        /// </para>
        /// </summary>
        /// <typeparam name="TObserver">The observer implementation.</typeparam>
        /// <param name="builder">The auditing builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        /// <remarks>
        /// Singleton, not scoped: an observer counts and logs rather than reading anything about a
        /// request, and one that needs per-request state can inject an accessor for it.
        /// <see cref="IAuditObserver"/> says what an implementation may not do — throw, or block.
        /// </remarks>
        public static AuditingBuilder UseObserver<TObserver>(this AuditingBuilder builder)
            where TObserver : class, IAuditObserver
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.Replace(ServiceDescriptor.Singleton<IAuditObserver, TObserver>());
            return builder;
        }

        /// <summary>Uses <typeparamref name="TAccessor"/> to read entity keys.</summary>
        /// <typeparam name="TAccessor">The accessor implementation.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public static AuditingBuilder UseEntityKeyAccessor<TAccessor>(this AuditingBuilder builder)
            where TAccessor : class, IEntityKeyAccessor
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.Replace(ServiceDescriptor.Scoped<IEntityKeyAccessor, TAccessor>());
            return builder;
        }

        /// <summary>
        /// Uses <typeparamref name="TStore"/> as the audit store, instead of one of the store
        /// packages.
        /// </summary>
        /// <typeparam name="TStore">The store implementation.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">A store is already configured.</exception>
        public static AuditingBuilder UseCustomStore<TStore>(this AuditingBuilder builder)
            where TStore : class, IAuditStore
        {
            ArgumentNullException.ThrowIfNull(builder);

            AuditingBuilderGuard.EnsureStoreNotConfigured(builder, typeof(TStore).Name);
            builder.Services.Replace(ServiceDescriptor.Scoped<IAuditStore, TStore>());
            return builder;
        }

        /// <summary>Uses the store <paramref name="store"/> resolves, instead of one of the store packages.</summary>
        /// <param name="builder">The builder.</param>
        /// <param name="store">Resolves the store.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">A store is already configured.</exception>
        public static AuditingBuilder UseCustomStore(
            this AuditingBuilder builder,
            Func<IServiceProvider, IAuditStore> store)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(store);

            AuditingBuilderGuard.EnsureStoreNotConfigured(builder, "custom");
            builder.Services.Replace(ServiceDescriptor.Scoped(store));
            return builder;
        }
    }
}
