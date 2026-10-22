using System.IO;
using AiJobSearchBy.Models;

namespace AiJobSearchBy.ViewModels;

// WP-U1: строка списка вакансий («бегут строки перебора») — проекция JobPosting
// для compiled bindings (x:DataType). STATE: immutable — Posting не меняется.
public sealed class VacancyRow
{
    public required JobPosting Posting { get; init; }

    public string Title => Posting.Title ?? "(без названия)";
    public string Site => Posting.PortalId;

    public string Meta
        => string.Join(" · ", new[] { Posting.Company, Posting.Location }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Salary => Posting.SalaryText ?? "";

    // Нижняя строка строки-вакансии: «remoteok   от 90 до 130 USD».
    public string Footer
        => string.Join("   ", new[] { Site, Salary }.Where(s => !string.IsNullOrEmpty(s)));
}
