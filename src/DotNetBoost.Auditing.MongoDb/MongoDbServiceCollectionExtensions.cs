using DotNetBoost.Auditing.Core;
using DotNetBoost.Auditing.Core.Interfaces;
using DotNetBoost.Auditing.MongoDb;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the MongoDB store.</summary>
    public static class MongoDbAuditingBuilderExtensions
    {
        /// <summary>
        /// Keeps entries in MongoDB, over the <see cref="IMongoDatabase"/> the application has
        /// already registered:
        /// <code>
        /// services.AddSingleton&lt;IMongoClient&gt;(new MongoClient(connectionString));
        /// services.AddSingleton(sp =&gt; sp.GetRequiredService&lt;IMongoClient&gt;().GetDatabase("shop"));
        ///
        /// services.AddAuditing()
        ///         .UseMongoDb()
        ///         .DeclareEntitiesFrom(typeof(Product).Assembly)   // not optional here — see below
        ///         .Build();
        /// </code>
        /// <para>
        /// <b>Declare your entities.</b> Recording here is by name and key, so the engine has no type
        /// to read <c>[AuditRedact]</c> and <c>[AuditIgnore]</c> from unless the assembly is
        /// declared. Undeclared, a redacted property is written to the document in full.
        /// </para>
        /// </summary>
        /// <param name="builder">The auditing builder.</param>
        /// <param name="configure">Collection naming and index behaviour.</param>
        /// <returns>The builder, for chaining.</returns>
        public static AuditingBuilder UseMongoDb(this AuditingBuilder builder, Action<MongoDbOptions>? configure = null) =>
            builder.UseMongoDb(static sp => sp.GetRequiredService<IMongoDatabase>(), configure);

        /// <summary>
        /// Keeps entries in MongoDB over its own client, for an application that does not register
        /// one itself. The client is a singleton, as the driver intends — it owns the connection
        /// pool.
        /// </summary>
        /// <param name="builder">The auditing builder.</param>
        /// <param name="connectionString">The MongoDB connection string.</param>
        /// <param name="databaseName">The database to keep the collection in.</param>
        /// <param name="configure">Collection naming and index behaviour.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">A required string is empty.</exception>
        public static AuditingBuilder UseMongoDb(
            this AuditingBuilder builder,
            string connectionString,
            string databaseName,
            Action<MongoDbOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            builder.Services.TryAddSingleton<IMongoClient>(_ => new MongoClient(connectionString));

            return builder.UseMongoDb(
                sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName),
                configure);
        }

        /// <summary>
        /// Keeps entries in the database <paramref name="database"/> resolves — one per tenant, say,
        /// from a scoped accessor.
        /// </summary>
        /// <param name="builder">The auditing builder.</param>
        /// <param name="database">Resolves the database.</param>
        /// <param name="configure">Collection naming and index behaviour.</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">A store is already configured on this builder.</exception>
        public static AuditingBuilder UseMongoDb(
            this AuditingBuilder builder,
            Func<IServiceProvider, IMongoDatabase> database,
            Action<MongoDbOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(database);
            AuditingBuilderGuard.EnsureStoreNotConfigured(builder, "MongoDb");

            var options = new MongoDbOptions();
            configure?.Invoke(options);
            builder.Services.TryAddSingleton(options);

            // No class map is registered anywhere. The documents carry their serialization on
            // attributes of this package's own types, so nothing here changes how the rest of the
            // application serializes anything — see AuditEntryDocument.

            builder.Services.TryAddScoped(database);
            builder.Services.AddScoped<IAuditStore>(sp => new MongoAuditStore(
                sp.GetRequiredService<IMongoDatabase>(),
                options,
                sp.GetService<IClientSessionHandle>()));

            // IEntityKeyAccessor is deliberately left alone: the driver maps plain objects, so the
            // Core convention ([Key], Id, {Type}Id) is already the best available answer.

            if (options.CreateIndexes)
            {
                builder.Services.AddSingleton<IHostedService>(sp => new MongoIndexHostedService(
                    sp,
                    options,
                    sp.GetService<ILogger<MongoAuditStore>>() ?? NullLogger<MongoAuditStore>.Instance));
            }

            return builder;
        }
    }
}
