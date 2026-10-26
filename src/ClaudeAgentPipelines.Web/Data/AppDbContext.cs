using ClaudeAgentPipelines.Billing;
using ClaudeAgentPipelines.Board;
using ClaudeAgentPipelines.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAgentPipelines.Data;

/// <summary>
/// The application's SQL Server database: pipelines and their agents, board projects, features and tasks, and finished runs.
/// Lists whose order matters (agents, tasks, steps...) get a "Position" column. The stores save an aggregate as a whole:
/// they delete its rows (children go with them) and insert the new graph.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    internal const string Position = "Position";

    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<AgentDefinition> Agents => Set<AgentDefinition>();
    public DbSet<AgentFile> AgentFiles => Set<AgentFile>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TeamAgent> TeamAgents => Set<TeamAgent>();
    public DbSet<Feature> Features => Set<Feature>();
    public DbSet<FeatureTask> FeatureTasks => Set<FeatureTask>();
    public DbSet<PipelineRun> Runs => Set<PipelineRun>();
    public DbSet<StepState> Steps => Set<StepState>();
    public DbSet<AppMeta> Meta => Set<AppMeta>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionMember> SubscriptionMembers => Set<SubscriptionMember>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<UsageCharge> UsageCharges => Set<UsageCharge>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums are stored as their names.
        builder.Properties<RunStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<StepStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<AgentRole>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<FeatureStage>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<FeatureStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<TaskState>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<TaskKind>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<SubscriptionStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<PaymentStatus>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Pipeline>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasMany(p => p.Agents).WithOne().HasForeignKey("PipelineId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AgentDefinition>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property<int>(Position);
            e.HasMany(a => a.Files).WithOne().HasForeignKey("AgentDefinitionId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<AgentFile>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property<int>(Position);
        });

        model.Entity<Project>(e =>
        {
            e.HasKey(p => p.Id);
            e.OwnsOne(p => p.Azure);
            e.OwnsOne(p => p.Orchestration);
            e.HasMany(p => p.Team).WithOne().HasForeignKey("ProjectId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<TeamAgent>(e =>
        {
            e.Property<string>("ProjectId");
            e.HasKey("ProjectId", nameof(TeamAgent.Role));
            e.Property<int>(Position);
        });

        model.Entity<Feature>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => new { f.ProjectId, f.Number }).IsUnique();
            e.HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(f => f.Tasks).WithOne().HasForeignKey("FeatureId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<FeatureTask>(e =>
        {
            e.HasKey(t => t.StepId);
            e.Property<int>(Position);
        });

        model.Entity<PipelineRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Ignore(r => r.FinalOutput);
            e.Ignore(r => r.IsFinished);
            e.Ignore(r => r.LastSeq);
            e.HasIndex(r => r.CompletedAt);
            e.HasMany(r => r.Steps).WithOne().HasForeignKey("RunId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<StepState>(e =>
        {
            e.Property<string>("RunId");
            e.HasKey("RunId", nameof(StepState.AgentId));
            e.Property<int>(Position);
        });

        model.Entity<AppMeta>().HasKey(m => m.Key);

        model.Entity<AppUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.UserName).HasMaxLength(64);
            e.Property(u => u.DisplayName).HasMaxLength(128);
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.Phone).HasMaxLength(20);
            e.HasIndex(u => u.UserName).IsUnique();
        });
        model.Entity<Subscription>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).HasMaxLength(128);
            e.HasOne<AppUser>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(s => s.Members).WithOne().HasForeignKey(m => m.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SubscriptionMember>(e =>
        {
            e.HasKey(m => m.UserId);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<Payment>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.CashfreeOrderId).HasMaxLength(64);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.CreatedByUserId).HasMaxLength(32);
            e.HasIndex(p => p.CashfreeOrderId).IsUnique();
            e.HasIndex(p => new { p.SubscriptionId, p.CreatedAt });
            e.HasOne<Subscription>().WithMany().HasForeignKey(p => p.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<UsageCharge>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.UserId).HasMaxLength(32);
            e.Property(c => c.UserName).HasMaxLength(128);
            e.Property(c => c.Amount).HasPrecision(18, 2);
            e.HasIndex(c => new { c.SubscriptionId, c.UserId, c.Date }).IsUnique();
            e.HasOne<Subscription>().WithMany().HasForeignKey(c => c.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

        // IDs are 32-character GUIDs: keys and foreign keys get a fixed length so they can be indexed.
        foreach (var property in model.Model.GetEntityTypes().SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(string) && (p.IsKey() || p.IsForeignKey())))
        {
            property.SetMaxLength(32);
        }
    }

    /// <summary>Marks a new pipeline, its agents and their files for insert, remembering their order.</summary>
    public void Stage(Pipeline pipeline)
    {
        Pipelines.Add(pipeline);
        SetPositions(pipeline.Agents);
        foreach (var agent in pipeline.Agents) SetPositions(agent.Files);
    }

    /// <summary>Marks a new project and its team for insert.</summary>
    public void Stage(Project project)
    {
        Projects.Add(project);
        SetPositions(project.Team);
    }

    /// <summary>Marks a new feature and its tasks for insert.</summary>
    public void Stage(Feature feature)
    {
        Features.Add(feature);
        SetPositions(feature.Tasks);
    }

    /// <summary>Marks a new run and its steps for insert.</summary>
    public void Stage(PipelineRun run)
    {
        Runs.Add(run);
        SetPositions(run.Steps);
    }

    /// <summary>
    /// Saves changes to an existing project. Its team is deleted and inserted again (the project row stays, so its
    /// features are kept).
    /// </summary>
    public async Task UpdateAsync(Project project, CancellationToken cancellationToken)
    {
        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        await TeamAgents.Where(t => EF.Property<string>(t, "ProjectId") == project.Id).ExecuteDeleteAsync(cancellationToken);

        Projects.Update(project);
        foreach (var agent in project.Team) Entry(agent).State = EntityState.Added;
        SetPositions(project.Team);

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void SetPositions<T>(List<T> items) where T : class
    {
        for (var i = 0; i < items.Count; i++) Entry(items[i]).Property(Position).CurrentValue = i;
    }

    // Read queries: untracked, with ordered children.

    public IQueryable<Pipeline> PipelinesWithAgents() => Pipelines.AsNoTracking()
        .Include(p => p.Agents.OrderBy(a => EF.Property<int>(a, Position)))
        .ThenInclude(a => a.Files.OrderBy(f => EF.Property<int>(f, Position)))
        .AsSplitQuery();

    public IQueryable<Project> ProjectsWithTeam() => Projects.AsNoTracking()
        .Include(p => p.Team.OrderBy(t => EF.Property<int>(t, Position)))
        .AsSplitQuery();

    public IQueryable<Feature> FeaturesWithTasks() => Features.AsNoTracking()
        .Include(f => f.Tasks.OrderBy(t => EF.Property<int>(t, Position)))
        .AsSplitQuery();

    public IQueryable<PipelineRun> RunsWithSteps() => Runs.AsNoTracking()
        .Include(r => r.Steps.OrderBy(s => EF.Property<int>(s, Position)))
        .AsSplitQuery();
}

/// <summary>Small key/value facts about the database, such as whether the old JSON files were imported.</summary>
public sealed class AppMeta
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
