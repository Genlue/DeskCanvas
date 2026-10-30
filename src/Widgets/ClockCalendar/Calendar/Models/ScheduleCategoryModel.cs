namespace Calendar.Models;

/// <summary>
/// A calendar category with an assigned color indicator.
/// </summary>
public record ScheduleCategoryModel(
    string Id,
    string Name,
    string Color
);
