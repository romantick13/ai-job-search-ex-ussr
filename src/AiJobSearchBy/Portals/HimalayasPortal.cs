using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: адаптер Himalayas на RSS-ленте (Access=Feed, Auth=None, карточка
// BuiltInPortals.Core). Лента: https://himalayas.app/jobs/rss — подтверждена
// живым зондом 2026-10-22 (20 свежих вакансий, title в CDATA).
// Компания/регион в ленте не переносятся — поля честно null, не выдумываем.
// THREAD: состояние нет — потокобезопасен.
public sealed class HimalayasPortal : FeedPortalBase
{
    private const string FeedUrl = "https://himalayas.app/jobs/rss";

    public HimalayasPortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => new()
    {
        Id = "himalayas",
        DisplayName = "Himalayas",
        SourceClass = PortalSourceClass.Commercial,
        Access = PortalAccessKind.Feed,
        Auth = PortalAuthKind.None,
        DefaultPriority = 6,
        ApplySupported = true,
    };

    protected override string BuildFeedUrl(string query) => FeedUrl;

    protected override JobPosting MapItem(FeedItem item) => new()
    {
        PortalId = Descriptor.Id,
        ExternalId = item.ExternalId,
        CanonicalUrl = item.Link,
        Title = item.Title,
        Company = null,
        Location = null,
        SalaryText = null,
        DescriptionMd = item.Summary,
        SourceClass = Descriptor.SourceClass,
        AccessKind = Descriptor.Access,
        DedupeHash = DedupeHasher.ComputeComposite(Descriptor.Id, item.ExternalId, item.Link),
    };
}
