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
            ConfigureAddresses(modelBuilder);
            ConfigureUsers(modelBuilder);
            ConfigureTechnicians(modelBuilder);
            ConfigurePlans(modelBuilder);
            ConfigureServiceObjects(modelBuilder);
            ConfigureTasks(modelBuilder);
            ConfigureTaskEvaluations(modelBuilder);
            ConfigureTravelRoutes(modelBuilder);
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
                table.HasCheckConstraint("ck_technicians_travel_switch_non_negative", "travel_switch_limit >= 0");
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
            entity.Property(e => e.TravelSwitchLimit)
                .HasDefaultValue(0);
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
            entity.Property(e => e.PreviousTaskId);

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

            entity.HasOne(e => e.PreviousTask)
                .WithMany()
                .HasForeignKey(e => e.PreviousTaskId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.PlanId, e.TechnicianId, e.StartAt });
            entity.HasIndex(e => new { e.PlanId, e.StartAt });
            entity.HasIndex(e => new { e.TechnicianId, e.StartAt });
            entity.HasIndex(e => new { e.ServiceObjectId, e.StartAt });
        }

        private static void ConfigureTaskEvaluations(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<TaskEvaluationEntity>();
            entity.ToTable("task_evaluations");
            entity.HasKey(e => e.TaskId);

            entity.Property(e => e.RiskLevel)
                .HasColumnType("risk_level");
            entity.Property(e => e.EvaluatedAt)
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");
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
    }
}
