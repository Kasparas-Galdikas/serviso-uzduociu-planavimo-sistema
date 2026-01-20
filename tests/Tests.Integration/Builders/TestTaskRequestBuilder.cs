using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;

namespace Tests.Integration.Builders;

/// <summary>
/// Builder for CreateTaskRequestDto with sensible defaults for integration tests.
/// When new required fields are added to the DTO, update the defaults here only.
/// </summary>
public class TestTaskRequestBuilder
{
    private int _technicianId;
    private string _targetDate = string.Empty;
    private int _startMinutes;
    private int _endMinutes;
    private string _title = string.Empty;

    // Defaults for optional/required fields - UPDATE HERE when new fields are added
    private string _location = "HQ";
    private TaskPriority _priority = TaskPriority.Medium;
    private ScheduleTaskStatus _status = ScheduleTaskStatus.Planned;
    private int _timezoneOffsetMinutes = 0;
    private string _skillLevel = "Junior";
    private string _objectName = "Test Object";
    private string _objectAddress = "Test Address 1";
    private string? _instructions = null;

    private TestTaskRequestBuilder() { }

    /// <summary>
    /// Creates a new builder with required parameters.
    /// </summary>
    public static TestTaskRequestBuilder Create(long technicianId, string targetDate, int startMinutes, int endMinutes, string title)
    {
        return new TestTaskRequestBuilder
        {
            _technicianId = (int)technicianId,
            _targetDate = targetDate,
            _startMinutes = startMinutes,
            _endMinutes = endMinutes,
            _title = title
        };
    }

    /// <summary>
    /// Creates a new builder with required parameters using DateTime for the date.
    /// </summary>
    public static TestTaskRequestBuilder Create(long technicianId, DateTime targetDate, int startMinutes, int endMinutes, string title)
    {
        return Create(technicianId, DateOnly.FromDateTime(targetDate.Date).ToString("yyyy-MM-dd"), startMinutes, endMinutes, title);
    }

    public TestTaskRequestBuilder WithLocation(string location)
    {
        _location = location;
        return this;
    }

    public TestTaskRequestBuilder WithPriority(TaskPriority priority)
    {
        _priority = priority;
        return this;
    }

    public TestTaskRequestBuilder WithStatus(ScheduleTaskStatus status)
    {
        _status = status;
        return this;
    }

    public TestTaskRequestBuilder WithTimezone(int timezoneOffsetMinutes)
    {
        _timezoneOffsetMinutes = timezoneOffsetMinutes;
        return this;
    }

    public TestTaskRequestBuilder WithSkillLevel(string skillLevel)
    {
        _skillLevel = skillLevel;
        return this;
    }

    public TestTaskRequestBuilder WithObjectName(string objectName)
    {
        _objectName = objectName;
        return this;
    }

    public TestTaskRequestBuilder WithObjectAddress(string objectAddress)
    {
        _objectAddress = objectAddress;
        return this;
    }

    public TestTaskRequestBuilder WithInstructions(string? instructions)
    {
        _instructions = instructions;
        return this;
    }

    /// <summary>
    /// Builds the CreateTaskRequestDto with all configured values.
    /// </summary>
    public CreateTaskRequestDto Build()
    {
        return new CreateTaskRequestDto
        {
            TechnicianId = _technicianId,
            TargetDate = _targetDate,
            StartMinutes = _startMinutes,
            EndMinutes = _endMinutes,
            Title = _title,
            Location = _location,
            Priority = _priority,
            Status = _status,
            TimezoneOffsetMinutes = _timezoneOffsetMinutes,
            SkillLevel = _skillLevel,
            ObjectName = _objectName,
            ObjectAddress = _objectAddress,
            Instructions = _instructions
        };
    }
}
