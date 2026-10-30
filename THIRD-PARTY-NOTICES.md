# Third-Party Notices

This file lists third-party components used or distributed with
**ai-job-search-ex-ussr**. It is provided for information only and is not a
license agreement; the actual terms are the texts of the respective
licenses.

## Licensing overview

| What | Copyright | License | Grant file |
|---|---|---|---|
| Upstream project [ai-job-search](https://github.com/MadsLorentzen/ai-job-search) (attribution; no files ported verbatim) | © 2025 MadsLorentzen | MIT | [LICENSE](LICENSE) |
| The ai-job-search-ex-ussr adaptation (.NET/Avalonia app, scripts, docs) | © 2026 ThreeI and the ai-job-search-ex-ussr contributors | MIT | [LICENSE-THREEI.md](LICENSE-THREEI.md) |

No upstream files are ported verbatim: the upstream markdown
prompts/commands/skills are **not** carried over (owner decision,
2026-10-20). If content is ever ported in the future, each copied
markdown asset must carry a fixed 3-line provenance header (original
upstream path + MIT notice + copy date) with the file body verbatim to
upstream.

## 1. ai-job-search (upstream) — MIT License

- Source: <https://github.com/MadsLorentzen/ai-job-search>
- License: MIT — see [LICENSE](LICENSE), included verbatim in this repository.
- Used by this project: the portal adapter contract and the reference
  workflow (no verbatim files; the upstream markdown prompt assets are
  not carried over — owner decision, 2026-10-20).
- Attribution: [README.md](README.md), раздел «Лицензии».

## 2. QuestPDF 2026.8.0 — QuestPDF Community License v3.0

- Source: <https://www.questpdf.com/>
- License: QuestPDF Community License v3.0 (Effective Date 06.07.2026),
  <https://www.questpdf.com/license/community.html>
- Explicit notice: "QuestPDF is used under the Community License for
  FOSS — https://www.questpdf.com/license/"
- Free for individuals, OSI-licensed open-source projects, charitable
  organizations, academic institutions and businesses with annual gross
  revenue under USD 1,000,000. Not eligible regardless of revenue:
  public-sector entities and publicly traded companies; if eligibility
  is lost, there is a 90-day period to transition to a paid license.
- Redistribution is permitted only as a **compiled component of a
  Derivative Application**: QuestPDF source code is not included in the
  distribution zip and proprietary notices are not removed or obscured.
- "Built with QuestPDF" is used in the README as a truthful factual
  statement — permitted by the license, not a requirement.
- This distribution is built and redistributed by the ai-job-search-ex-ussr
  maintainers, who comply with the Community terms; end users of the
  application are not "Developers" under the License Selection Guide
  v3.0 and do not need a separate QuestPDF license.
- A summary of the conditions also appears in [DISCLAIMER.md](DISCLAIMER.md).

## 3. Fonts

- This project does **not** bundle any font files of its own.
- **Lato 2.015** (SIL Open Font License 1.1, OFL-1.1) ships *inside the
  QuestPDF NuGet package* as QuestPDF's default document font. Our code
  never invokes it: every document sets its font family explicitly
  (see below).
- Document exporters (QuestPDF) use the Windows system font **Segoe UI**
  — it ships with Windows 10/11 and covers Cyrillic; no font files are
  copied into the distribution.
- The PDF overlay service resolves fonts from the operating system in a
  fixed order: Segoe UI → Arial → Calibri → Tahoma → DejaVu Sans. If
  none of these TTFs is available, the overlay stops with a clear error
  message instead of producing a broken document.

## 4. NuGet dependencies

Versions are pinned in [`src/AiJobSearchExUssr/AiJobSearchExUssr.csproj`](src/AiJobSearchExUssr/AiJobSearchExUssr.csproj);
that file is the authoritative source at build time.

| Component | Version | License (SPDX) |
|---|---|---|
| Avalonia | 12.1.1 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Microsoft.Data.Sqlite | 10.0.10 | MIT |
| Dapper | 2.1.79 | Apache-2.0 |
| Markdig | 1.3.2 | BSD-2-Clause |
| DocumentFormat.OpenXml | 3.2.0 | MIT |
| PdfPig | 0.1.16 | Apache-2.0 |
| PDFsharp | 6.1.1 | MIT |
| QuestPDF | 2026.8.0 | QuestPDF Community v3.0 (see §2) |

PdfPig license verified against the primary source (GitHub,
UglyToad/PdfPig → Apache-2.0) on 2026-10-22. Note (2026-10-22):
the package id moved on NuGet from «UglyToad.PdfPig» to «PdfPig»; the
pin «pinned at integration» closed at 0.1.16 (namespace UglyToad.PdfPig
unchanged).

Markdig license verified against the primary source (GitHub API,
xoofx/markdig → BSD-2-Clause) on 2026-10-19.

## 5. .NET runtime

The self-contained distribution includes the .NET 10 runtime, which
contains Microsoft and third-party components. The SDK
`ThirdPartyNotices.txt` ships with the distribution:
`licenses\ThirdPartyNotices-dotnet.txt`.

## 6. Test-only dependencies

- xunit 2.9.2 — Apache-2.0 (test-only; not part of the distribution zip).

## Maintenance rule

A new NuGet package in the distribution = a new line in this file in the
same commit. Licenses are verified against the primary source
(NuGet / deps.dev / GitHub), never copied from another document.
Last full check: 2026-10-19.

2026-10-29: former §7 (embedding model Qwen3-Embedding-0.6B) removed —
the embedding subsystem was cut from the application entirely; the
application no longer acts as an embeddings HTTP client.
