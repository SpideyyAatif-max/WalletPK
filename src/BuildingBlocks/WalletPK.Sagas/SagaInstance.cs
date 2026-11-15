using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace WalletPK.Sagas;

/// <summary>
/// One running (or finished) Saga: its state, its data as JSON, and when it next wants to wake up.
/// Table "saga_instances", one per service database.
/// </summary>
public sealed class SagaInstance
{
    public Guid Id { get; set; }

    /// <summary>Which definition drives it, e.g. "bank-transfer".</summary>
    public string SagaType { get; set; } = "";

    public string State { get; set; } = "";

    /// <summary>The definition's data, as JSON.</summary>
    public string Data { get; set; } = "{}";

    public bool IsCompleted { get; set; }

    /// <summary>When its timer fires (null: no timer). Also used as a lease while a timer step runs.</summary>
    public DateTimeOffset? WakeAt { get; set; }

    /// <summary>Why it wakes, e.g. "send" or "status-check".</summary>
    public string? WakeReason { get; set; }

    /// <summary>Goes up on every change; a timer step whose Saga changed meanwhile is thrown away.</summary>
    public int Version { get; set; }

    /// <summary>How many timeline steps it has (the next step's number).</summary>
    public int StepCount { get; set; }

    /// <summary>The request that started it; every message it sends carries this ID.</summary>
    public string? CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>One entry in a Saga's timeline (table "saga_steps"): what happened, and which state it led to.</summary>
public sealed class SagaStep
{
    public long Id { get; set; }

    public Guid SagaId { get; set; }

    /// <summary>1, 2, 3 ... within the Saga.</summary>
    public int Sequence { get; set; }

    /// <summary>The state after this step.</summary>
    public string State { get; set; } = "";

    /// <summary>The state before it (the same when nothing changed).</summary>
    public string? PreviousState { get; set; }

    /// <summary>What caused it: "start", a message ("HoldPlaced"), a callback, or "timer:send".</summary>
    public string Trigger { get; set; } = "";

    /// <summary>In words, for the timeline.</summary>
    public string Detail { get; set; } = "";

    public DateTimeOffset At { get; set; }
}

internal sealed class SagaInstanceConfiguration : IEntityTypeConfiguration<SagaInstance>
{
    public void Configure(EntityTypeBuilder<SagaInstance> builder)
    {
        // Column names are spelled out, so the engine's SQL works in any service's naming convention.
        builder.ToTable("saga_instances");
        builder.HasKey(saga => saga.Id);
        builder.Property(saga => saga.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(saga => saga.SagaType).HasColumnName("saga_type").HasMaxLength(64).IsRequired();
        builder.Property(saga => saga.State).HasColumnName("state").HasMaxLength(64).IsRequired();
        builder.Property(saga => saga.Data).HasColumnName("data").HasColumnType("jsonb").IsRequired();
        builder.Property(saga => saga.IsCompleted).HasColumnName("is_completed");
        builder.Property(saga => saga.WakeAt).HasColumnName("wake_at");
        builder.Property(saga => saga.WakeReason).HasColumnName("wake_reason").HasMaxLength(64);
        builder.Property(saga => saga.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(saga => saga.StepCount).HasColumnName("step_count");
        builder.Property(saga => saga.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
        builder.Property(saga => saga.CreatedAt).HasColumnName("created_at");
        builder.Property(saga => saga.UpdatedAt).HasColumnName("updated_at");
        builder.Property(saga => saga.CompletedAt).HasColumnName("completed_at");

        // The timer loop reads "due first"; only Sagas with a timer are in the index.
        builder.HasIndex(saga => saga.WakeAt).HasDatabaseName("ix_saga_instances_wake_at").HasFilter("wake_at IS NOT NULL AND NOT is_completed");
        builder.HasIndex(saga => new { saga.SagaType, saga.State }).HasDatabaseName("ix_saga_instances_type_state");
    }
}

internal sealed class SagaStepConfiguration : IEntityTypeConfiguration<SagaStep>
{
    public void Configure(EntityTypeBuilder<SagaStep> builder)
    {
        builder.ToTable("saga_steps");
        builder.HasKey(step => step.Id);
        builder.Property(step => step.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(step => step.SagaId).HasColumnName("saga_id");
        builder.Property(step => step.Sequence).HasColumnName("sequence");
        builder.Property(step => step.State).HasColumnName("state").HasMaxLength(64).IsRequired();
        builder.Property(step => step.PreviousState).HasColumnName("previous_state").HasMaxLength(64);
        builder.Property(step => step.Trigger).HasColumnName("trigger").HasMaxLength(100).IsRequired();
        builder.Property(step => step.Detail).HasColumnName("detail").HasMaxLength(1000).IsRequired();
        builder.Property(step => step.At).HasColumnName("at");
        builder.HasIndex(step => new { step.SagaId, step.Sequence }).IsUnique().HasDatabaseName("ux_saga_steps_saga_sequence");
        builder.HasOne<SagaInstance>().WithMany().HasForeignKey(step => step.SagaId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_saga_steps_saga_instances");
    }
}

public static class SagaModelBuilderExtensions
{
    /// <summary>Adds "saga_instances" and "saga_steps" to a service's model. Call it from OnModelCreating.</summary>
    public static ModelBuilder AddSagas(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        return modelBuilder.ApplyConfiguration(new SagaInstanceConfiguration()).ApplyConfiguration(new SagaStepConfiguration());
    }
}
