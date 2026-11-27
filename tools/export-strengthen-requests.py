"""从角色委托表和图鉴的立绘索引生成强化委托配置。依赖 openpyxl、Pillow、numpy、opencv-python。"""
import argparse
import base64
import csv
import json
import re
from pathlib import Path
from urllib.parse import urlparse
from urllib.request import url2pathname

import cv2
import numpy as np
from openpyxl import load_workbook
from PIL import Image


def portrait_patch(path):
    # 在上半身内取前景不透明、边缘清晰的局部，避开透明背景和详情卡底边。
    rgba = np.asarray(Image.open(path).convert("RGBA"))
    gray = cv2.cvtColor(rgba, cv2.COLOR_RGBA2GRAY)
    best = None
    for y in range(0, min(len(gray) // 2, len(gray) - 128), 16):
        for x in range(0, gray.shape[1] - 128, 16):
            if rgba[y:y+128, x:x+128, 3].min() < 250:
                continue
            patch = gray[y:y+128, x:x+128]
            gx = cv2.Sobel(patch, cv2.CV_32F, 1, 0)
            gy = cv2.Sobel(patch, cv2.CV_32F, 0, 1)
            quality = np.mean(np.abs(gx)) * np.mean(np.abs(gy))
            if best is None or quality > best[0]:
                best = (quality, patch)
    if best is None:
        raise ValueError(f"缺少足够大的立绘前景: {path.name}")
    small = cv2.resize(best[1], (48, 48), interpolation=cv2.INTER_AREA)
    return base64.b64encode(cv2.imencode(".png", small)[1]).decode("ascii")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ("workbook", "image_index", "output"):
        parser.add_argument(arg, type=Path)
    args = parser.parse_args()
    with args.image_index.open(encoding="utf-8-sig", newline="") as f:
        portraits = {r["ID"]: Path(url2pathname(urlparse(r["原图URI"]).path))
                     for r in csv.DictReader(f) if r["类别"] == "人物" and r["图片类别"] == "人物立绘"}
    book = load_workbook(args.workbook, read_only=True, data_only=True)
    roles = {}
    for row in book["角色委托"].iter_rows(min_row=5, values_only=True):
        name, _, title, _, kind, amount = row[:6]
        match = re.fullmatch(r"强化部队([0-9]+)", str(title))
        if not match or kind != "强化指定系列单位":
            continue
        role_id = str(row[12])
        if role_id not in roles:
            roles[role_id] = dict(Id=role_id, Name=name, Portrait=portrait_patch(portraits[role_id]), Requests={})
        offers = roles[role_id]["Requests"]
        number = match[1]
        if number in offers or not 1 <= amount <= 5:
            raise ValueError(f"委托数据有歧义: {name} {title}")
        offers[number] = dict(Id=str(row[11]), Series=row[9], Amount=amount)
    book.close()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(list(roles.values()), ensure_ascii=False, indent=2) + "\n", encoding="utf8")


if __name__ == "__main__":
    main()
