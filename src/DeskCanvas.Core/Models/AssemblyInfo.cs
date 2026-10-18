namespace DeskCanvas.Core.Models;

/// <summary>
/// Represents the information of an assembly.
/// </summary>
/// <param name="FilePath">The path to the file that contains the assembly.</param>
/// <param name="AssemblyName">Name of the assembly.</param>
/// <param name="DisplayName">Display name of the assembly.</param>
/// <param name="Author">Author of the assembly.</param>
/// <param name="Version">Version of the assembly.</param>
/// <param name="IconData">Icon data of the assembly.</param>
/// <param name="Categories">Distinct <see cref="Attributes.WidgetInfoAttribute"/> category keys
/// declared by this pack's widgets; null/empty for packs compiled before categorization.</param>
public record AssemblyInfo(
    string FilePath,
    string AssemblyName,
    string DisplayName,
    string Author,
    Version Version,
    string IconData,
    IReadOnlyList<string>? Categories = null);