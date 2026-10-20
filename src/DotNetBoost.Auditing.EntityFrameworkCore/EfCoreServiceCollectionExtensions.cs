using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the EF Core store.</summary>
    public static class EntityFrameworkAuditingBuilderExtensions
    {
        /// <summary>
        /// Keeps entries in <typeparamref name="TContext"/>:
        /// <code>
        /// services.AddDbContext&lt;AppDbContext&gt;((sp, o) =&gt; o.UseNpgsql(cs).UseAuditing(sp));
        ///
        /// services.AddAuditing()
        ///         .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///         .Build();
        /// </code>
        /// <para>
        /// Two lines, and they do different things. This one says <i>where entries are kept</i>.
        /// <see cref="AuditingDbContextOptionsExtensions.UseAuditing"/> on the context says
        /// <i>capture changes to this context automatically</i> — without it the tables are still
        /// there and <see cref="IAuditManager"/> still works, but nothing records itself.
        /// </para>
        /// <para>
        /// Add the tables to the model with <c>modelBuilder.ApplyAuditing()</c> in
        /// <c>OnModelCreating</c>, and generate a migration for them as you would any other table.
        /// </para>
        /// </summary>
        /// <typeparam name="TContext">The context holding the two tables.</typeparam>
        /// <param name="builder">The auditing builder.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">A store is already configured.</exception>
        public static AuditingBuilder UseEntityFrameworkCore<TContext>(this AuditingBuilder builder)
            where TContext : DbContext
        {
            ArgumentNullException.ThrowIfNull(builder);
            AuditingBuilderGuard.EnsureStoreNotConfigured(builder, "EntityFrameworkCore");

            var services = builder.Services;

            services.AddScoped<IAuditStore>(sp => new EfCoreAuditStore(sp.GetRequiredService<TContext>()));

            // The model knows more about keys than the convention accessor can work out — a shadow
            // key, or one configured fluently — so it replaces it rather than sitting alongside.
            services.Replace(ServiceDescriptor.Scoped<IEntityKeyAccessor>(
                sp => new EfCoreEntityKeyAccessor(sp.GetRequiredService<TContext>())));

            services.TryAddScoped<AuditingSaveChangesInterceptor>();

            return builder;
        }
    }

    /// <summary>Attaches the auditing capture to a context.</summary>
    public static class AuditingDbContextOptionsExtensions
    {
        /// <summary>
        /// Captures every change this context saves. Use the <c>(sp, options)</c> overload of
        /// <c>AddDbContext</c>:
        /// <code>
        /// services.AddDbContext&lt;AppDbContext&gt;((sp, o) =&gt; o.UseSqlite(cs).UseAuditing(sp));
        /// </code>
        /// <para>
        /// Not compatible with <c>AddDbContextPool</c>: pooled options are built once from the root
        /// provider and cannot carry the per-request services the capture needs to know who is
        /// acting.
        /// </para>
        /// </summary>
        /// <param name="options">The context's options being built.</param>
        /// <param name="serviceProvider">The scope's provider.</param>
        /// <returns>The options builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The EF Core store has not been registered.</exception>
        public static DbContextOptionsBuilder UseAuditing(
            this DbContextOptionsBuilder options,
            IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(serviceProvider);

            var interceptor = serviceProvider.GetService<AuditingSaveChangesInterceptor>()
                ?? throw new InvalidOperationException(
                    "The auditing EF Core store is not registered. Call " +
                    "services.AddAuditing().UseEntityFrameworkCore<TContext>().Build() first.");

            return options.AddInterceptors(interceptor);
        }
    }
}
