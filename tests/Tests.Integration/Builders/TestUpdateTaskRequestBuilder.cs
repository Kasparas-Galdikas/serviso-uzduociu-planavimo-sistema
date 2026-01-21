using ServiceTaskPlanningSystem.Data;
using ServiceTaskPlanningSystem.Models.DTOs.Requests;

namespace Tests.Integration.Builders;

/// <summary>
/// Builder for UpdateTaskRequestDto with sensible defaults for integration tests.
/// When new required fields are added to the DTO, update the defaults here only.
/// </summary>
public class TestUpdateTaskRequestBuilder
{
    private long _technicianId;
    private DateTime _startAt;
    private DateTime _endAt;
    private string _title = string.Empty;
    private uint _version;

    // Defaults for optional/required fields - UPDATE HERE when new fields are added
    private string _location = "HQ";
    private TaskPriority _priority = TaskPriority.Medium;
    private ScheduleTaskStatus _status = ScheduleTaskStatus.InProgress;
    private int _timezoneOffsetMinutes = 0;
    private string _skillLevel = "Junior";
    private string _objectName = "Test Object";
    private string _objectAddress = "Test Address 1";
    private string? _instructions = null;
    private string? _technicianComment = null;

    private TestUpdateTaskRequestBuilder() { }

    /// <summary>
    /// Creates a new builder with required parameters.
    /// </summary>
    public static TestUpdateTaskRequestBuilder Create(long technicianId, DateTime startAt, DateTime endAt, string title, uint version)
    {
        return new TestUpdateTaskRequestBuilder
        {
            _technicianId = technicianId,
            _startAt = startAt,
            _endAt = endAt,
            _title = title,
            _version = version
        };
    }

    public TestUpdateTaskRequestBuilder WithLocation(string location)
    {
        _location = location;
        return this;
    }

    public TestUpdateTaskRequestBuilder WithPriority(TaskPriority priority)
    {
        _priority = priority;
        return this;
    }

    public TestUpdateTaskRequestBuilder WithStatus(ScheduleTaskStatus status)
    {
        _status = status;
        return this;
    }

    public TestUpdateTaskRequestBuilder WithTimezone(int timezoneOffsetMinutes)
    {
        _timezoneOffsetMinutes = timezoneOffsetMinutes;
        return this;
    }

    public TestUpdateTaskRequestBuilder WithSkillLevel(string skillLevel)
    {
        _skillLevel = skillLevel;
        return this;
    }

    public TestUpdateTaskRequestBuilder WithObjectName(string objectName)
    {
        _objectName = objectName;
        return this;
    }

    public TestUpdateTaskRequestBuilder WithObjectAddress(string objectAddress)
    {
        _objectAddress = objectAddress;
        return this;
    }

    public TestUpdateTaskRequestBuilder WithInstructions(string? instructions)
    {
        _instructions = instructions;
        return this;
    }

    public TestUpdateTaskRequestBuilder WithTechnicianComment(string? comment)
    {
        _technicianComment = comment;
        return this;
    }

    /// <summary>
    /// Builds the UpdateTaskRequestDto with all configured values.
    /// </summary>
    public UpdateTaskRequestDto Build()
    {
        return new UpdateTaskRequestDto
        {
            TechnicianId = _technicianId,
            StartAt = _startAt,
            EndAt = _endAt,
            Title = _title,
            Version = _version,
            Location = _location,
            Priority = _priority,
            Status = _status,
            TimezoneOffsetMinutes = _timezoneOffsetMinutes,
            SkillLevel = _skillLevel,
            ObjectName = _objectName,
            ObjectAddress = _objectAddress,
            Instructions = _instructions,
            TechnicianComment = _technicianComment
        };
    }
}
