using AiJobSearchBy.Models;
using AiJobSearchBy.Services;

namespace AiJobSearchBy.Portals;

// FLOW: адаптер NoDesk на RSS-ленте (Access=Feed, Auth=None, карточка
// BuiltInPortals.Core). Лента: https:// nodesk.co/remote-jobs/index.xml —
// подтверждена живым зондом (10 свежих вакансий).
// Заголовок вида «Должность at Компания» — компания выцепляется хвостом
// « at …» (последнее вхождение, чтобы не съесть «at» внутри должности);
// без «at» компания честно null.
// THREAD: состояние нет — потокобезопасен.
public sealed class NodeskPortal : FeedPortalBase
{
    private const string FeedUrl = "https://nodesk.co/remote-jobs/index.xml";

    public NodeskPortal(HttpClient http, Services.ILogService log)
        : base(BuildDescriptor(), http, log)
    {
    }

    private static PortalDescriptor BuildDescriptor() => BuiltInPortals.CoreCard("nodesk");

    protected override string BuildFeedUrl(string query) => FeedUrl;

    protected override JobPosting MapItem(FeedItem item)
    {
        var (title, company) = SplitCompany(item.Title);
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

    // «Senior Meta Strategist at Directive Consulting» → (должность, компания).
    private static (string? Title, string? Company) SplitCompany(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (raw, null);
        }

        var marker = raw.LastIndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0 || marker + 4 >= raw.Length)
        {
            return (raw, null);
        }

        return (raw[..marker].Trim(), raw[(marker + 4)..].Trim());
    }
}
