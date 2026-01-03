using Microsoft.EntityFrameworkCore;
using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Data.Seed;
using ServiceTaskPlanningSystem.Services.Schedule;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString) ||
    connectionString.Contains("Use env var", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("DefaultConnection is not configured. Set ConnectionStrings__DefaultConnection in environment variables.");
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
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<IScheduleProvider, ScheduleProvider>();
builder.Services.AddTransient<DatabaseSeeder>();
builder.Services.AddSingleton<IScheduleLayoutBuilder, ScheduleLayoutBuilder>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
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
