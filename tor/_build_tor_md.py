"""Build a single TOR.md combining PDF and DOCX content with image references.

Layout:
  # Terms of Reference (TOR) — consolidated

  > Source files preserved at tor/ocds-.../ ; images extracted to tor/images/.

  ## Part A — PDF: <pdf filename>
  (page-by-page, image refs inserted on the page where they appear)

  ## Part B — DOCX: <docx filename>
  (paragraphs + tables in document order, single embedded image referenced)
"""
import json
import os
import re
import sys
from collections import defaultdict

import fitz  # pymupdf
from docx import Document
from docx.document import Document as _DocCls
from docx.oxml.ns import qn
from docx.table import Table
from docx.text.paragraph import Paragraph

sys.stdout.reconfigure(encoding="utf-8")

ROOT = r"C:/Users/evisoft/source/repos/cnas/tor"
PDF = rf"{ROOT}/ocds-b3wdp1-MD-1775223422648-EV-1775223852969/W0Y595-Caietul de sarcini 17.04.2026 modificat.semnat.pdf"
DOCX = rf"{ROOT}/ocds-b3wdp1-MD-1775223422648-EV-1775223852969/f7r_ir-ds_servicii_omf_115_15_09_2021 dezvoltare SI Protecția Socială CNAS 2026.docx"
OUT_MD = rf"{ROOT}/TOR.md"
MANIFEST = rf"{ROOT}/images/manifest.json"

PDF_NAME = os.path.basename(PDF)
DOCX_NAME = os.path.basename(DOCX)

# ---------- Load image manifest, group PDF images by page ----------
with open(MANIFEST, encoding="utf-8") as f:
    manifest = json.load(f)

pdf_imgs_by_page = defaultdict(list)
pdf_full_by_page = {}
docx_imgs = []
for entry in manifest:
    if entry["source"] == "pdf":
        if entry.get("kind") == "page-full":
            pdf_full_by_page[entry["page"]] = entry["file"]
        else:
            pdf_imgs_by_page[entry["page"]].append(entry["file"])
    else:
        docx_imgs.append(entry["file"])

# ---------- PDF extraction ----------
def extract_pdf_pages(pdf_path):
    doc = fitz.open(pdf_path)
    pages = []
    for i, page in enumerate(doc):
        text = page.get_text("text")
        # Normalize whitespace runs that PDF text has (lots of trailing spaces)
        # Keep blank lines but trim trailing spaces on each line.
        text = "\n".join(line.rstrip() for line in text.splitlines())
        # Collapse 3+ blank lines down to 2
        text = re.sub(r"\n{3,}", "\n\n", text).strip("\n")
        pages.append(text)
    return pages

pdf_pages = extract_pdf_pages(PDF)

# ---------- DOCX extraction ----------
def iter_block_items(parent):
    """Yield paragraphs and tables in document order."""
    if isinstance(parent, _DocCls):
        parent_elm = parent.element.body
    else:
        parent_elm = parent._element
    for child in parent_elm.iterchildren():
        if child.tag == qn("w:p"):
            yield Paragraph(child, parent)
        elif child.tag == qn("w:tbl"):
            yield Table(child, parent)

def md_escape(text):
    # Don't mangle the source words; just escape pipes and collapse newlines/runs of whitespace.
    text = text.replace("|", "\\|")
    text = re.sub(r"\s+", " ", text)
    return text.strip()

def paragraph_to_md(p: Paragraph) -> str:
    text = p.text
    if not text.strip():
        return ""
    style = p.style.name if p.style else "Normal"
    style_l = style.lower()
    if "heading 1" in style_l:
        return f"### {text}"
    if "heading 2" in style_l:
        return f"#### {text}"
    if "heading 3" in style_l:
        return f"##### {text}"
    if "heading 4" in style_l or "heading 5" in style_l or "heading 6" in style_l:
        return f"###### {text}"
    if "title" in style_l:
        return f"### {text}"
    if "list" in style_l:
        return f"- {text}"
    return text

def table_to_md(t: Table) -> str:
    rows = []
    for row in t.rows:
        cells = [md_escape(cell.text) for cell in row.cells]
        rows.append(cells)
    if not rows:
        return ""
    width = max(len(r) for r in rows)
    # Pad short rows
    rows = [r + [""] * (width - len(r)) for r in rows]
    header = rows[0]
    sep = ["---"] * width
    body = rows[1:] if len(rows) > 1 else []
    out = []
    out.append("| " + " | ".join(header) + " |")
    out.append("| " + " | ".join(sep) + " |")
    for r in body:
        out.append("| " + " | ".join(r) + " |")
    return "\n".join(out)

def extract_docx_blocks(docx_path):
    doc = Document(docx_path)
    parts = []
    for block in iter_block_items(doc):
        if isinstance(block, Paragraph):
            md = paragraph_to_md(block)
            if md:
                parts.append(md)
        elif isinstance(block, Table):
            md = table_to_md(block)
            if md:
                parts.append("")  # blank line before
                parts.append(md)
                parts.append("")  # blank line after
    return parts

docx_blocks = extract_docx_blocks(DOCX)

# ---------- Build TOR.md ----------
lines = []
lines.append("# Terms of Reference (TOR) — Consolidated")
lines.append("")
lines.append(
    "> **Project:** Servicii de elaborare și implementare a unui nou sistem informațional „Protecția Socială” pentru anii 2026–2028 (CNAS)."
)
lines.append(
    "> **Sources merged here:** the official PDF *Caietul de sarcini* and the standard DOCX *Documentația standard* (Anexa nr.1 la Ordinul ministrului finanțelor nr.115 din 15.09.2021)."
)
lines.append(
    f"> **Original files:** [`tor/ocds-.../{PDF_NAME}`](ocds-b3wdp1-MD-1775223422648-EV-1775223852969/{PDF_NAME}) · [`tor/ocds-.../{DOCX_NAME}`](ocds-b3wdp1-MD-1775223422648-EV-1775223852969/{DOCX_NAME})"
)
_pdf_clip_count = sum(len(v) for v in pdf_imgs_by_page.values())
_pdf_full_count = len(pdf_full_by_page)
lines.append(
    f"> **Images extracted to:** `tor/images/` — PDF: {_pdf_clip_count} image clips + {_pdf_full_count} full-page renders (200 DPI, mask-composited); DOCX: {len(docx_imgs)}. See `tor/images/manifest.json` for metadata."
)
lines.append("")
lines.append("---")
lines.append("")
lines.append("## Table of Contents")
lines.append("")
lines.append(f"- [Part A — PDF: Caietul de sarcini](#part-a--pdf-caietul-de-sarcini)")
lines.append(f"- [Part B — DOCX: Documentația standard](#part-b--docx-documentația-standard)")
lines.append("- [Image Index](#image-index)")
lines.append("")
lines.append("---")
lines.append("")

# ---- Part A: PDF ----
lines.append("## Part A — PDF: Caietul de sarcini")
lines.append("")
lines.append(f"*Source: `{PDF_NAME}` ({len(pdf_pages)} pages).*")
lines.append("")

for page_idx, page_text in enumerate(pdf_pages, start=1):
    lines.append(f"### PDF Page {page_idx}")
    lines.append("")
    if page_text.strip():
        lines.append(page_text)
        lines.append("")
    full = pdf_full_by_page.get(page_idx)
    if full:
        lines.append(f"**Full-page render (page {page_idx}):**")
        lines.append("")
        lines.append(f"![PDF p{page_idx} — full page](images/{full})")
        lines.append("")
    imgs = pdf_imgs_by_page.get(page_idx, [])
    if imgs:
        lines.append(
            f"<details><summary>Individual image clips on page {page_idx} ({len(imgs)})</summary>"
        )
        lines.append("")
        for fname in imgs:
            lines.append(f"![PDF p{page_idx} — {fname}](images/{fname})")
            lines.append("")
        lines.append("</details>")
        lines.append("")
    lines.append("---")
    lines.append("")

# ---- Part B: DOCX ----
lines.append("## Part B — DOCX: Documentația standard")
lines.append("")
lines.append(f"*Source: `{DOCX_NAME}`.*")
lines.append("")
if docx_imgs:
    lines.append("**Embedded media (full DOCX content follows below):**")
    lines.append("")
    for fname in docx_imgs:
        # WMF won't render in most markdown viewers — link it as a file.
        if fname.lower().endswith(".wmf"):
            lines.append(f"- [`{fname}`](images/{fname}) (Windows Metafile — open in Word/LibreOffice)")
        else:
            lines.append(f"![DOCX — {fname}](images/{fname})")
    lines.append("")

# Body blocks
for block in docx_blocks:
    lines.append(block)
    lines.append("")

# ---- Image index ----
lines.append("---")
lines.append("")
lines.append("## Image Index")
lines.append("")
lines.append("| # | File | Source | Kind | Page / Origin |")
lines.append("|---|------|--------|------|---------------|")
idx = 0
for entry in manifest:
    idx += 1
    file = entry["file"]
    src = entry["source"]
    kind = entry.get("kind", "media") if src == "pdf" else "media"
    if src == "pdf":
        origin = f"PDF page {entry.get('page')}"
    else:
        origin = f"DOCX `{entry.get('original','')}`"
    lines.append(f"| {idx} | [`{file}`](images/{file}) | {src} | {kind} | {origin} |")

lines.append("")

# Write out
with open(OUT_MD, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines))

# Stats
total_chars = sum(len(p) for p in pdf_pages)
print(f"PDF pages: {len(pdf_pages)}, PDF text chars: {total_chars}")
print(f"DOCX blocks: {len(docx_blocks)}")
print(f"TOR.md size: {os.path.getsize(OUT_MD):,} bytes")
print(f"Written: {OUT_MD}")
