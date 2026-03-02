using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;
using ServiceTaskPlanningSystem.Models.DTOs.Responses;
using Tests.Integration.Builders;
using Tests.Integration.Fixtures;
using Tests.Integration.Seeds;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using ServiceTaskPlanningSystem.Data;
using Microsoft.EntityFrameworkCore;

namespace Tests.Integration.Tasks;

/// <summary>
/// Integration tests for time slot conflict detection during task creation, update, and move operations.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TimeSlotConflictTests : IAsyncLifetime
{
    private readonly TestWebApplicationFactoryFixture _fixture;

    public TimeSlotConflictTests(TestWebApplicationFactoryFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await ResetAndReseedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Task Creation Conflicts

    [Fact]
    public async Task CreateTask_WithExactOverlap_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create first task 9:00-10:00
        await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Existing Task");

        // Act - Try to create task at exact same time slot
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 10 * 60, "Overlapping Task").Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateTask_WithPartialOverlapAtStart_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create first task 10:00-12:00
        await CreateTaskAsync(client, techId, targetDate, 10 * 60, 12 * 60, "Existing Task");

        // Act - Try to create task 9:00-11:00 (overlaps at start)
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 11 * 60, "Overlapping Task").Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateTask_WithPartialOverlapAtEnd_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create first task 10:00-12:00
        await CreateTaskAsync(client, techId, targetDate, 10 * 60, 12 * 60, "Existing Task");

        // Act - Try to create task 11:00-13:00 (overlaps at end)
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 11 * 60, 13 * 60, "Overlapping Task").Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateTask_FullyContainedInExisting_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create first task 9:00-13:00
        await CreateTaskAsync(client, techId, targetDate, 9 * 60, 13 * 60, "Existing Task");

        // Act - Try to create task 10:00-12:00 (fully contained)
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 10 * 60, 12 * 60, "Contained Task").Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateTask_FullyContainsExisting_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create first task 10:00-12:00
        await CreateTaskAsync(client, techId, targetDate, 10 * 60, 12 * 60, "Existing Task");

        // Act - Try to create task 9:00-13:00 (fully contains existing)
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 9 * 60, 13 * 60, "Containing Task").Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateTask_AdjacentSlots_NoConflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create first task 9:00-10:00
        await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "First Task");

        // Act - Create task immediately after: 10:00-11:00 (adjacent, no overlap)
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, 10 * 60, 11 * 60, "Adjacent Task").Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTask_DifferentTechnician_NoConflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create task for first technician 9:00-10:00
        await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Tech1 Task");

        // Act - Create task at same time for different technician
        var dto = TestTaskRequestBuilder.Create(otherTechId, targetDate, 9 * 60, 10 * 60, "Tech2 Task").Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTask_DifferentDate_NoConflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate1 = GetFutureDate(14);
        var targetDate2 = GetFutureDate(15);

        // Create task for first date 9:00-10:00
        await CreateTaskAsync(client, techId, targetDate1, 9 * 60, 10 * 60, "Day1 Task");

        // Act - Create task at same time but different date
        var dto2 = TestTaskRequestBuilder.Create(techId, targetDate2, 9 * 60, 10 * 60, "Day2 Task").Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto2);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Task Move Conflicts

    [Fact]
    public async Task MoveTask_ToOccupiedSlot_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create two tasks: 9:00-10:00 and 11:00-12:00
        var task1 = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Task 1");
        await CreateTaskAsync(client, techId, targetDate, 11 * 60, 12 * 60, "Task 2");

        var version = await GetTaskVersionAsync(task1.Id);

        // Act - Try to move Task 1 to overlap with Task 2 (move to 11:00)
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task1.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = techId,
            TargetDate = targetDate,
            TargetSlotStartMinutes = 11 * 60,
            Version = version,
            TimezoneOffsetMinutes = 0
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task MoveTask_ToFreeSlot_Succeeds()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create task at 9:00-10:00
        var task = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Movable Task");
        var version = await GetTaskVersionAsync(task.Id);

        // Act - Move task to 14:00 (free slot)
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = techId,
            TargetDate = targetDate,
            TargetSlotStartMinutes = 14 * 60,
            Version = version,
            TimezoneOffsetMinutes = 0
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MoveTask_ToDifferentTechnicianOccupiedSlot_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create task for tech1 at 9:00-10:00
        var task = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Tech1 Task");
        // Create task for tech2 at 11:00-12:00
        await CreateTaskAsync(client, otherTechId, targetDate, 11 * 60, 12 * 60, "Tech2 Task");

        var version = await GetTaskVersionAsync(task.Id);

        // Act - Try to move tech1's task to tech2's occupied slot
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = otherTechId,
            TargetDate = targetDate,
            TargetSlotStartMinutes = 11 * 60,
            Version = version,
            TimezoneOffsetMinutes = 0
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task MoveTask_ToDifferentTechnicianFreeSlot_Succeeds()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create task for tech1 at 9:00-10:00
        var task = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Tech1 Task");
        var version = await GetTaskVersionAsync(task.Id);

        // Act - Move tech1's task to tech2 at free slot
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task.Id}/move", new MoveTaskRequestDto
        {
            TargetTechnicianId = otherTechId,
            TargetDate = targetDate,
            TargetSlotStartMinutes = 14 * 60,
            Version = version,
            TimezoneOffsetMinutes = 0
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SwapTask_SameTechnician_FreeSwap_Succeeds()
    {
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        var first = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Task A");
        var second = await CreateTaskAsync(client, techId, targetDate, 11 * 60, 12 * 60, "Task B");
        var firstVersion = await GetTaskVersionAsync(first.Id);
        var secondVersion = await GetTaskVersionAsync(second.Id);

        var response = await client.PatchAsJsonAsync($"/api/tasks/{first.Id}/swap", new SwapTaskRequestDto
        {
            TargetTaskId = second.Id,
            SourceVersion = firstVersion,
            TargetVersion = secondVersion,
            TimezoneOffsetMinutes = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SwapTask_DifferentTechnician_Returns422()
    {
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var otherTechId = await GetTechnicianIdAsync("austeja.j@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        var first = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Task A");
        var second = await CreateTaskAsync(client, otherTechId, targetDate, 11 * 60, 12 * 60, "Task B");
        var firstVersion = await GetTaskVersionAsync(first.Id);
        var secondVersion = await GetTaskVersionAsync(second.Id);

        var response = await client.PatchAsJsonAsync($"/api/tasks/{first.Id}/swap", new SwapTaskRequestDto
        {
            TargetTaskId = second.Id,
            SourceVersion = firstVersion,
            TargetVersion = secondVersion,
            TimezoneOffsetMinutes = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task SwapTask_WithMiddleGap_CompactsLaterTask()
    {
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        var first = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Swap Source");
        var second = await CreateTaskAsync(client, techId, targetDate, 11 * 60, 12 * 60, "Swap Target");
        var third = await CreateTaskAsync(client, techId, targetDate, 15 * 60, 16 * 60, "Tail Task");
        await EnsureTravelRouteFromWorkplaceToTaskAsync(techId, first.Id, minutes: 1);
        var firstVersion = await GetTaskVersionAsync(first.Id);
        var secondVersion = await GetTaskVersionAsync(second.Id);

        var response = await client.PatchAsJsonAsync($"/api/tasks/{first.Id}/swap", new SwapTaskRequestDto
        {
            TargetTaskId = second.Id,
            SourceVersion = firstVersion,
            TargetVersion = secondVersion,
            TimezoneOffsetMinutes = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var thirdStartUtc = await GetTaskStartUtcAsync(third.Id);
        thirdStartUtc.TimeOfDay.Should().Be(TimeSpan.FromHours(12));
    }

    #endregion

    #region Task Update Conflicts

    [Fact]
    public async Task UpdateTask_ToOverlapWithAnother_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create two tasks: 9:00-10:00 and 12:00-13:00
        var task1 = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Task 1");
        await CreateTaskAsync(client, techId, targetDate, 12 * 60, 13 * 60, "Task 2");

        var version = await GetTaskVersionAsync(task1.Id);
        var startAt = DateTime.Parse(targetDate).AddMinutes(11 * 60); // 11:00
        var endAt = DateTime.Parse(targetDate).AddMinutes(13 * 60);   // 13:00 - overlaps with Task 2

        // Act - Try to update Task 1 to overlap with Task 2
        var updateDto = TestUpdateTaskRequestBuilder.Create(techId, startAt, endAt, "Updated Task 1", version).Build();
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task1.Id}", updateDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateTask_WithNoOverlap_Succeeds()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create task at 9:00-10:00
        var task = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Original Task");
        var version = await GetTaskVersionAsync(task.Id);
        var startAt = DateTime.Parse(targetDate).AddMinutes(14 * 60); // 14:00
        var endAt = DateTime.Parse(targetDate).AddMinutes(15 * 60);   // 15:00

        // Act - Update task to a free slot
        var updateDto = TestUpdateTaskRequestBuilder.Create(techId, startAt, endAt, "Updated Task", version).Build();
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task.Id}", updateDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateTask_ExtendingIntoOccupiedSlot_Returns409Conflict()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);

        // Create two consecutive tasks: 9:00-10:00 and 10:00-11:00
        var task1 = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Task 1");
        await CreateTaskAsync(client, techId, targetDate, 10 * 60, 11 * 60, "Task 2");

        var version = await GetTaskVersionAsync(task1.Id);
        var startAt = DateTime.Parse(targetDate).AddMinutes(9 * 60);   // 9:00
        var endAt = DateTime.Parse(targetDate).AddMinutes(10 * 60 + 30); // 10:30 - extends into Task 2

        // Act - Try to extend Task 1 into Task 2's slot
        var updateDto = TestUpdateTaskRequestBuilder.Create(techId, startAt, endAt, "Extended Task 1", version).Build();
        var response = await client.PatchAsJsonAsync($"/api/tasks/{task1.Id}", updateDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    #endregion

    #region Drag Preview with Conflicts

    [Fact]
    public async Task DragPreview_ReturnsHasConflict_WhenDroppingOntoOccupiedSlot()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);
        var parsedDate = DateTime.Parse(targetDate);

        // Create two tasks: one to drag (9:00-10:00) and one at target location (11:00-12:00)
        var taskToDrag = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Task to Drag");
        await CreateTaskAsync(client, techId, targetDate, 11 * 60, 12 * 60, "Blocking Task");

        // Act - Preview dragging task to overlap with the blocking task (10:30-11:30)
        var response = await client.PostAsJsonAsync("/api/tasks/preview/drag", new DragPreviewRequestDto
        {
            TaskId = taskToDrag.Id,
            TargetTechnicianId = techId,
            TargetStartAt = DateTime.SpecifyKind(parsedDate.AddMinutes(10 * 60 + 30), DateTimeKind.Utc), // 10:30
            TargetEndAt = DateTime.SpecifyKind(parsedDate.AddMinutes(11 * 60 + 30), DateTimeKind.Utc)    // 11:30
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<DragPreviewResponseDto>();
        preview.Should().NotBeNull();
        preview!.PreviewStates.Should().NotBeEmpty();

        var techState = preview.PreviewStates.FirstOrDefault(s => s.TechnicianId == techId);
        techState.Should().NotBeNull();
        techState!.HasConflict.Should().BeTrue();
        techState.LoadState.Should().Be("Conflict");
    }

    [Fact]
    public async Task DragPreview_ReturnsNoConflict_WhenDroppingOntoFreeSlot()
    {
        // Arrange
        var techId = await GetTechnicianIdAsync("technician@test.com");
        var client = CreateClient("manager@test.com");
        var targetDate = GetFutureDate(14);
        var parsedDate = DateTime.Parse(targetDate);

        // Create a task to drag (9:00-10:00)
        var taskToDrag = await CreateTaskAsync(client, techId, targetDate, 9 * 60, 10 * 60, "Task to Drag");

        // Act - Preview dragging task to a free slot (14:00-15:00)
        var response = await client.PostAsJsonAsync("/api/tasks/preview/drag", new DragPreviewRequestDto
        {
            TaskId = taskToDrag.Id,
            TargetTechnicianId = techId,
            TargetStartAt = DateTime.SpecifyKind(parsedDate.AddMinutes(14 * 60), DateTimeKind.Utc), // 14:00
            TargetEndAt = DateTime.SpecifyKind(parsedDate.AddMinutes(15 * 60), DateTimeKind.Utc)   // 15:00
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await response.Content.ReadFromJsonAsync<DragPreviewResponseDto>();
        preview.Should().NotBeNull();
        preview!.PreviewStates.Should().NotBeEmpty();

        var techState = preview.PreviewStates.FirstOrDefault(s => s.TechnicianId == techId);
        techState.Should().NotBeNull();
        techState!.HasConflict.Should().BeFalse();
    }

    #endregion

    #region Helper Methods

    private HttpClient CreateClient(string email, string role = "manager")
    {
        var client = _fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-Email", email);
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    private async Task ResetAndReseedAsync()
    {
        await _fixture.Respawn.ResetAsync(_fixture.Postgres.ConnectionString);
        using var scope = _fixture.Factory.Services.CreateScope();
        await SeedData.EnsureSeedDataAsync(scope);
    }

    private static string GetFutureDate(int daysAhead)
    {
        return DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(daysAhead)).ToString("yyyy-MM-dd");
    }

    private async Task<long> GetTechnicianIdAsync(string email)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Technicians
            .Include(t => t.User)
            .Where(t => t.User != null && t.User.Email == email)
            .Select(t => t.Id)
            .FirstAsync();
    }

    private async Task<TaskCreatedOrUpdatedResponseDto> CreateTaskAsync(HttpClient client, long techId, string targetDate, int startMinutes, int endMinutes, string title)
    {
        var dto = TestTaskRequestBuilder.Create(techId, targetDate, startMinutes, endMinutes, title).Build();
        var response = await client.PostAsJsonAsync("/api/tasks", dto);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskCreatedOrUpdatedResponseDto>())!;
    }

    private async Task<uint> GetTaskVersionAsync(long taskId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => EF.Property<uint>(t, "xmin"))
            .FirstAsync();
    }

    private async Task EnsureTravelRouteFromWorkplaceToTaskAsync(long technicianId, long taskId, int minutes)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();

        var workplaceAddressId = await db.Technicians
            .Where(t => t.Id == technicianId)
            .Select(t => t.WorkplaceAddressId)
            .FirstAsync();

        var taskAddressId = await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => t.ServiceObject.AddressId)
            .FirstAsync();

        var existing = await db.TravelRoutes
            .FirstOrDefaultAsync(r => r.FromAddressId == workplaceAddressId && r.ToAddressId == taskAddressId);
        if (existing == null)
        {
            db.TravelRoutes.Add(new TravelRouteEntity
            {
                FromAddressId = workplaceAddressId,
                ToAddressId = taskAddressId,
                DistanceKm = 1m,
                DurationMinutes = minutes,
                CalculatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.DurationMinutes = minutes;
            existing.DistanceKm = 1m;
            existing.CalculatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private async Task<DateTime> GetTaskStartUtcAsync(long taskId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        return await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => t.StartAt)
            .FirstAsync();
    }

    #endregion
}
