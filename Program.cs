using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Data.Seed;
using ServiceTaskPlanningSystem.Services.Schedule;
using ServiceTaskPlanningSystem.Services.Plans;
using ServiceTaskPlanningSystem.Configuration;
using ServiceTaskPlanningSystem.Middleware;
using ServiceTaskPlanningSystem.Infrastructure.Logging;

var builder = WebApplication.CreateBuilder(args);

var configuration = ConfigurationLoader.Build(builder.Environment.ContentRootPath, builder.Environment.EnvironmentName);
builder.Configuration.AddConfiguration(configuration);

var connectionString = ConfigurationLoader.GetRequiredConnectionString(configuration);

var traceFilterOptions = new TraceFilterOptions();
var traceLogStore = new TraceLogStore(traceFilterOptions);
builder.Services.AddSingleton(traceFilterOptions);
builder.Services.AddSingleton(traceLogStore);
builder.Logging.AddProvider(new TraceInMemoryLoggerProvider(traceLogStore, traceFilterOptions));

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
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<IScheduleProvider, ScheduleProvider>();
builder.Services.AddScoped<IPlanService, PlanService>();
builder.Services.AddScoped<ITaskEvaluationService, TaskEvaluationService>();
builder.Services.AddTransient<DatabaseSeeder>();
builder.Services.AddSingleton<IScheduleLayoutBuilder, ScheduleLayoutBuilder>();
builder.Services.AddHostedService<EvaluationRepairService>();

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

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

await app.RunAsync();
