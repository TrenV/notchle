#!/usr/bin/env python3
"""Cut saved Spotify embed pages down to what the parsers read.

Keeps the original `<script id="__NEXT_DATA__" ...>` tag and, inside it, only the path
props.pageProps.state.data.entity with the entity's name/title/subtitle/uri/type and, per
track, uri/title/subtitle/duration/isPlayable/entityType/audioPreview.url. Everything else
(page scripts, styles, images, Spotify's web-player config and access token) is dropped.
Idempotent: running it on an already-shrunk page changes nothing.
"""
import json
import pathlib
import re
import sys

ENTITY_KEYS = ("name", "title", "subtitle", "uri", "type")
TRACK_KEYS = ("uri", "title", "subtitle", "duration", "isPlayable", "entityType")

def shrink(html: str) -> str:
    m = re.search(r'(<script id="__NEXT_DATA__"[^>]*>)(.*?)</script>', html, re.S)
    if not m:
        raise ValueError("no __NEXT_DATA__ script")
    open_tag, data = m.group(1), json.loads(m.group(2))
    entity = data["props"]["pageProps"]["state"]["data"]["entity"]
    small = {k: entity[k] for k in ENTITY_KEYS if k in entity}
    tracks = []
    for item in entity.get("trackList", []):
        t = {k: item[k] for k in TRACK_KEYS if k in item}
        preview = item.get("audioPreview")
        if isinstance(preview, dict) and "url" in preview:
            t["audioPreview"] = {"url": preview["url"]}
        tracks.append(t)
    small["trackList"] = tracks
    doc = {"props": {"pageProps": {"state": {"data": {"entity": small}}}}}
    body = json.dumps(doc, ensure_ascii=False, separators=(",", ":"))
    return f"<!DOCTYPE html><html><head>{open_tag}{body}</script></head><body></body></html>\n"

if __name__ == "__main__":
    for arg in sys.argv[1:]:
        path = pathlib.Path(arg)
        before = path.stat().st_size
        path.write_text(shrink(path.read_text(encoding="utf-8")), encoding="utf-8")
        print(f"{path.name}: {before} -> {path.stat().st_size} bytes")
