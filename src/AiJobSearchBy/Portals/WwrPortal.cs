using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: адаптер WeWorkRemotely на RSS-ленте (Access=Feed, Auth=None, карточка
// BuiltInPortals.Core). Лента: https:// weworkremotely.com/remote-jobs.rss —
// полная (все категории, ~90 свежих), подтверждена живым зондом.
// Заголовок item'а в формате «Компания: Должность» — раскладываем на поля
// честно: без «:» компания = null, title целиком.
// Регион/категория ленты в FeedItem не переносятся (база парсит
// title/link/summary/published/guid) — Location = null, не выдумываем.
// THREAD: состояние нет — потокобезопасен.
public sealed class WwrPortal : FeedPortalBase
{
    private const string FeedUrl = "https://weworkremotely.com/remote-jobs.rss";

    public WwrPortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => BuiltInPortals.CoreCard("wwr");

    protected override string BuildFeedUrl(string query) => FeedUrl;

    protected override JobPosting MapItem(FeedItem item)
    {
        // «Компания: Должность» → два поля; без разделителя — компания честно null.
        var colon = item.Title?.IndexOf(':') ?? -1;
        var hasCompany = colon > 0;
        var company = hasCompany ? item.Title![..colon].Trim() : null;
        var title = hasCompany ? item.Title![(colon + 1)..].Trim() : item.Title;

        return new JobPosting
        {
            PortalId = Descriptor.Id,
            ExternalId = item.ExternalId,
            CanonicalUrl = item.Link,
            Title = title,
            Company = company,
            Location = null,
            SalaryText = null,
            DescriptionMd = item.Summary,
            SourceClass = Descriptor.SourceClass,
            AccessKind = Descriptor.Access,
            DedupeHash = DedupeHasher.ComputeComposite(Descriptor.Id, item.ExternalId, item.Link),
        };
    }
}
