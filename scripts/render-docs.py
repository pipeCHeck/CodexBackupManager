#!/usr/bin/env python3
"""
docs/*.md → 같은 이름의 .html (표준 라이브러리만 사용).

    python scripts/render-docs.py docs/import-ux-redesign-phase9.md docs/phase9-implementation-plan.md

- 원본은 항상 .md다. .html은 생성물이므로 직접 고치지 않는다.
- 지원 문법(이 저장소 문서가 쓰는 부분집합): 제목, 문단, 굵게/기울임/취소선/인라인 코드/링크,
  목록(들여쓰기 중첩), 작업 목록(- [ ] / - [x]), 표, 코드 블록, 인용문, 구분선.
- 문서 규칙 → 화면 표현:
  * `**라벨**: 값`으로 시작하는 문단 → 라벨/값 필드 표
  * `- [ ] **9_1-03** …` → 작업 행(ID 배지, 테스트 ID `-T`는 별도 표시), 진행률은 생성 시 정적으로 계산
  * `⬜ 대기` `🟦 구현 중` `🟨 점검 중` `🟩 완료` `⛔ 보류` → 상태 배지
  * 제목 앞 번호(`5.` `5.1`)와 단계 ID(`9_1 —`) → 번호 칩
  * 첫 번째 인용문 → 문서 개요 패널, `## 목차` 절 → 사이드 목차로 대체(본문에서 생략)
"""
from __future__ import annotations

import html
import re
import sys
from datetime import datetime
from pathlib import Path

# ─────────────────────────────── inline ───────────────────────────────

_CODE_SPAN = re.compile(r"`([^`]+)`")
_LINK = re.compile(r"\[([^\]]+)\]\(([^)\s]+)\)")
_BOLD = re.compile(r"\*\*(.+?)\*\*")
_STRIKE = re.compile(r"~~(.+?)~~")
_ITALIC = re.compile(r"(?<![\w*])\*(?!\s)([^*]+?)(?<!\s)\*(?![\w*])")

STATUS = {
    "⬜ 대기": ("wait", "대기"),
    "🟦 구현 중": ("doing", "구현 중"),
    "🟨 점검 중": ("review", "점검 중"),
    "🟩 완료": ("done", "완료"),
    "⛔ 보류": ("hold", "보류"),
}


def _link_target(url: str, rendered: set[str]) -> str:
    if re.match(r"^[a-z]+:", url) or url.startswith("#"):
        return url
    path, _, frag = url.partition("#")
    if path.endswith(".md") and Path(path).name in rendered:
        path = path[:-3] + ".html"
    return path + ("#" + frag if frag else "")


def inline(text: str, rendered: set[str]) -> str:
    codes: list[str] = []

    def keep_code(m: re.Match) -> str:
        codes.append(f"<code>{html.escape(m.group(1))}</code>")
        return f"\x00{len(codes) - 1}\x00"

    text = _CODE_SPAN.sub(keep_code, text)
    text = html.escape(text, quote=False)
    for key, (cls, label) in STATUS.items():
        text = text.replace(key, f'<span class="status st-{cls}">{label}</span>')
    text = _LINK.sub(lambda m: f'<a href="{html.escape(_link_target(m.group(2), rendered))}">{m.group(1)}</a>', text)
    text = _BOLD.sub(r"<strong>\1</strong>", text)
    text = _STRIKE.sub(r"<del>\1</del>", text)
    text = _ITALIC.sub(r"<em>\1</em>", text)
    return re.sub(r"\x00(\d+)\x00", lambda m: codes[int(m.group(1))], text)


def strip_tags(s: str) -> str:
    return html.unescape(re.sub(r"<[^>]+>", "", s))


# ─────────────────────────────── blocks ───────────────────────────────

_HEADING = re.compile(r"^(#{1,6})\s+(.*)$")
_LIST = re.compile(r"^(\s*)([-*]|\d+\.)\s+(.*)$")
_TASK = re.compile(r"^\[([ xX])\]\s+(.*)$")
_TASK_ID = re.compile(r"^\*\*(9_[0-9A-Za-z]+-[0-9A-Za-z]+)\*\*\s*(.*)$")
_TABLE_SEP = re.compile(r"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")
_FIELD = re.compile(r"^\*\*([^*]{1,24})\*\*\s*:\s*(.*)$")
_SECNUM = re.compile(r"^(\d+(?:\.\d+)*)\.?\s+(.*)$")
_PHASE = re.compile(r"^(9_[0-9A-Za-z]+(?:-[A-Z])?)\s+—\s+(.*)$")

LANG_LABEL = {"text": "텍스트", "csharp": "C#", "sql": "SQL", "bash": "명령", "powershell": "PowerShell", "json": "JSON"}


def slugify(text: str, used: dict[str, int]) -> str:
    plain = strip_tags(text).strip().lower()
    slug = re.sub(r"[^\w가-힣\- ]", "", plain).replace(" ", "-")
    slug = re.sub(r"-{2,}", "-", slug).strip("-") or "section"
    n = used.get(slug, 0)
    used[slug] = n + 1
    return slug if n == 0 else f"{slug}-{n}"


def split_row(line: str) -> list[str]:
    line = line.strip()
    if line.startswith("|"):
        line = line[1:]
    if line.endswith("|"):
        line = line[:-1]
    cells, cur, in_code = [], "", False
    for ch in line:
        if ch == "`":
            in_code = not in_code
        if ch == "|" and not in_code:
            cells.append(cur.strip())
            cur = ""
        else:
            cur += ch
    cells.append(cur.strip())
    return cells


class Section:
    def __init__(self, slug: str, title_html: str, level: int):
        self.slug, self.title_html, self.level = slug, title_html, level
        self.total = 0
        self.done = 0


class Renderer:
    def __init__(self, rendered: set[str], top: bool = True):
        self.rendered = rendered
        self.top = top
        self.used_slugs: dict[str, int] = {}
        self.sections: list[Section] = []   # h2 + h3 (목차)
        self.h2: Section | None = None
        self.title = ""
        self.overview = ""
        self.total = 0
        self.done = 0

    def il(self, t: str) -> str:
        return inline(t, self.rendered)

    # ── heading ──
    def heading(self, level: int, raw: str) -> str:
        num = phase = None
        m = _SECNUM.match(raw)
        if m and level in (2, 3, 4):
            num, raw = m.group(1), m.group(2)
        m = _PHASE.match(raw)
        if m and level in (2, 3):
            phase, raw = m.group(1), m.group(2)
        inner = self.il(raw)
        slug = slugify(("s" + num + "-" if num else "") + (phase + "-" if phase else "") + inner, self.used_slugs)
        chips = ""
        if num:
            chips += f'<span class="num">{"§" if level == 2 else ""}{num}</span>'
        if phase:
            chips += f'<span class="phase">{phase}</span>'
        if level == 1:
            self.title = strip_tags(inner)
            return ""  # 제목은 머리말(masthead)에서 렌더링한다
        sec = Section(slug, chips + f"<span>{inner}</span>", level)
        counter = ""
        if level == 2:
            self.h2 = sec
            counter = f"\x01{len(self.sections)}\x01"
        if level in (2, 3):
            self.sections.append(sec)
        return f'<h{level} id="{slug}">{chips}<span class="htext">{inner}</span>{counter}<a class="anchor" href="#{slug}" aria-label="이 절 링크">¶</a></h{level}>'

    def render(self, lines: list[str]) -> str:
        out: list[str] = []
        i = 0
        para: list[str] = []
        skipping_toc = False
        collapse_next = False

        def flush_para() -> None:
            if not para:
                return
            first = para[0].strip()
            if _FIELD.match(first):
                fields: list[list[str]] = []
                for ln in para:
                    fm = _FIELD.match(ln.strip())
                    if fm:
                        fields.append([fm.group(1), fm.group(2)])
                    else:
                        fields[-1][1] += " " + ln.strip()
                rows = "".join(f"<div><dt>{self.il(k)}</dt><dd>{self.il(v)}</dd></div>" for k, v in fields)
                out.append(f'<dl class="fields">{rows}</dl>')
            else:
                out.append(f"<p>{self.il(' '.join(s.strip() for s in para))}</p>")
            para.clear()

        while i < len(lines):
            line = lines[i]
            stripped = line.strip()

            if skipping_toc:
                if stripped.startswith("## ") or re.match(r"^-{3,}\s*$", stripped):
                    skipping_toc = False
                else:
                    i += 1
                    continue

            if stripped.startswith("<!--html"):
                flush_para()
                i += 1
                raw = []
                while i < len(lines) and lines[i].strip() != "-->":
                    raw.append(lines[i])
                    i += 1
                i += 1
                out.append('<div class="visual">' + "\n".join(raw) + "</div>")
                collapse_next = True
                continue

            if stripped.startswith("```"):
                flush_para()
                lang = stripped[3:].strip()
                i += 1
                buf = []
                while i < len(lines) and not lines[i].strip().startswith("```"):
                    buf.append(lines[i])
                    i += 1
                i += 1
                indent = min((len(b) - len(b.lstrip()) for b in buf if b.strip()), default=0)
                code = "\n".join(b[indent:] for b in buf)
                label = LANG_LABEL.get(lang, lang or "코드")
                kind = "diagram" if lang == "text" else "code"
                fig = (
                    f'<figure class="block {kind}"><figcaption><span>{html.escape(label)}</span>'
                    f'<button type="button" class="copy" aria-label="내용 복사">복사</button></figcaption>'
                    f"<pre><code>{html.escape(code)}</code></pre></figure>"
                )
                if collapse_next:
                    fig = f'<details class="alt"><summary>텍스트 버전 보기</summary>{fig}</details>'
                    collapse_next = False
                out.append(fig)
                continue

            if not stripped:
                flush_para()
                i += 1
                continue

            m = _HEADING.match(line)
            if m:
                flush_para()
                level, text = len(m.group(1)), m.group(2).strip()
                if self.top and level == 2 and strip_tags(text) == "목차":
                    skipping_toc = True
                    i += 1
                    continue
                h = self.heading(level, text)
                if h:
                    out.append(h)
                i += 1
                continue

            if re.match(r"^-{3,}\s*$", stripped) or re.match(r"^\*{3,}\s*$", stripped):
                flush_para()
                i += 1
                continue

            if stripped.startswith(">"):
                flush_para()
                buf = []
                while i < len(lines) and lines[i].strip().startswith(">"):
                    buf.append(re.sub(r"^\s*>\s?", "", lines[i]))
                    i += 1
                inner = Renderer(self.rendered, top=False).render(buf)
                if self.top and not self.overview and not self.sections:
                    self.overview = inner
                else:
                    out.append(f'<aside class="note">{inner}</aside>')
                continue

            if "|" in stripped and i + 1 < len(lines) and _TABLE_SEP.match(lines[i + 1]):
                flush_para()
                header = split_row(line)
                i += 2
                rows = []
                while i < len(lines) and "|" in lines[i] and lines[i].strip():
                    rows.append(split_row(lines[i]))
                    i += 1
                th = "".join(f"<th>{self.il(c)}</th>" for c in header)
                body = "".join("<tr>" + "".join(f"<td>{self.il(c)}</td>" for c in r) + "</tr>" for r in rows)
                out.append(f'<div class="table-wrap"><table><thead><tr>{th}</tr></thead><tbody>{body}</tbody></table></div>')
                continue

            if _LIST.match(line):
                flush_para()
                i = self.render_list(lines, i, out)
                continue

            para.append(line)
            i += 1

        flush_para()
        result = "\n".join(out)
        if self.top:
            for idx, sec in enumerate(self.sections):
                badge = ""
                if sec.level == 2 and sec.total:
                    full = " full" if sec.done == sec.total else ""
                    badge = f'<span class="count{full}">{sec.done}/{sec.total}</span>'
                result = result.replace(f"\x01{idx}\x01", badge)
        return result

    def render_list(self, lines: list[str], i: int, out: list[str]) -> int:
        items: list[tuple[int, bool, list[str]]] = []
        while i < len(lines):
            line = lines[i]
            m = _LIST.match(line)
            if m:
                items.append((len(m.group(1).expandtabs(4)), m.group(2)[0].isdigit(), [m.group(3)]))
                i += 1
                continue
            if line.strip() and items and (len(line) - len(line.lstrip())) > items[-1][0] and not line.strip().startswith("```"):
                items[-1][2].append(line.strip())
                i += 1
                continue
            break
        out.append(self._nest(items, 0, len(items)))
        return i

    def _nest(self, items, start: int, end: int) -> str:
        base = items[start][0]
        tag = "ol" if items[start][1] else "ul"
        parts, has_task, j = [], False, start
        while j < end:
            text = " ".join(items[j][2])
            k = child_end = j + 1
            while child_end < end and items[child_end][0] > base:
                child_end += 1
            child = self._nest(items, k, child_end) if child_end > k else ""
            tm = _TASK.match(text)
            if tm:
                has_task = True
                parts.append(self._task(tm.group(1) in "xX", tm.group(2), child))
            else:
                parts.append(f"<li>{self.il(text)}{child}</li>")
            j = child_end
        cls = ' class="tasks"' if has_task else ""
        return f"<{tag}{cls}>{''.join(parts)}</{tag}>"

    def _task(self, checked: bool, text: str, child: str) -> str:
        self.total += 1
        self.done += checked
        if self.h2:
            self.h2.total += 1
            self.h2.done += checked
        idm = _TASK_ID.match(text)
        tid, kind = "", ""
        if idm:
            tid_s, text = idm.group(1), idm.group(2)
            is_test = re.search(r"-T\d*$", tid_s) is not None
            kind = " test" if is_test else ""
            tid = f'<code class="tid{kind}">{tid_s}</code>'
            if is_test:
                tid += '<span class="kind">테스트</span>'
        state = "done" if checked else "todo"
        mark = f'<span class="box" role="img" aria-label="{"완료" if checked else "미완료"}"></span>'
        return f'<li class="task {state}{kind}">{mark}<div class="tbody"><div class="thead">{tid}</div><div class="ttext">{self.il(text)}</div>{child}</div></li>'


# ─────────────────────────────── page ───────────────────────────────

PAGE = r"""<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>__TITLE__</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500;600&family=IBM+Plex+Sans+KR:wght@400;500;600;700&family=Nanum+Gothic+Coding:wght@400;700&display=swap">
<style>
/* Layout: 고정 목차 레일 + 72ch 본문 열(표·도식은 열 전체 폭), 앱(WPF) 다크 팔레트를 문서 톤으로 옮김 */
:root {
  --bg: #f4f6f8;        /* 청회색 기운의 종이 */
  --surface: #ffffff;
  --surface-2: #eef1f5;
  --fg: #1c2128;
  --muted: #5b6573;
  --line: #d9dee5;
  --accent: #2f5f93;    /* 앱 선택색 #3A6EA5 계열 */
  --accent-soft: #e3ecf6;
  --ok: #2e7d32;  --ok-soft: #e4f2e5;
  --warn: #9a5b00; --warn-soft: #fbefdc;
  --bad: #c0392b;  --bad-soft: #fbe6e3;
  --info: #3b5bb5; --info-soft: #e5eafa;
  --code-bg: #eef1f4;
  --shadow: 0 1px 2px rgba(20, 30, 45, .06), 0 2px 8px rgba(20, 30, 45, .04);
  --font-body: "IBM Plex Sans KR", "Malgun Gothic", "Apple SD Gothic Neo", system-ui, sans-serif;
  --font-mono: "IBM Plex Mono", "D2Coding", Consolas, "Malgun Gothic", monospace;
  --font-diagram: "D2Coding", "Nanum Gothic Coding", "IBM Plex Mono", Consolas, monospace;
  --measure: 72ch;
  /* 앱 화면 목업 전용(앱은 다크 테마 하나뿐이라 두 테마에서 같은 값) */
  --app-bg: #1e1f22; --app-panel: #2b2d31; --app-panel-2: #33363b; --app-fg: #e6e6e6; --app-muted: #9aa0a6;
  --app-line: #44474d; --app-accent: #3a6ea5; --app-on-accent: #ffffff; --app-ok: #4caf50; --app-warn: #ffb74d;
  --app-warn-bg: #3a2e1e; --app-input: #17181b; --app-bubble: #2f3b4c;
  color-scheme: light;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --bg: #1e1f22; --surface: #2b2d31; --surface-2: #33363b; --fg: #e6e6e6; --muted: #9aa0a6; --line: #44474d;
    --accent: #7fa8d6; --accent-soft: #2a3747;
    --ok: #6cc070; --ok-soft: #243427; --warn: #ffb74d; --warn-soft: #3a2e1e; --bad: #e57373; --bad-soft: #3b2626;
    --info: #9bb0ee; --info-soft: #2a3150; --code-bg: #17181b; --shadow: none; color-scheme: dark;
  }
}
:root[data-theme="dark"] {
  --bg: #1e1f22; --surface: #2b2d31; --surface-2: #33363b; --fg: #e6e6e6; --muted: #9aa0a6; --line: #44474d;
  --accent: #7fa8d6; --accent-soft: #2a3747;
  --ok: #6cc070; --ok-soft: #243427; --warn: #ffb74d; --warn-soft: #3a2e1e; --bad: #e57373; --bad-soft: #3b2626;
  --info: #9bb0ee; --info-soft: #2a3150; --code-bg: #17181b; --shadow: none; color-scheme: dark;
}

* { box-sizing: border-box; }
html { scroll-padding-top: 20px; -webkit-text-size-adjust: 100%; }
body { margin: 0; background: var(--bg); color: var(--fg); font: 400 15.5px/1.75 var(--font-body); word-break: keep-all; overflow-wrap: break-word; }
a { color: var(--accent); text-underline-offset: 3px; }
a:focus-visible, button:focus-visible, summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }

.shell { display: grid; grid-template-columns: 272px minmax(0, 1fr); min-height: 100vh; }

/* ── 목차 레일 ── */
.rail { position: sticky; top: 0; height: 100vh; overflow: auto; border-right: 1px solid var(--line); background: var(--surface); padding-block: 22px 40px; padding-inline: 14px; }
.rail .brand { font: 600 11px/1.4 var(--font-mono); letter-spacing: .08em; text-transform: uppercase; color: var(--muted); padding: 0 8px 10px; }
.rail .rail-title { font-weight: 700; font-size: 14px; line-height: 1.45; padding: 0 8px 14px; border-bottom: 1px solid var(--line); margin-bottom: 10px; text-wrap: balance; }
.rail details summary { list-style: none; cursor: pointer; font: 600 12px var(--font-body); letter-spacing: .02em; color: var(--muted); padding: 4px 8px; }
.rail details summary::-webkit-details-marker { display: none; }
.toc { display: grid; gap: 1px; margin-top: 4px; }
.toc a { display: flex; gap: 8px; align-items: baseline; padding: 5px 8px; border-radius: 6px; color: var(--muted); text-decoration: none; font-size: 13px; line-height: 1.45; }
.toc a:hover { background: var(--surface-2); color: var(--fg); }
.toc a.l3 { padding-left: 26px; font-size: 12.5px; }
.toc a.active { background: var(--accent-soft); color: var(--fg); }
.toc .tnum { font: 500 11.5px var(--font-mono); color: var(--muted); min-width: 2.2em; }
.toc .tlabel { flex: 1; min-width: 0; }
.toc .count { font: 500 11px var(--font-mono); color: var(--muted); font-variant-numeric: tabular-nums; }
.toc .count.full { color: var(--ok); }

/* ── 본문 ── */
main { min-width: 0; padding-block: 36px 96px; padding-inline: clamp(16px, 5vw, 56px); }
.col { max-width: 1040px; }
.masthead { display: grid; gap: 14px; padding-bottom: 8px; }
.eyebrow { font: 600 13px/1 var(--font-body); letter-spacing: .02em; color: var(--accent); }
h1 { font-size: clamp(26px, 3.4vw, 36px); line-height: 1.25; letter-spacing: -.02em; margin: 0; text-wrap: balance; font-weight: 700; }
.meta { display: flex; flex-wrap: wrap; gap: 8px 18px; align-items: center; color: var(--muted); font-size: 13px; }
.meta code { font-size: 12px; }
.meta .spacer { flex: 1; }
.btn { font: 500 12.5px var(--font-body); color: var(--fg); background: var(--surface); border: 1px solid var(--line); border-radius: 7px; padding: 4px 12px; cursor: pointer; }
.btn:hover { background: var(--surface-2); }
.overview { background: var(--surface); border: 1px solid var(--line); border-radius: 12px; padding: 18px 22px; box-shadow: var(--shadow); max-width: var(--measure); }
.overview p { margin: 6px 0; }
.overview > :first-child { margin-top: 0; }
.overview ol, .overview ul { margin: 6px 0; }

/* 진행 대시보드 */
.dash { margin: 26px 0 8px; background: var(--surface); border: 1px solid var(--line); border-radius: 12px; box-shadow: var(--shadow); overflow: hidden; }
.dash-head { display: flex; flex-wrap: wrap; gap: 8px 16px; align-items: baseline; justify-content: space-between; padding: 16px 20px 12px; border-bottom: 1px solid var(--line); }
.dash-head strong { font-size: 15px; }
.dash-total { font: 600 22px/1 var(--font-mono); font-variant-numeric: tabular-nums; }
.dash-total small { font-size: 13px; color: var(--muted); font-weight: 500; }
.dash-rows { display: grid; }
.dash-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(90px, 220px) 64px; gap: 14px; align-items: center; padding: 9px 20px; text-decoration: none; color: var(--fg); border-bottom: 1px solid var(--line); font-size: 13.5px; }
.dash-row:last-child { border-bottom: none; }
.dash-row:hover { background: var(--surface-2); }
.dash-row .dname { display: flex; gap: 8px; align-items: baseline; min-width: 0; }
.dash-row .dname span:last-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.dash-row .dcount { font: 500 12.5px var(--font-mono); text-align: right; color: var(--muted); font-variant-numeric: tabular-nums; }
.dash-row.full .dcount { color: var(--ok); }
.meter { height: 6px; background: var(--surface-2); border-radius: 99px; overflow: hidden; }
.meter > i { display: block; height: 100%; background: var(--ok); border-radius: 99px; }

/* 제목 */
h2, h3, h4 { position: relative; display: flex; flex-wrap: wrap; gap: 4px 10px; align-items: baseline; text-wrap: balance; }
h2 { font-size: 22px; line-height: 1.35; letter-spacing: -.01em; margin: 56px 0 14px; padding-top: 22px; border-top: 1px solid var(--line); font-weight: 700; }
h3 { font-size: 17.5px; line-height: 1.4; margin: 34px 0 10px; font-weight: 600; }
h4 { font-size: 15.5px; margin: 24px 0 8px; font-weight: 600; }
.htext { min-width: 0; }
.num { font: 600 .72em/1 var(--font-mono); color: var(--accent); background: var(--accent-soft); padding: .35em .5em; border-radius: 6px; align-self: center; }
h3 .num { background: none; padding: 0; color: var(--muted); }
.phase { font: 600 .72em/1 var(--font-mono); color: var(--fg); border: 1px solid var(--line); background: var(--surface); padding: .35em .5em; border-radius: 6px; align-self: center; }
h2 .count, h3 .count { font: 500 12px/1 var(--font-mono); color: var(--muted); border: 1px solid var(--line); border-radius: 99px; padding: 5px 9px; align-self: center; margin-left: auto; font-variant-numeric: tabular-nums; }
h2 .count.full { color: var(--ok); border-color: var(--ok); background: var(--ok-soft); }
.anchor { position: absolute; left: -22px; top: auto; color: var(--muted); text-decoration: none; opacity: 0; font-weight: 400; }
h2 .anchor { top: 22px; }
h2:hover .anchor, h3:hover .anchor, h4:hover .anchor, .anchor:focus-visible { opacity: .7; }

/* 본문 요소 */
p, main > .col > ul, main > .col > ol, .fields, aside.note { max-width: var(--measure); }
p { margin: 10px 0; }
strong { font-weight: 600; }
code { font: 500 .86em var(--font-mono); background: var(--code-bg); border: 1px solid var(--line); padding: .05em .38em; border-radius: 5px; overflow-wrap: anywhere; }
ul, ol { padding-left: 1.4em; margin: 8px 0; }
li { margin: 4px 0; }
li::marker { color: var(--muted); }
del { color: var(--muted); }

.fields { margin: 12px 0 16px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); overflow: hidden; }
.fields > div { display: grid; grid-template-columns: 8.5em minmax(0, 1fr); gap: 14px; padding: 9px 16px; border-bottom: 1px solid var(--line); }
.fields > div:last-child { border-bottom: none; }
.fields dt { font-weight: 600; font-size: 13px; color: var(--muted); padding-top: 1px; }
.fields dd { margin: 0; min-width: 0; }

aside.note { margin: 14px 0; padding: 12px 16px; border: 1px solid var(--line); border-left: 3px solid var(--info); background: var(--info-soft); border-radius: 8px; }
aside.note p { margin: 4px 0; }

.table-wrap { overflow-x: auto; margin: 14px 0 18px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); }
table { border-collapse: collapse; width: 100%; font-size: 13.5px; line-height: 1.6; }
th, td { text-align: left; vertical-align: top; padding: 9px 12px; border-bottom: 1px solid var(--line); }
th { background: var(--surface-2); font-weight: 600; font-size: 12.5px; color: var(--muted); white-space: nowrap; }
tbody tr:last-child td { border-bottom: none; }
tbody tr:hover td { background: color-mix(in srgb, var(--surface-2) 55%, transparent); }
td:first-child { font-weight: 500; }

figure.block { margin: 14px 0 18px; border: 1px solid var(--line); border-radius: 10px; overflow: hidden; background: var(--code-bg); }
figure.block figcaption { display: flex; justify-content: space-between; align-items: center; padding: 6px 10px 6px 14px; border-bottom: 1px solid var(--line); background: var(--surface-2); font: 600 12px var(--font-body); letter-spacing: .02em; color: var(--muted); }
.copy { font: 500 11.5px var(--font-body); letter-spacing: 0; text-transform: none; color: var(--muted); background: transparent; border: 1px solid var(--line); border-radius: 6px; padding: 2px 9px; cursor: pointer; }
.copy:hover { color: var(--fg); background: var(--surface); }
figure.block pre { margin: 0; padding: 14px 16px; overflow-x: auto; }
figure.block pre code { font: 400 12.5px/1.6 var(--font-mono); background: none; border: 0; padding: 0; white-space: pre; word-break: normal; overflow-wrap: normal; }
figure.diagram pre code { font-family: var(--font-diagram); font-size: 13px; line-height: 1.45; }

/* 상태 배지 */
.status { display: inline-flex; align-items: center; gap: 6px; font-size: 12.5px; font-weight: 600; padding: 2px 9px 2px 8px; border-radius: 99px; border: 1px solid transparent; white-space: nowrap; }
.status::before { content: ""; width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
.st-wait { color: var(--muted); background: var(--surface-2); }
.st-doing { color: var(--info); background: var(--info-soft); }
.st-review { color: var(--warn); background: var(--warn-soft); }
.st-done { color: var(--ok); background: var(--ok-soft); }
.st-hold { color: var(--bad); background: var(--bad-soft); }

/* 작업 목록 */
ul.tasks { list-style: none; padding: 0; margin: 12px 0 18px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); overflow: hidden; max-width: none; }
li.task { display: grid; grid-template-columns: 22px minmax(0, 1fr); gap: 12px; margin: 0; padding: 11px 16px; border-bottom: 1px solid var(--line); }
li.task:last-child { border-bottom: none; }
li.task .box { width: 18px; height: 18px; border: 1.5px solid var(--line); border-radius: 5px; margin-top: 3px; background: var(--surface); position: relative; }
li.task.done .box { background: var(--ok); border-color: var(--ok); }
li.task.done .box::after { content: ""; position: absolute; left: 5px; top: 1.5px; width: 5px; height: 10px; border: solid var(--surface); border-width: 0 2px 2px 0; transform: rotate(45deg); }
li.task .thead:empty { display: none; }
li.task .thead { display: flex; gap: 8px; align-items: center; margin-bottom: 2px; }
code.tid { font: 600 12px/1.3 var(--font-mono); color: var(--accent); background: var(--accent-soft); border-color: transparent; padding: .15em .45em; word-break: normal; }
code.tid.test { color: var(--info); background: var(--info-soft); }
.kind { font-size: 11.5px; color: var(--muted); }
li.task .ttext { font-size: 14.5px; line-height: 1.65; }
li.task.done .ttext { color: var(--muted); }
li.task.done code.tid { color: var(--ok); background: var(--ok-soft); }
li.task ul { margin: 6px 0 0; }

footer.gen { margin-top: 72px; padding-top: 14px; border-top: 1px solid var(--line); color: var(--muted); font-size: 12.5px; display: flex; flex-wrap: wrap; gap: 8px 16px; justify-content: space-between; }

/* 시각 자료(목업/흐름도) */
.visual { margin: 16px 0 10px; }
details.alt { margin: 4px 0 18px; }
details.alt > summary { cursor: pointer; color: var(--muted); font-size: 13px; width: fit-content; }
.mock { background: var(--app-bg); color: var(--app-fg); border: 1px solid var(--app-line); border-radius: 12px; overflow: hidden; font-size: 13px; line-height: 1.5; box-shadow: var(--shadow); }
.mock b { font-weight: 600; }
.m-top, .m-foot, .m-tools, .m-proj, .m-conv, .m-kv, .m-create { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; }
.m-top { padding: 12px 16px 4px; font-size: 14px; }
.m-link { color: var(--app-muted); }
.m-sub { padding: 0 16px 10px; color: var(--app-muted); font-size: 12px; overflow-wrap: anywhere; }
.m-banner { margin: 0 16px 12px; padding: 8px 12px; background: var(--app-warn-bg); color: var(--app-warn); border-radius: 8px; }
.m-grow { flex: 1; }
.m-dim { color: var(--app-muted); }
.m-btn { background: var(--app-panel-2); border: 1px solid var(--app-line); border-radius: 6px; padding: 1px 9px; font-size: 12px; white-space: nowrap; }
.m-primary { background: var(--app-accent); color: var(--app-on-accent); border-radius: 7px; padding: 6px 14px; font-weight: 600; white-space: nowrap; }
.m-split { display: grid; grid-template-columns: minmax(0, 1.15fr) minmax(0, 1fr); border-top: 1px solid var(--app-line); border-bottom: 1px solid var(--app-line); }
.m-tree { padding: 12px 14px; border-right: 1px solid var(--app-line); display: grid; gap: 6px; align-content: start; min-width: 0; }
.m-detail { padding: 12px 16px; display: grid; gap: 7px; align-content: start; background: var(--app-panel); min-width: 0; }
.m-title { font-size: 14px; }
.m-search { background: var(--app-input); border: 1px solid var(--app-line); border-radius: 6px; padding: 1px 10px; color: var(--app-muted); min-width: 5em; }
.m-proj { padding: 4px 2px 0; }
.m-conv { padding: 3px 2px 3px 22px; }
.m-conv.m-child { padding-left: 48px; color: var(--app-muted); font-size: 12.5px; }
.m-folder { margin-left: 22px; padding: 8px 10px; background: var(--app-panel); border: 1px solid var(--app-line); border-radius: 8px; display: grid; gap: 5px; }
.m-cap { color: var(--app-muted); font-size: 11.5px; font-weight: 600; display: flex; gap: 6px; align-items: center; }
.m-kv > span:first-child { color: var(--app-muted); min-width: 4.5em; font-size: 12px; }
.m-path { font-family: var(--font-mono); font-size: 11.5px; overflow-wrap: anywhere; }
.m-input { background: var(--app-input); border: 1px solid var(--app-line); border-radius: 5px; padding: 0 8px; font-family: var(--font-mono); font-size: 11.5px; flex: 1; min-width: 8em; }
.m-input.sm { flex: 0 1 auto; min-width: 5em; }
.m-create { font-size: 12.5px; }
.m-spark { color: var(--app-warn); font-weight: 600; }
.m-cb { width: 13px; height: 13px; border: 1.5px solid var(--app-muted); border-radius: 3px; flex: none; position: relative; }
.m-cb.on { background: var(--app-accent); border-color: var(--app-accent); }
.m-cb.on::after { content: ""; position: absolute; left: 3.5px; top: 0.5px; width: 3.5px; height: 7px; border: solid var(--app-on-accent); border-width: 0 1.5px 1.5px 0; transform: rotate(45deg); }
.m-cb.part::after { content: ""; position: absolute; left: 2px; right: 2px; top: 4.5px; height: 1.5px; background: var(--app-fg); }
.m-badge { font-size: 11.5px; border-radius: 99px; padding: 0 8px; border: 1px solid var(--app-line); white-space: nowrap; width: fit-content; }
.b-gray { color: var(--app-muted); }
.b-green { color: var(--app-ok); border-color: var(--app-ok); }
.m-tag { font-family: var(--font-mono); font-size: 10.5px; color: var(--app-muted); border: 1px dashed var(--app-line); border-radius: 4px; padding: 0 5px; }
.m-msg { background: var(--app-panel-2); border-radius: 8px; padding: 6px 10px; color: var(--app-muted); }
.m-msg.m-you { background: var(--app-bubble); margin-left: 24px; }
.m-foot { padding: 12px 16px; font-size: 12.5px; }
.flow { display: grid; grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr); gap: 14px; align-items: center; }
.room { background: var(--surface); border: 1px solid var(--line); border-radius: 12px; padding: 14px 18px; box-shadow: var(--shadow); min-width: 0; }
.room-h { font-weight: 700; margin-bottom: 6px; }
.room ol { list-style: none; padding: 0; margin: 0; display: grid; gap: 4px; font-size: 14px; }
.room li b { color: var(--accent); font-weight: 600; margin-right: 4px; }
.flow-links { display: grid; gap: 10px; }
.flow-arrow { font-size: 12.5px; color: var(--muted); background: var(--surface-2); border-radius: 99px; padding: 4px 12px; white-space: nowrap; text-align: center; }
.flow-arrow span { color: var(--accent); font-weight: 700; }

@media (max-width: 960px) {
  .shell { grid-template-columns: minmax(0, 1fr); }
  .rail { position: static; height: auto; max-height: none; border-right: none; border-bottom: 1px solid var(--line); padding-block: 14px; }
  .rail .rail-title { display: none; }
  .rail details:not([open]) .toc { display: none; }
  .anchor { display: none; }
  .fields > div { grid-template-columns: minmax(0, 1fr); gap: 2px; }
  .dash-row { grid-template-columns: minmax(0, 1fr) 70px 54px; gap: 10px; padding-inline: 14px; }
}
@media (max-width: 720px) {
  .m-split { grid-template-columns: minmax(0, 1fr); }
  .m-tree { border-right: none; border-bottom: 1px solid var(--app-line); }
  .flow { grid-template-columns: minmax(0, 1fr); }
  .flow-links { grid-auto-flow: column; justify-content: center; }
}
@media (prefers-reduced-motion: no-preference) { html { scroll-behavior: smooth; } }
@media print { .rail, .btn, .copy { display: none; } .shell { display: block; } body { background: #fff; } }
</style>
</head>
<body>
<div class="shell">
<nav class="rail" aria-label="목차">
  <div class="brand">Codex Backup Manager</div>
  <div class="rail-title">__TITLE__</div>
  <details id="toc-details" open><summary>목차</summary>
  <div class="toc">
__TOC__
  </div></details>
</nav>
<main>
<div class="col">
<header class="masthead">
  <div class="eyebrow">__EYEBROW__</div>
  <h1>__TITLE__</h1>
  <div class="meta"><span>원본 <code>__SOURCE__</code></span><span>생성 __GENERATED__</span><span class="spacer"></span>
  <button type="button" class="btn" id="theme">밝게 / 어둡게</button></div>
__OVERVIEW__
</header>
__DASH__
__BODY__
<footer class="gen"><span>이 HTML은 <code>__SOURCE__</code>에서 <code>scripts/render-docs.py</code>로 생성했습니다. .md를 고친 뒤 다시 생성하세요.</span><a href="#top" id="to-top">맨 위로</a></footer>
</div>
</main>
</div>
<script>
(function () {
  var root = document.documentElement, key = 'cbm-doc-theme';
  try { var saved = localStorage.getItem(key); if (saved) root.setAttribute('data-theme', saved); } catch (e) {}
  document.getElementById('theme').addEventListener('click', function () {
    var cur = root.getAttribute('data-theme');
    var dark = cur ? cur === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches;
    var next = dark ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem(key, next); } catch (e) {}
  });
  document.getElementById('to-top').addEventListener('click', function (e) { e.preventDefault(); window.scrollTo(0, 0); });
  if (window.matchMedia('(max-width: 960px)').matches) { document.getElementById('toc-details').removeAttribute('open'); }

  document.querySelectorAll('button.copy').forEach(function (b) {
    b.addEventListener('click', function () {
      var text = b.closest('figure').querySelector('code').innerText;
      var done = function () { b.textContent = '복사됨'; setTimeout(function () { b.textContent = '복사'; }, 1400); };
      var fallback = function () {
        var r = document.createRange(); r.selectNodeContents(b.closest('figure').querySelector('code'));
        var s = window.getSelection(); s.removeAllRanges(); s.addRange(r); b.textContent = '선택됨';
      };
      if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(text).then(done, fallback); } else { fallback(); }
    });
  });

  var links = {}; document.querySelectorAll('.toc a').forEach(function (a) { links[a.getAttribute('href').slice(1)] = a; });
  var heads = Array.prototype.slice.call(document.querySelectorAll('main h2[id], main h3[id]')).filter(function (h) { return links[h.id]; });
  if ('IntersectionObserver' in window && heads.length) {
    var current = null;
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) { if (en.isIntersecting) { current = en.target.id; } });
      if (!current) return;
      Object.keys(links).forEach(function (k) { links[k].classList.toggle('active', k === current); });
    }, { rootMargin: '0px 0px -70% 0px' });
    heads.forEach(function (h) { io.observe(h); });
  }
})();
</script>
</body>
</html>
"""


def eyebrow_for(md_path: Path) -> str:
    name = md_path.stem
    if "plan" in name:
        return "구현 계획 · 진행 체크리스트"
    if "redesign" in name or "design" in name:
        return "설계 문서"
    return "문서"


def render_file(md_path: Path, rendered: set[str]) -> Path:
    r = Renderer(rendered)
    body = r.render(md_path.read_text(encoding="utf-8").splitlines())

    toc = []
    for s in r.sections:
        num = re.search(r'<span class="num">§?([^<]+)</span>', s.title_html)
        phase = re.search(r'<span class="phase">([^<]+)</span>', s.title_html)
        label = strip_tags(re.sub(r'<span class="(num|phase)">[^<]*</span>', "", s.title_html))
        if phase:
            label = f"{phase.group(1)} {label}"
        count = ""
        if s.level == 2 and s.total:
            full = " full" if s.done == s.total else ""
            count = f'<span class="count{full}">{s.done}/{s.total}</span>'
        toc.append(
            f'<a class="l{s.level}" href="#{s.slug}"><span class="tnum">{html.escape(num.group(1)) if num else ""}</span>'
            f'<span class="tlabel">{html.escape(label)}</span>{count}</a>'
        )

    dash = ""
    task_secs = [s for s in r.sections if s.level == 2 and s.total]
    if r.total:
        pct = round(r.done * 100 / r.total)
        rows = []
        for s in task_secs:
            p = s.done * 100 / s.total
            full = " full" if s.done == s.total else ""
            phase = re.search(r'<span class="phase">([^<]+)</span>', s.title_html)
            label = strip_tags(re.sub(r'<span class="(num|phase)">[^<]*</span>', "", s.title_html))
            chip = f'<span class="phase">{phase.group(1)}</span>' if phase else ""
            rows.append(
                f'<a class="dash-row{full}" href="#{s.slug}"><span class="dname">{chip}<span>{html.escape(label)}</span></span>'
                f'<span class="meter" role="img" aria-label="{s.done}/{s.total} 완료"><i style="width:{p:.1f}%"></i></span>'
                f'<span class="dcount">{s.done}/{s.total}</span></a>'
            )
        dash = (
            f'<section class="dash" aria-label="진행 현황"><div class="dash-head"><strong>단계별 진행</strong>'
            f'<span class="dash-total">{pct}% <small>{r.done} / {r.total} 항목 점검 완료</small></span></div>'
            f'<div class="dash-rows">{"".join(rows)}</div></section>'
        )

    overview = f'<div class="overview">{r.overview}</div>' if r.overview else ""
    title = html.escape(r.title or md_path.stem)
    page = (
        PAGE.replace("__TITLE__", title)
        .replace("__EYEBROW__", eyebrow_for(md_path))
        .replace("__SOURCE__", html.escape(md_path.as_posix()))
        .replace("__GENERATED__", datetime.now().strftime("%Y-%m-%d %H:%M"))
        .replace("__TOC__", "\n".join(toc))
        .replace("__OVERVIEW__", overview)
        .replace("__DASH__", dash)
        .replace("__BODY__", body)
    )
    page = page.replace('<div class="shell">', '<div class="shell" id="top">', 1)
    out = md_path.with_suffix(".html")
    out.write_text(page, encoding="utf-8")
    return out


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2
    paths = [Path(a) for a in argv[1:]]
    rendered = {p.name for p in paths} | {p.with_suffix(".md").name for p in Path("docs").glob("*.html")}
    for p in paths:
        if not p.exists():
            print(f"not found: {p}", file=sys.stderr)
            return 1
        print(f"rendered: {render_file(p, rendered)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
