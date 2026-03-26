using System;
using System.ComponentModel.DataAnnotations;

namespace ServiceTaskPlanningSystem.Models.Validation;

/// <summary>
/// Rejects a nullable DateTime whose date portion is strictly before today (UTC).
/// Null values pass — the field is optional.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotInPastAttribute : ValidationAttribute
{
    public NotInPastAttribute()
        : base("Terminas negali būti praeityje.") { }

    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (value is not DateTime date)
            return ValidationResult.Success;

        var todayUtc = DateTime.UtcNow.Date;
        if (date.Date < todayUtc)
            return new ValidationResult(ErrorMessage, [context.MemberName!]);

        return ValidationResult.Success;
    }
}
