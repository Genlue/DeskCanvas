using System.Collections.Generic;

namespace Calendar.Models;

/// <summary>
/// Root persistent document for unified schedule events.
/// </summary>
public record ScheduleModel(
    int Version = 1,
    List<ScheduleCategoryModel>? Categories = null,
    List<ScheduleEventModel>? Events = null
);
