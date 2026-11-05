"""Refresh the source embedded in DeckCapture's standalone preview export."""

from __future__ import annotations

import html
from pathlib import Path
import re


START = "<!-- DECKCAPTURE_PREVIEW_SOURCE_START -->"
END = "<!-- DECKCAPTURE_PREVIEW_SOURCE_END -->"


def update_preview() -> None:
    repo = Path(__file__).resolve().parents[1]
    preview_dir = repo / "docs" / "preview"
    source = (preview_dir / "deckcapture.fragment.html").read_text(encoding="utf-8").strip()
    output_path = preview_dir / "index.html"
    document = output_path.read_text(encoding="utf-8")

    if source.count(START) != 1 or source.count(END) != 1:
        raise ValueError("The source needs exactly one pair of preview boundary comments.")
    if not source.startswith(START) or not source.endswith(END):
        raise ValueError("Keep all preview source between its boundary comments.")

    frames = list(re.finditer(r'data-srcdoc="([^"]*)"', document))
    if len(frames) != 1:
        raise ValueError("Expected one embedded preview document in index.html.")
    frame = frames[0]
    embedded = html.unescape(frame.group(1))
    if embedded.count(START) != 1 or embedded.count(END) != 1:
        raise ValueError("The exported preview needs one pair of source boundary comments.")
    pattern = re.compile(re.escape(START) + r".*?" + re.escape(END), re.DOTALL)
    updated, count = pattern.subn(lambda _: source, embedded)
    if count != 1:
        raise ValueError("The exported source could not be located uniquely.")

    encoded = html.escape(updated, quote=True)
    result = document[: frame.start(1)] + encoded + document[frame.end(1) :]
    output_path.write_text(result, encoding="utf-8", newline="\n")
    print(f"Updated {output_path}")


if __name__ == "__main__":
    update_preview()
