using Microsoft.EntityFrameworkCore;

namespace ServiceTaskPlanningSystem.Data
{
    public class ScheduleDbContext : DbContext
    {
        public ScheduleDbContext(DbContextOptions<ScheduleDbContext> options)
            : base(options)
        {
        }

        public DbSet<UserEntity> Users => Set<UserEntity>();
        public DbSet<TechnicianEntity> Technicians => Set<TechnicianEntity>();
        public DbSet<PlanEntity> Plans => Set<PlanEntity>();
        public DbSet<ServiceObjectEntity> ServiceObjects => Set<ServiceObjectEntity>();
        public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
        public DbSet<TaskEvaluationEntity> TaskEvaluations => Set<TaskEvaluationEntity>();
        public DbSet<AddressEntity> Addresses => Set<AddressEntity>();
        public DbSet<TravelRouteEntity> TravelRoutes => Set<TravelRouteEntity>();
        public DbSet<TaskActionLogEntity> TaskActionLogs => Set<TaskActionLogEntity>();
        public DbSet<BacklogItemEntity> BacklogItems => Set<BacklogItemEntity>();
        public DbSet<TechnicianUnavailabilityEntity> TechnicianUnavailabilities => Set<TechnicianUnavailabilityEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyUtcDateTimeConverter();
            modelBuilder.HasPostgresExtension("citext");
            modelBuilder.HasPostgresEnum<UserRole>();
            modelBuilder.HasPostgresEnum<PlanStatus>();
            modelBuilder.HasPostgresEnum<TaskPriority>();
            modelBuilder.HasPostgresEnum<ScheduleTaskStatus>("task_status");
            modelBuilder.HasPostgresEnum<RiskLevel>();
            modelBuilder.HasPostgresEnum<LoadState>();
            modelBuilder.HasPostgresEnum<BacklogItemType>();
            ConfigureAddresses(modelBuilder);
            ConfigureUsers(modelBuilder);
            ConfigureTechnicians(modelBuilder);
            ConfigurePlans(modelBuilder);
            ConfigureServiceObjects(modelBuilder);
            ConfigureTasks(modelBuilder);
            ConfigureTaskEvaluations(modelBuilder);
            ConfigureTravelRoutes(modelBuilder);
            ConfigureTaskActionLogs(modelBuilder);
            ConfigureBacklogItems(modelBuilder);
            ConfigureTechnicianUnavailabilities(modelBuilder);
        }

        private static void ConfigureAddresses(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<AddressEntity>();
            entity.ToTable("addresses");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Street)
                .IsRequired()
                .HasMaxLength(300);
            entity.Property(e => e.City)
                .IsRequired()
                .HasMaxLength(150);
            entity.Property(e => e.FullAddress)
                .IsRequired()
                .HasMaxLength(500);
            entity.Property(e => e.Latitude)
                .HasColumnType("numeric(9,6)");
            entity.Property(e => e.Longitude)
                .HasColumnType("numeric(9,6)");
            entity.Property(e => e.SnappedLatitude)
                .HasColumnName("snapped_latitude")
                .HasColumnType("numeric(9,6)");
            entity.Property(e => e.SnappedLongitude)
                .HasColumnName("snapped_longitude")
                .HasColumnType("numeric(9,6)");
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");
            entity.HasIndex(e => new { e.City, e.Street })
                .IsUnique();
        }

        private static void ConfigureUsers(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<UserEntity>();
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name)
                .IsRequired();
            entity.Property(e => e.Email)
                .IsRequired()
                .HasColumnType("citext");
            entity.Property(e => e.Role)
                .HasColumnType("user_role")
                .IsRequired();
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true);
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");
            entity.HasIndex(e => e.Email)
                .IsUnique();
        }

        private static void ConfigureTechnicians(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<TechnicianEntity>();
            entity.ToTable("technicians", table =>
            {
                table.HasCheckConstraint("ck_technicians_daily_limit_positive", "daily_limit > 0");
                table.HasCheckConstraint("ck_technicians_weekly_limit_positive", "weekly_limit > 0");
            });
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.WorkplaceAddress)
                .WithMany(a => a.Technicians)
                .HasForeignKey(e => e.WorkplaceAddressId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(e => e.DailyLimit)
                .HasColumnType("numeric(5,2)")
                .HasDefaultValue(8)
                .IsRequired();
            entity.Property(e => e.WeeklyLimit)
                .HasColumnType("numeric(6,2)")
                .HasDefaultValue(40)
                .IsRequired();
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        }

        private static void ConfigurePlans(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<PlanEntity>();
            entity.ToTable("plans", table =>
            {
                table.HasCheckConstraint("ck_plans_period", "period_end >= period_start");
                table.HasCheckConstraint("ck_plans_weeks_positive", "weeks > 0");
            });
            entity.HasKey(e => e.Id);

            entity.Property(e => e.PeriodStart)
                .HasColumnType("date")
                .IsRequired();
            entity.Property(e => e.PeriodEnd)
                .HasColumnType("date")
                .IsRequired();
            entity.Property(e => e.Weeks)
                .HasDefaultValue(1);
            entity.Property(e => e.Status)
                .HasColumnType("plan_status")
                .IsRequired();
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");

            entity.HasOne(e => e.CreatedByUser)
                .WithMany()
                .HasForeignKey(e => e.CreatedBy)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.SupersededByPlan)
                .WithMany(p => p.SupersededPlans)
                .HasForeignKey(e => e.SupersededByPlanId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.Status)
                .HasDatabaseName("ix_plans_status_draft")
                .HasFilter("status = 'draft'")
                .IsUnique();
        }

        private static void ConfigureServiceObjects(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<ServiceObjectEntity>();
            entity.ToTable("service_objects");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(500);
            entity.HasOne(e => e.Address)
                .WithMany(a => a.ServiceObjects)
                .HasForeignKey(e => e.AddressId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.Description)
                .HasMaxLength(1000);
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");

            entity.HasIndex(e => new { e.Name, e.AddressId });
        }

        private static void ConfigureTasks(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<TaskEntity>();
            entity.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
            entity.ToTable("tasks", table =>
            {
                table.HasCheckConstraint("ck_tasks_time_range", "end_at > start_at");
            });
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Title)
                .IsRequired();
            entity.Property(e => e.Priority)
                .HasColumnType("task_priority")
                .HasDefaultValueSql("'medium'::task_priority")
                .HasSentinel((TaskPriority)(-1))
                .IsRequired();
            entity.Property(e => e.Status)
                .HasColumnType("task_status")
                .HasDefaultValueSql("'in_progress'::task_status")
                .HasSentinel((ScheduleTaskStatus)(-1))
                .IsRequired();
            entity.Property(e => e.StartAt)
                .HasColumnType("timestamptz")
                .IsRequired();
            entity.Property(e => e.EndAt)
                .HasColumnType("timestamptz")
                .IsRequired();
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");
            entity.Property(e => e.SkillLevel)
                .HasMaxLength(100)
                .HasDefaultValue(string.Empty);
            entity.Property(e => e.Instructions)
                .HasMaxLength(2000)
                .HasDefaultValue(string.Empty);
            entity.Property(e => e.SourceTaskId)
                .HasColumnName("source_task_id")
                .IsRequired(false);

            entity.HasOne(e => e.Plan)
                .WithMany(p => p.Tasks)
                .HasForeignKey(e => e.PlanId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Technician)
                .WithMany(t => t.Tasks)
                .HasForeignKey(e => e.TechnicianId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ServiceObject)
                .WithMany(o => o.Tasks)
                .HasForeignKey(e => e.ServiceObjectId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.PlanId, e.TechnicianId, e.StartAt });
            entity.HasIndex(e => new { e.PlanId, e.StartAt });
            entity.HasIndex(e => new { e.TechnicianId, e.StartAt });
            entity.HasIndex(e => new { e.ServiceObjectId, e.StartAt });
            entity.HasIndex(e => e.SourceTaskId)
                .HasDatabaseName("ix_tasks_source_task_id")
                .HasFilter("source_task_id IS NOT NULL");
            // Partial index used exclusively by TaskAutoExpirationService.
            // Only covers InProgress rows — stays small as tasks expire and never revert.
            entity.HasIndex(e => e.EndAt)
                .HasFilter("status = 'in_progress'")
                .HasDatabaseName("ix_tasks_inprogress_end_at");

            entity.Property(e => e.SourceUnavailabilityId)
                .HasColumnName("source_unavailability_id")
                .IsRequired(false);
            entity.Property(e => e.OriginalStartAt)
                .HasColumnName("original_start_at")
                .HasColumnType("timestamptz")
                .IsRequired(false);
            entity.Property(e => e.OriginalEndAt)
                .HasColumnName("original_end_at")
                .HasColumnType("timestamptz")
                .IsRequired(false);
            // Partial index — only rows currently linked to an active unavailability have a value.
            entity.HasIndex(e => e.SourceUnavailabilityId)
                .HasDatabaseName("ix_tasks_source_unavailability_id")
                .HasFilter("source_unavailability_id IS NOT NULL");
        }

        private static void ConfigureTaskEvaluations(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<TaskEvaluationEntity>();
            entity.ToTable("task_evaluations");
            entity.HasKey(e => e.TaskId);

            entity.Property(e => e.Status)
                .HasColumnType("load_state");

            entity.HasOne(e => e.Task)
                .WithOne(t => t.Evaluation)
                .HasForeignKey<TaskEvaluationEntity>(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        }

        private static void ConfigureTravelRoutes(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<TravelRouteEntity>();
            entity.ToTable("travel_routes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DistanceKm)
                .HasColumnType("numeric(8,2)")
                .IsRequired();
            entity.Property(e => e.DurationMinutes)
                .IsRequired();
            entity.Property(e => e.CalculatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");

            entity.HasOne(e => e.FromAddress)
                .WithMany()
                .HasForeignKey(e => e.FromAddressId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ToAddress)
                .WithMany()
                .HasForeignKey(e => e.ToAddressId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.FromAddressId, e.ToAddressId })
                .IsUnique();
        }

        private static void ConfigureBacklogItems(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<BacklogItemEntity>();
            entity.ToTable("backlog_items");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title)
                .IsRequired()
                .HasMaxLength(200);
            entity.Property(e => e.SourceUnavailabilityId)
                .HasColumnName("source_unavailability_id")
                .IsRequired(false);
            entity.Property(e => e.OriginalTechnicianId)
                .HasColumnName("original_technician_id")
                .IsRequired(false);
            entity.Property(e => e.OriginalStartAt)
                .HasColumnName("original_start_at")
                .HasColumnType("timestamptz")
                .IsRequired(false);
            entity.Property(e => e.OriginalEndAt)
                .HasColumnName("original_end_at")
                .HasColumnType("timestamptz")
                .IsRequired(false);
            entity.Property(e => e.SkillLevel)
                .HasMaxLength(100)
                .HasDefaultValue(string.Empty);
            entity.Property(e => e.ObjectName)
                .HasMaxLength(500)
                .HasDefaultValue(string.Empty);
            entity.Property(e => e.ObjectAddress)
                .HasMaxLength(500)
                .HasDefaultValue(string.Empty);
            entity.Property(e => e.Instructions)
                .HasMaxLength(2000)
                .HasDefaultValue(string.Empty);
            entity.Property(e => e.Priority)
                .HasColumnType("task_priority")
                .HasDefaultValueSql("'medium'::task_priority")
                .HasSentinel((TaskPriority)(-1))
                .IsRequired();
            entity.Property(e => e.ItemType)
                .HasColumnName("item_type")
                .HasDefaultValueSql("'temporary'::backlog_item_type")
                .HasSentinel((BacklogItemType)(-1));
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");
            entity.HasOne(e => e.CreatedBy)
                .WithMany()
                .HasForeignKey(e => e.CreatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.CreatedByUserId);
            entity.HasIndex(e => e.SourceUnavailabilityId);
        }

        private static void ConfigureTaskActionLogs(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<TaskActionLogEntity>();
            entity.ToTable("task_action_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActionType)
                .IsRequired()
                .HasMaxLength(50);
            entity.Property(e => e.Outcome)
                .IsRequired()
                .HasMaxLength(10)
                .HasDefaultValue("success");
            entity.Property(e => e.ActorName)
                .HasMaxLength(200)
                .HasDefaultValue(string.Empty);
            entity.Property(e => e.ActorRole)
                .HasMaxLength(50)
                .HasDefaultValue(string.Empty);
            entity.Property(e => e.Summary)
                .IsRequired()
                .HasMaxLength(500);
            entity.Property(e => e.DetailsJson)
                .HasColumnType("jsonb");
            entity.Property(e => e.Source)
                .HasMaxLength(20)
                .HasDefaultValue("user");
            entity.Property(e => e.OccurredAtUtc)
                .HasColumnType("timestamptz")
                .IsRequired();
            entity.HasIndex(e => new { e.PlanId, e.OccurredAtUtc });
            entity.HasIndex(e => new { e.TaskId, e.OccurredAtUtc });
        }

        private static void ConfigureTechnicianUnavailabilities(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<TechnicianUnavailabilityEntity>();
            entity.ToTable("technician_unavailabilities", table =>
            {
                table.HasCheckConstraint("ck_unavailability_period", "period_end >= period_start");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PeriodStart)
                .HasColumnType("timestamptz")
                .IsRequired();
            entity.Property(e => e.PeriodEnd)
                .HasColumnType("timestamptz")
                .IsRequired();
            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");
            entity.HasOne(e => e.Technician)
                .WithMany()
                .HasForeignKey(e => e.TechnicianId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.TechnicianId, e.PeriodStart })
                .HasDatabaseName("ix_unavailability_tech_period");
        }
    }
}
