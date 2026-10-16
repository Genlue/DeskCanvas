namespace Wheel.Models;

public record WheelModel(
    string? CurrentTemplateId = null,
    double SpinDurationSeconds = 2.5,
    bool EliminateOnSelected = false
);
