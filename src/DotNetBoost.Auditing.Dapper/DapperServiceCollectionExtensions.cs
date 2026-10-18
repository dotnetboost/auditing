using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.Dapper;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the Dapper store.</summary>
    public static class DapperAuditingBuilderExtensions
    {
        /// <summary>
        /// Keeps entries through Dapper, over a connection created per scope:
        /// <code>
        /// services.AddAuditing()
        ///         .UseDapper(_ =&gt; new NpgsqlConnection(connectionString), migrateSchema: true)
        ///         .DeclareEntitiesFrom(typeof(Product).Assembly)   // not optional here — see below
        ///         .Build();
        /// </code>
        /// <para>
        /// The connection is registered as <see cref="IDbConnection"/> in the scope, so the
        /// container disposes it and application code can inject the same one — begin a transaction
        /// on it, write your row, record the change, commit, and the two land together.
        /// </para>
        /// <para>
        /// <b>Declare your entities.</b> A Dapper application records by name and key, so the engine
        /// has no type to read <c>[AuditRedact]</c> and <c>[AuditIgnore]</c> from unless the
        /// assembly is declared. Undeclared, a redacted property is written down in full — the one
        /// failure in this library that is both silent and permanent.
        /// </para>
        /// </summary>
        /// <param name="builder">The auditing builder.</param>
        /// <param name="connectionFactory">
        /// Creates the connection for each scope. Its runtime type selects the SQL dialect — SQL
        /// Server, PostgreSQL or SQLite — unless <see cref="DapperOptions.Dialect"/> says otherwise.
        /// </param>
        /// <param name="migrateSchema">
        /// When <c>true</c>, a hosted service creates the two tables at startup if they are absent,
        /// in the shape the EF Core store maps. Leave it off — the default — when the schema is
        /// owned by migrations or a deployment step; it only ever creates, and will not migrate a
        /// table whose shape has changed.
        /// </param>
        /// <param name="configure">Table and schema naming, and an explicit dialect.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">A store is already configured on this builder.</exception>
        public static AuditingBuilder UseDapper(
            this AuditingBuilder builder,
            Func<IServiceProvider, IDbConnection> connectionFactory,
            bool migrateSchema = false,
            Action<DapperOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(connectionFactory);
            AuditingBuilderGuard.EnsureStoreNotConfigured(builder, "Dapper");

            var options = new DapperOptions();
            configure?.Invoke(options);
            builder.Services.TryAddSingleton(options);

            // Scoped and container-owned: the connection is disposed with the scope, and anything
            // else in the scope that asks for IDbConnection gets the very one the store uses.
            builder.Services.AddScoped(connectionFactory);

            // Scoped too, so a transaction the application puts in the scope is the one the store
            // enlists in — a row and its trail then commit or roll back together.
            builder.Services.AddScoped<IAuditStore>(sp => new DapperAuditStore(
                sp.GetRequiredService<IDbConnection>(), options, sp.GetService<IDbTransaction>()));

            // IEntityKeyAccessor is deliberately left alone: Dapper maps plain objects, so the Core
            // convention ([Key], Id, {Type}Id) is already the best available answer.

            if (migrateSchema)
            {
                builder.Services.AddSingleton<IHostedService>(sp => new DapperMigrationHostedService(
                    sp,
                    options,
                    sp.GetService<ILogger<DapperAuditStore>>() ?? NullLogger<DapperAuditStore>.Instance));
            }

            return builder;
        }
    }
}
