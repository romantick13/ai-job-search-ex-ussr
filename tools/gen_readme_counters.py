"""One-shot helper: regenerate the generated blocks in README.md from the portal registry.

Запуск (из корня репозитория):

    python tools/gen_readme_counters.py

Файл хранить в UTF-8 без BOM (канон репозитория, RFC-007).

Что делает:
  * читает config/registry.json — ЕДИНЫЙ источник правды (schema_version 3);
  * считает порталы в цепочке по умолчанию и отдельно — доступные без
    ключа и авторизации (access = none | rss);
  * подставляет результаты в README.md между маркерами:
        <!-- PORTAL_TABLE:BEGIN -->     ... <!-- PORTAL_TABLE:END -->
        <!-- AI_PROVIDER_TABLE:BEGIN -->  ... <!-- AI_PROVIDER_TABLE:END -->
  * провайдеры с profile="me" (частная инфраструктура автора) в публичную
    таблицу README НЕ попадают;
  * сверяет фактическое число auth-меньше порталов с expected_counts в
    реестре и, если они разошлись, печатает предупреждение, помечает блок
    в README и возвращает код 2.

Проверки реестра (падение с кодом 1):
  * forbidden-портал не может быть default_enabled или иметь рабочий адаптер;
  * forbidden требует disabled_reason с цитатой;
  * unverified-портал не может быть default_enabled;
  * allowed-портал ОБЯЗАН иметь tos_evidence (цитата/ссылка + дата проверки)
    — защита от «просто поставили allowed» без чтения ToS;
  * default_enabled не может стоять у access=account;
  * default_enabled без рабочего адаптера запрещён;
  * у каждого портала должен быть domain.

Чего скрипт НЕ делает:
  * не ходит в сеть;
  * не правит текст вне маркеров;
  * не удаляет и не перемещает файлы.

Скрипт one-shot: запускается вручную или из CI (джоба docs-consistency).
В рантайме продукта Python не участвует (RFC-001).
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

# GitHub Actions (windows-latest): stdout/stderr по умолчанию cp1252 —
# кириллица в print() падает UnicodeEncodeError. Принудительный UTF-8-вывод.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
REGISTRY = ROOT / "config" / "registry.json"
README = ROOT / "README.md"

# Доступ, не требующий ни ключа, ни входа в аккаунт.
AUTHLESS_ACCESS = {"none", "rss"}

ACCESS_LABEL = {
    "none": "без авторизации",
    "account": "нужен аккаунт",
    "official_api": "официальное API",
    "rss": "RSS",
}

TOS_LABEL = {
    "allowed": "разрешён",
    "official_api": "официальное API",
    "forbidden": "запрещён",
    "unverified": "не проверен",
}

COUNTRY_LABEL = {
    "BY": "🇧🇾",
    "RU": "🇷🇺",
    "PL": "🇵🇱",
    "GLOBAL": "🌍",
}


def load_registry() -> dict:
    if not REGISTRY.exists():
        sys.exit(f"Не найден реестр: {REGISTRY}")
    with REGISTRY.open(encoding="utf-8") as fh:
        return json.load(fh)


def validate(registry: dict) -> list[str]:
    """Проверки, которые не должны тихо проходить."""
    problems: list[str] = []
    for portal in registry.get("portals", []):
        pid = portal.get("id", "<без id>")
        tos = portal.get("tos_status")
        enabled = portal.get("default_enabled", False)
        wired = portal.get("adapter_implemented", True)
        access = portal.get("access")

        if tos == "forbidden":
            if enabled:
                problems.append(
                    f"Портал {pid}: tos_status=forbidden, но default_enabled=true. "
                    f"Запрещённый портал не может быть в цепочке."
                )
            if wired:
                problems.append(
                    f"Портал {pid}: tos_status=forbidden, но adapter_implemented=true. "
                    f"Адаптер запрещённого портала должен быть отключён."
                )
            if not portal.get("disabled_reason"):
                problems.append(
                    f"Портал {pid}: tos_status=forbidden, но нет disabled_reason. "
                    f"Укажите цитату на конкретный пункт соглашения."
                )
        elif tos == "unverified" and enabled:
            problems.append(
                f"Портал {pid}: tos_status=unverified, но default_enabled=true. "
                f"Непроверенный портал по умолчанию выключен."
            )
        elif tos == "allowed" and not portal.get("tos_evidence"):
            problems.append(
                f"Портал {pid}: tos_status=allowed, но нет tos_evidence. "
                f"Приложите цитату/ссылку на документ (или публичный API) и дату проверки."
            )

        if enabled and access == "account":
            problems.append(
                f"Портал {pid}: default_enabled=true, но access=account. "
                f"Портал с обязательным входом не может быть в цепочке «без ключей»."
            )
        if enabled and not wired:
            problems.append(
                f"Портал {pid}: default_enabled=true, но adapter_implemented=false. "
                f"Включать нечего."
            )
        if not portal.get("domain"):
            problems.append(f"У портала {pid} не указан domain.")
    return problems


def default_chain(registry: dict) -> list[dict]:
    return [
        p
        for p in registry.get("portals", [])
        if p.get("default_enabled") and p.get("adapter_implemented", True)
    ]


def authless(registry: dict) -> list[dict]:
    """Порталы цепочки по умолчанию, работающие без ключа и авторизации."""
    return [p for p in default_chain(registry) if p.get("access") in AUTHLESS_ACCESS]


def expected_authless(registry: dict) -> int | None:
    return (registry.get("expected_counts") or {}).get("authless_default")


def render_portal_table(registry: dict) -> str:
    chain = default_chain(registry)
    authless_list = authless(registry)
    count = len(authless_list)
    expected = expected_authless(registry)

    if expected is not None and count != expected:
        head = (
            f"> ⚠️ **Счётчик в реестре не совпадает с ожидаемым.** "
            f"В реестре {count}, ожидается {expected}. "
            f"Реестр не заполнен или изменился — не публикуйте README в этом состоянии."
        )
    else:
        head = (
            f"**{count} порталов** работают без авторизации, "
            f"**{len(chain)}** включены в цепочку по умолчанию."
        )

    lines = [head, "", "| Портал | Домен | Страна | Доступ | Статус ToS |", "|---|---|---|---|---|"]
    for p in sorted(chain, key=lambda x: (x.get("country", ""), x.get("name", ""))):
        country = COUNTRY_LABEL.get(p.get("country", ""), p.get("country", "—"))
        lines.append(
            f"| {p.get('name', p['id'])} | `{p.get('domain', '—')}` | {country} | "
            f"{ACCESS_LABEL.get(p.get('access'), p.get('access', '—'))} | "
            f"{TOS_LABEL.get(p.get('tos_status'), p.get('tos_status', '—'))} |"
        )
    lines += [
        "",
        "_Счётчик и таблица генерируются из "
        "[`config/registry.json`](config/registry.json) — "
        "не правьте их руками в README._",
    ]
    return "\n".join(lines)


def render_ai_table(registry: dict) -> str:
    # profile="me" — частная инфраструктура автора; в публичный README не попадает.
    providers = [
        p for p in registry.get("ai_providers", []) if p.get("profile", "public") != "me"
    ]
    lines = [
        "| Провайдер | Бесплатный тир | Заметка |",
        "|---|---|---|",
    ]
    for p in providers:
        note = (p.get("notes") or "—").replace("|", "\\|")
        lines.append(f"| {p.get('name', p['id'])} | `{p.get('free_tier', '—')}` | {note} |")
    lines += [
        "",
        "_Таблица генерируется из "
        "[`config/registry.json`](config/registry.json)._",
    ]
    return "\n".join(lines)


def replace_block(text: str, tag: str, body: str) -> str:
    begin = f"<!-- {tag}:BEGIN -->"
    end = f"<!-- {tag}:END -->"
    pattern = re.compile(re.escape(begin) + r".*?" + re.escape(end), re.DOTALL)
    replacement = f"{begin}\n{body}\n{end}"
    new_text, count = pattern.subn(replacement, text)
    if count == 0:
        sys.exit(f"В README.md не найден блок {tag}. Добавьте маркеры вручную.")
    if count > 1:
        sys.exit(f"В README.md найдено {count} блоков {tag}. Оставьте один.")
    return new_text


def main() -> int:
    registry = load_registry()

    problems = validate(registry)
    if problems:
        print("Проблемы в реестре:", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        return 1

    text = README.read_text(encoding="utf-8")
    text = replace_block(text, "PORTAL_TABLE", render_portal_table(registry))
    text = replace_block(text, "AI_PROVIDER_TABLE", render_ai_table(registry))
    README.write_text(text, encoding="utf-8", newline="")

    count = len(authless(registry))
    expected = expected_authless(registry)
    print("README.md обновлён из реестра.")
    print(f"  порталов в цепочке по умолчанию: {len(default_chain(registry))}")
    print(f"  без ключа и авторизации: {count}")
    if expected is not None and count != expected:
        print(
            f"  ⚠️  ОЖИДАЛОСЬ {expected}. Реестр неполон или изменился — "
            f"дополните config/registry.json.",
            file=sys.stderr,
        )
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
