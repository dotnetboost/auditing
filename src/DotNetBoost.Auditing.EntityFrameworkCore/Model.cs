using DotNetBoost.Auditing.Core.Metadata;
using DotNetBoost.Auditing.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DotNetBoost.Auditing.EntityFrameworkCore;

/// <summary>Table naming for the two tables this store adds to your model.</summary>
public sealed class AuditingModelOptions
{
    /// <summary>Table holding the entries. Default: <c>AuditEntries</c>.</summary>
    public string EntriesTable { get; set; } = "AuditEntries";

    /// <summary>
    /// Table holding one row per property that changed. Default: <c>AuditPropertyChanges</c>.
    /// <para>
    /// Rows rather than a JSON column on the entry, because "every change anyone ever made to
    /// <c>Price</c>" is most of why a trail is kept, and in JSON that question is an unindexable
    /// scan in SQL Server and PostgreSQL alike.
    /// </para>
    /// </summary>
    public string ChangesTable { get; set; } = "AuditPropertyChanges";

    /// <summary>Schema for the tables. Default: the model's default schema.</summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Whether to index <see cref="AuditPropertyChange.PropertyName"/>. Default <c>true</c>, which
    /// is what makes the per-property question answerable.
    /// <para>
    /// Turn it off for a trail that is only ever read per entity: it is an index over the largest
    /// table in the schema, and if nothing asks "when did anyone last change a price" it is write
    /// cost for nothing.
    /// </para>
    /// </summary>
    public bool IndexPropertyName { get; set; } = true;
}

/// <summary>Entity mapping for <see cref="AuditEntry"/>.</summary>
/// <param name="options">Table naming.</param>
public sealed class AuditEntryConfiguration(AuditingModelOptions options) : IEntityTypeConfiguration<AuditEntry>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.ToTable(options.EntriesTable, options.Schema);

        builder.HasKey(e => e.Id).HasName("PK_" + options.EntriesTable);

        // The engine assigns the id before the store sees the entry — the interceptor needs one to
        // hang property rows off while the save is still in flight — so the database must not
        // generate one of its own.
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.EntityName).HasMaxLength(AuditEntityNames.MaxLength).IsRequired();
        builder.Property(e => e.EntityId).HasMaxLength(EntityKey.MaxLength).IsRequired();
        builder.Property(e => e.Action).IsRequired();
        builder.Property(e => e.Actor).HasMaxLength(AuditColumns.ActorMaxLength);
        builder.Property(e => e.CorrelationId).HasMaxLength(AuditColumns.CorrelationIdMaxLength).IsRequired();

        // Unbounded: the application chooses what goes in it, and a length limit here would be a
        // second, invisible one on top of whatever the application already validates.
        builder.Property(e => e.Metadata);

        // Every timestamp this library writes is UTC, and this converter makes the database agree on
        // the way in and the way out.
        //
        // On the way in it matters for PostgreSQL, whose `timestamp with time zone` rejects a
        // DateTime that is not Kind.Utc. On the way out it matters for SQLite and SQL Server, which
        // both return Kind.Unspecified — a value that then serializes to JSON without a "Z" and is
        // read by every client as local time, which is an hour or eight wrong. In an audit trail
        // that is not cosmetic: it moves when a change is recorded to have happened.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        builder.Property(e => e.OccurredAt).HasConversion(utc).IsRequired();

        // The property rows. Cascade is the one cascade in the schema and it is deliberate: a
        // property change with no entry is unreadable, so it goes when the entry goes — which is
        // the only time anything deletes an entry at all.
        builder.HasMany(e => e.Changes)
            .WithOne()
            .HasForeignKey(c => c.AuditEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        // The main read: one entity's history, newest first. Leading with the address and ending on
        // the sort lets one index serve the filter and the ordering together.
        builder.HasIndex(e => new { e.EntityName, e.EntityId, e.OccurredAt })
            .HasDatabaseName($"IX_{options.EntriesTable}_Entity_OccurredAt");

        // "What did this person change, and when" — and the actor half of the erasure path.
        builder.HasIndex(e => new { e.Actor, e.OccurredAt })
            .HasDatabaseName($"IX_{options.EntriesTable}_Actor_OccurredAt");

        // Backs the prune, which scans by age across every entity. Without it the first prune of a
        // year-old trail is a table scan under a delete.
        builder.HasIndex(e => e.OccurredAt)
            .HasDatabaseName($"IX_{options.EntriesTable}_OccurredAt");

        // "The other halves of this edit", and the join to the traces of the request that caused it.
        builder.HasIndex(e => e.CorrelationId)
            .HasDatabaseName($"IX_{options.EntriesTable}_CorrelationId");
    }
}

/// <summary>Entity mapping for <see cref="AuditPropertyChange"/>.</summary>
/// <param name="options">Table naming.</param>
public sealed class AuditPropertyChangeConfiguration(AuditingModelOptions options)
    : IEntityTypeConfiguration<AuditPropertyChange>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<AuditPropertyChange> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.ToTable(options.ChangesTable, options.Schema);

        // One row per property per entry is already unique, so the natural key is the whole of it:
        // no surrogate, and no sequence for every writer to contend on.
        builder.HasKey(c => new { c.AuditEntryId, c.PropertyName }).HasName("PK_" + options.ChangesTable);

        builder.Property(c => c.PropertyName).HasMaxLength(AuditColumns.PropertyNameMaxLength).IsRequired();

        // Unbounded in the database, bounded by the engine. AuditingOptions.MaxValueLength is what
        // decides how much of a value is kept, and it is configurable per application — pinning a
        // column width here would turn a setting into a migration.
        builder.Property(c => c.OldValue);
        builder.Property(c => c.NewValue);

        builder.Property(c => c.IsRedacted).IsRequired();
        builder.Property(c => c.IsTruncated).IsRequired();

        if (options.IndexPropertyName)
        {
            builder.HasIndex(c => c.PropertyName)
                .HasDatabaseName($"IX_{options.ChangesTable}_PropertyName");
        }
    }
}

/// <summary>Model-building entry points.</summary>
public static class AuditingModelBuilderExtensions
{
    /// <summary>
    /// Adds the <c>AuditEntries</c> and <c>AuditPropertyChanges</c> tables to the model.
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    ///     modelBuilder.ApplyAuditing();
    /// }
    /// </code>
    /// <para>
    /// Nothing about your own entities changes, and nothing is added to them. An entry points AT an
    /// entity by name and key, with deliberately no foreign key back — which is what lets two
    /// tables serve every entity in the application, and what makes auditing something new cost no
    /// migration.
    /// </para>
    /// <para>
    /// Here the missing foreign key is a feature rather than a price: nothing cascades to the
    /// trail, so deleting an entity leaves its history standing. The <c>Deleted</c> entry is the
    /// one people come looking for.
    /// </para>
    /// </summary>
    /// <param name="modelBuilder">The model being built.</param>
    /// <param name="configure">Table naming and indexing. Optional.</param>
    /// <returns>The model builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelBuilder"/> is <c>null</c>.</exception>
    public static ModelBuilder ApplyAuditing(
        this ModelBuilder modelBuilder,
        Action<AuditingModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var options = new AuditingModelOptions();
        configure?.Invoke(options);

        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration(options));
        modelBuilder.ApplyConfiguration(new AuditPropertyChangeConfiguration(options));
        return modelBuilder;
    }
}
