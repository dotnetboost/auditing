using DotNetBoost.Auditing.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace DotNetBoost.Auditing.Core;

/// <summary>
/// Collects the configuration between <c>AddAuditing()</c> and <c>Build()</c>. A store package adds
/// its own registrations through <see cref="Services"/> and marks itself with
/// <see cref="AuditingBuilderGuard.EnsureStoreNotConfigured"/>.
/// </summary>
public sealed class AuditingBuilder
{
    private readonly List<Assembly> _declaredAssemblies = [];
    private readonly List<Type> _declaredTypes = [];

    /// <summary>The service collection being configured. Stores register here.</summary>
    public IServiceCollection Services { get; }

    internal bool    StoreConfigured { get; set; }
    internal string? StoreName       { get; set; }

    /// <summary>
    /// The options instance handed to the engine through DI. Builder methods mutate this object
    /// rather than a private copy, so configuration applied anywhere in the chain takes effect.
    /// </summary>
    internal AuditingOptions Options { get; }

    /// <summary>Creates a builder over <paramref name="services"/> and registers its options.</summary>
    /// <param name="services">The services to configure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
    public AuditingBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Options  = new AuditingOptions();
        Services.AddSingleton(Options);
    }

    internal void DeclareAssembly(Assembly assembly) => _declaredAssemblies.Add(assembly);

    internal void DeclareType(Type type) => _declaredTypes.Add(type);

    /// <summary>
    /// Validates the configuration, reads the declared entities, and finishes the registration.
    /// </summary>
    /// <returns>The configured services, so a caller can carry on with them.</returns>
    /// <exception cref="InvalidOperationException">
    /// No store is configured, the options contradict each other, or two declared entities claim
    /// the same stored name.
    /// </exception>
    public IServiceCollection Build()
    {
        Options.Validate();

        if (!StoreConfigured)
        {
            throw new InvalidOperationException(
                "No audit store is configured, so there is nowhere to record to. Add one of " +
                "DotNetBoost.Auditing.EntityFrameworkCore, .Dapper or .MongoDb and call its " +
                "Use… method before Build() — or UseCustomStore<T>() for a store of your own.");
        }

        Services.Replace(ServiceDescriptor.Singleton(BuildRegistry()));
        return Services;
    }

    /// <summary>
    /// The declared entities, keyed by stored name. Built once, here, rather than scanned on
    /// demand: it reads every type in an assembly and throws on a collision, and both belong at
    /// startup rather than on the first recorded change.
    /// </summary>
    private AuditModelRegistry BuildRegistry()
    {
        var declared = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var type in _declaredAssemblies.SelectMany(Auditable).Concat(_declaredTypes))
        {
            var name = AuditEntityNames.For(type);

            if (declared.TryGetValue(name, out var existing))
            {
                if (existing == type)
                    continue;

                // Two types under one stored name would mean one entity's rules and retention
                // applied to the other's history, chosen by scan order. Refused at startup, where
                // it is a five-minute fix, rather than discovered as a trail that redacted the
                // wrong column.
                throw new InvalidOperationException(
                    $"'{existing.FullName}' and '{type.FullName}' are both declared under the entity name " +
                    $"'{name}'. Give one of them a distinct [AuditEntity(\"…\")] name.");
            }

            declared[name] = type;
        }

        return declared.Count == 0 ? AuditModelRegistry.Empty : new AuditModelRegistry(declared);
    }

    /// <summary>
    /// The types in <paramref name="assembly"/> worth declaring: a concrete, public class that
    /// carries any of the library's attributes. Scanning every class would put every DTO and
    /// controller in the registry under a name that happens to collide.
    /// </summary>
    private static IEnumerable<Type> Auditable(Assembly assembly) =>
        assembly.GetTypes().Where(static t =>
            t is { IsClass: true, IsAbstract: false, IsPublic: true }
            && (t.GetCustomAttribute<Attributes.AuditEntityAttribute>(inherit: true) is not null
                || t.GetCustomAttribute<Attributes.AuditedAttribute>(inherit: true) is not null
                || t.GetCustomAttribute<Attributes.AuditRetentionAttribute>(inherit: true) is not null));
}

/// <summary>
/// Guards a store package's registration. Public because the stores live in other packages and have
/// to use it — not because applications do.
/// </summary>
public static class AuditingBuilderGuard
{
    /// <summary>Records <paramref name="storeName"/> as the builder's audit store.</summary>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="storeName">The store's name, for the message if a second one is added.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="storeName"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">A store is already configured.</exception>
    public static void EnsureStoreNotConfigured(AuditingBuilder builder, string storeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);

        if (builder.StoreConfigured)
        {
            throw new InvalidOperationException(
                $"An audit store ('{builder.StoreName}') is already configured; '{storeName}' would replace " +
                "it silently. One store per application: entries written to one are invisible to the other, " +
                "and a trail split across two stores is not a trail.");
        }

        builder.StoreConfigured = true;
        builder.StoreName       = storeName;
    }
}
