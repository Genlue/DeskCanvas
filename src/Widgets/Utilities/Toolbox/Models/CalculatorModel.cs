using System;
using System.Collections.Generic;

namespace Tools.Models;

public record CalculatorHistoryItem(
    string Expression,
    string Result,
    double NumericResult,
    DateTime Timestamp
);

public record CalculatorStateFile(
    List<CalculatorHistoryItem>? History = null,
    List<double>? MemorySlots = null,
    string LastExpression = "",
    string LastResult = "0",
    double LastAns = 0
);

public record CalculatorModel(
    int DecimalPlaces = 6,
    bool UseThousandsSeparator = true,
    int MaxHistoryItems = 30,
    bool ClickHistoryToCopy = false,
    string DefaultAngleMode = "DEG",
    string DefaultPanelTab = "Scientific"
);
