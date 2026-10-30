using CommunityToolkit.Mvvm.ComponentModel;

namespace AiJobSearchBy.ViewModels;

// карточка профиля в панели «Моё резюме — профили».
// IsActive рисует плашку «АКТИВНОЕ»; MetaDisplay — источник + дата парса.
public sealed class ResumeProfileRow : ObservableObject
{
    private bool _isActive;

    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string CanonMd { get; init; }
    public required string SourcePath { get; init; }
    public string? DocxPath { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required IReadOnlyList<string> Keywords { get; set; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string SourceDisplay => System.IO.Path.GetFileName(SourcePath) is { Length: > 0 } f ? f : "вручную";

    public string MetaDisplay
    {
        get
        {
            var parts = new List<string> { SourceDisplay, $"ключевых: {Keywords.Count}" };
            if (DocxPath is not null)
            {
                parts.Add("DOCX ✓");
            }

            return string.Join(" · ", parts);
        }
    }
}
