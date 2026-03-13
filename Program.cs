using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Data.Seed;
using ServiceTaskPlanningSystem.Services.Scheduling;
using ServiceTaskPlanningSystem.Services.PlanManagement;
using ServiceTaskPlanningSystem.Services.PlanManagement.Repair;
using ServiceTaskPlanningSystem.Services.Time;
using ServiceTaskPlanningSystem.Services.Authorization;
using ServiceTaskPlanningSystem.Services.Authentication;
using ServiceTaskPlanningSystem.Configuration;
using ServiceTaskPlanningSystem.Middleware;
using ServiceTaskPlanningSystem.Infrastructure.Logging;
using ServiceTaskPlanningSystem.Services.ExternalServices.ExternalApis;
using ServiceTaskPlanningSystem.Services.ExternalServices.Common;
using ServiceTaskPlanningSystem.Services.ExternalServices.RouteEvaluation;
using ServiceTaskPlanningSystem.Services.Travel;
using ServiceTaskPlanningSystem.Services.Travel.RouteChains;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var migrateOnly = args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase);

var configuration = ConfigurationLoader.Build(builder.Environment.ContentRootPath, builder.Environment.EnvironmentName);
builder.Configuration.AddConfiguration(configuration);

var rawConnectionString = ConfigurationLoader.GetRequiredConnectionString(configuration);
var csb = new NpgsqlConnectionStringBuilder(rawConnectionString);
if (!string.IsNullOrWhiteSpace(csb.Host) &&
    csb.Host.EndsWith(".proxy.rlwy.net", StringComparison.OrdinalIgnoreCase))
{
    // Railway proxy requires TLS; force safe defaults even if env var is set to Prefer.
    if (csb.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
    {
        csb.SslMode = SslMode.Require;
    }
}

var connectionString = csb.ConnectionString;
// SAFE logging (no password)
Console.WriteLine("=== DATABASE CONFIG CHECK ===");
Console.WriteLine($"Host: {csb.Host}");
Console.WriteLine($"Port: {csb.Port}");
Console.WriteLine($"Database: {csb.Database}");
Console.WriteLine($"Username: {csb.Username}");
Console.WriteLine($"SSL Mode: {csb.SslMode}");
Console.WriteLine("Password present: " + (!string.IsNullOrEmpty(csb.Password)));
Console.WriteLine("================================");
var traceFilterOptions = new TraceFilterOptions();
var traceLogStore = new TraceLogStore(traceFilterOptions);
builder.Services.AddSingleton(traceFilterOptions);
builder.Services.AddSingleton(traceLogStore);
builder.Logging.AddProvider(new TraceInMemoryLoggerProvider(traceLogStore, traceFilterOptions));

try
{
    using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    Console.WriteLine("✅ Database connection test SUCCESS");
}
catch (Exception ex)
{
    Console.WriteLine("❌ Database connection test FAILED");
    Console.WriteLine(ex.GetType().FullName);
    Console.WriteLine(ex.Message);
    throw; // keep failing fast
}

// Add services to the container.
builder.Services.AddDbContext<ScheduleDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.MapEnum<UserRole>();
        npgsqlOptions.MapEnum<PlanStatus>();
        npgsqlOptions.MapEnum<TaskPriority>();
        npgsqlOptions.MapEnum<ScheduleTaskStatus>("task_status");
        npgsqlOptions.MapEnum<RiskLevel>();
        npgsqlOptions.MapEnum<LoadState>();
    }).UseSnakeCaseNamingConvention());

// Configure global authentication requirement - all endpoints require authentication by default
builder.Services.AddControllersWithViews(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.Filters.Add(new AuthorizeFilter(policy));
});

builder.Services.AddScoped<IScheduleProvider, ScheduleProvider>();
builder.Services.AddScoped<IPlanService, PlanService>();
builder.Services.AddScoped<IDayScheduleFitCore, DayScheduleFitCore>();
builder.Services.AddScoped<IScheduleRepairService, ScheduleRepairService>();
builder.Services.AddScoped<ITaskTravelFeasibilityService, TaskTravelFeasibilityService>();
builder.Services.AddScoped<ITravelTimeResolver, TravelTimeResolver>();
builder.Services.AddScoped<IRouteChainBuilder, RouteChainBuilder>();
builder.Services.AddScoped<IEvaluationEngine, TaskEvaluationEngine>();
builder.Services.AddScoped<IPlanningCalendar, PlanningCalendar>();
builder.Services.AddScoped<ITaskPermissionService, TaskPermissionService>();
builder.Services.AddScoped<IScheduleTimeService, ScheduleTimeService>();
builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
builder.Services.AddScoped<IUserContextResolver, HttpUserContextResolver>();
builder.Services.AddScoped<ILoginService, LoginService>();
builder.Services.AddSingleton(new HeigitApiOptions());
builder.Services.AddSingleton<IExternalCallPolicy, ExternalCallPolicy>();
builder.Services.AddSingleton<IExternalCallKeyedLock, ExternalCallKeyedLock>();
builder.Services.AddScoped<IRouteEvaluationService, RouteEvaluationService>();
builder.Services.AddHttpClient<ILocationApiClient, LocationApiClient>();
builder.Services.AddHttpClient<IDistanceApiClient, DistanceApiClient>();

// Register strategy pattern implementations for plan-status-specific edit policies
builder.Services.AddScoped<ServiceTaskPlanningSystem.Services.Authorization.Interfaces.IPlanStatusEditPolicy, ServiceTaskPlanningSystem.Services.Authorization.Policies.DraftPlanEditPolicy>();
builder.Services.AddScoped<ServiceTaskPlanningSystem.Services.Authorization.Interfaces.IPlanStatusEditPolicy, ServiceTaskPlanningSystem.Services.Authorization.Policies.ActivePlanEditPolicy>();
builder.Services.AddScoped<ServiceTaskPlanningSystem.Services.Authorization.Interfaces.IPlanStatusEditPolicy, ServiceTaskPlanningSystem.Services.Authorization.Policies.SupersededPlanEditPolicy>();
builder.Services.AddTransient<DatabaseSeeder>();
builder.Services.AddScoped<IScheduleLayoutBuilder, ScheduleLayoutBuilder>();
builder.Services.AddHostedService<EvaluationRepairService>();
builder.Services.AddHostedService<TaskAutoExpirationService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Authentication/Index";
        options.LogoutPath = "/Authentication/Logout";
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(options =>
{
    // Single policy for all manager-only operations
    // Replaces: ManagerOnly, CanPublishPlan, CanEditTask (all were identical)
    options.AddPolicy("RequireManager", policy => policy.RequireRole("manager"));
    options.AddPolicy("CanEditTask", policy => policy.RequireRole("manager"));
    options.AddPolicy("CanPublishPlan", policy => policy.RequireRole("manager"));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseMiddleware<TraceMiddleware>();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

using (var scope = app.Services.CreateScope())
{
    try
    {
        Console.WriteLine("Starting database migration...");
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
        if (pendingMigrations.Any())
        {
            await db.Database.MigrateAsync();
            Console.WriteLine($"Database migration completed (applied {pendingMigrations.Count()} migrations)");
        }
        else
        {
            Console.WriteLine("Database migration skipped (no pending migrations)");
        }

        Console.WriteLine("Starting database seeding...");
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAsync();
        Console.WriteLine("Database seeding completed");
    }
    catch (Exception ex)
    {
        Console.WriteLine("DATABASE STARTUP FAILURE");
        Console.WriteLine(ex.GetType().FullName);
        Console.WriteLine(ex.Message);
        Console.WriteLine(ex.StackTrace);
        throw;
    }
}

if (migrateOnly)
{
    Console.WriteLine("Migration-only mode completed successfully.");
    return;
}

await app.RunAsync();




