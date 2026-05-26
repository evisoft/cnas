"""Extract images from PDF and DOCX into tor/images/.

PDF strategy: many images in the source PDF have a separate soft-mask (alpha)
stream. Calling Document.extract_image() on those returns just the raw color
stream and they render as solid black. Instead, we use page.get_image_info()
to get each image's bounding box on the page, then page.get_pixmap(clip=bbox)
which composites the image with its mask exactly as it appears on the page.

Bonus: we also render whole pages that contain images as full-page PNGs, so the
architecture diagrams (where many small images stack into one figure) can be
viewed as a single picture.

Produces:
  - pdf_p{page:03d}_{n}.png       per-image clip
  - pdf_p{page:03d}_full.png      full page render (only for image-bearing pages)
  - docx_image{n}.{ext}           DOCX media files (verbatim)

Writes manifest.json with metadata for every emitted file.
"""
import json
import os
import sys
import zipfile

import fitz  # pymupdf

sys.stdout.reconfigure(encoding="utf-8")

ROOT = r"C:/Users/evisoft/source/repos/cnas/tor"
PDF = rf"{ROOT}/ocds-b3wdp1-MD-1775223422648-EV-1775223852969/W0Y595-Caietul de sarcini 17.04.2026 modificat.semnat.pdf"
DOCX = rf"{ROOT}/ocds-b3wdp1-MD-1775223422648-EV-1775223852969/f7r_ir-ds_servicii_omf_115_15_09_2021 dezvoltare SI Protecția Socială CNAS 2026.docx"
OUT = rf"{ROOT}/images"
os.makedirs(OUT, exist_ok=True)

# Clear any old PDF outputs to avoid stale black PNGs lingering
for f in os.listdir(OUT):
    if f.startswith("pdf_p"):
        os.remove(os.path.join(OUT, f))

manifest = []

# ---- PDF: clip each image's bbox from the rendered page ----
DPI = 200  # render resolution for clips and full pages
ZOOM = DPI / 72.0
matrix = fitz.Matrix(ZOOM, ZOOM)

doc = fitz.open(PDF)
total_clips = 0
pages_with_images = []

for page_idx, page in enumerate(doc):
    info = page.get_image_info(xrefs=True)
    if not info:
        continue
    pages_with_images.append(page_idx + 1)

    # Per-image clips
    for i, item in enumerate(info, start=1):
        bbox = fitz.Rect(item["bbox"])
        if bbox.is_empty or bbox.is_infinite or bbox.width < 1 or bbox.height < 1:
            continue
        try:
            pix = page.get_pixmap(clip=bbox, matrix=matrix, alpha=False)
        except Exception as e:
            print(f"  ! page {page_idx+1} image {i}: clip render failed: {e}")
            continue
        fname = f"pdf_p{page_idx+1:03d}_{i}.png"
        path = os.path.join(OUT, fname)
        pix.save(path)
        total_clips += 1
        manifest.append(
            {
                "file": fname,
                "source": "pdf",
                "page": page_idx + 1,
                "kind": "image-clip",
                "xref": item.get("xref"),
                "bbox": list(bbox),
                "width_px": pix.width,
                "height_px": pix.height,
                "has_mask": item.get("has-mask"),
            }
        )

    # Full-page render for any page that has images (so diagrams stay viewable as a whole)
    try:
        page_pix = page.get_pixmap(matrix=matrix, alpha=False)
        full_name = f"pdf_p{page_idx+1:03d}_full.png"
        page_pix.save(os.path.join(OUT, full_name))
        manifest.append(
            {
                "file": full_name,
                "source": "pdf",
                "page": page_idx + 1,
                "kind": "page-full",
                "width_px": page_pix.width,
                "height_px": page_pix.height,
            }
        )
    except Exception as e:
        print(f"  ! page {page_idx+1}: full-page render failed: {e}")

print(f"PDF image clips: {total_clips} across {len(pages_with_images)} pages")
print(f"PDF full-page renders: {len(pages_with_images)}")

# ---- DOCX: copy media as-is ----
docx_count = 0
with zipfile.ZipFile(DOCX) as z:
    for name in z.namelist():
        if not name.startswith("word/media/"):
            continue
        data = z.read(name)
        base = os.path.basename(name)
        out_name = f"docx_{base}"
        with open(os.path.join(OUT, out_name), "wb") as f:
            f.write(data)
        docx_count += 1
        manifest.append(
            {
                "file": out_name,
                "source": "docx",
                "original": name,
                "bytes": len(data),
            }
        )

print(f"DOCX images: {docx_count}")

with open(os.path.join(OUT, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)

print(f"Manifest entries: {len(manifest)}")
