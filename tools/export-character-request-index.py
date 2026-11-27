"""从同版委托工作簿、已解析主数据及 OCTO 资源生成关系索引、开发起点和原生 PNG。"""
import argparse
import csv
import json
import re
from collections import defaultdict
from pathlib import Path
from shutil import copyfile

from openpyxl import load_workbook
from PIL import Image


def read_table(master, name, folder="业务表"):
    return json.loads((master / folder / (name + ".json")).read_bytes())


def store_json(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def collect(workbook, snapshot, resources):
    master = snapshot / "已解析主数据"
    art = snapshot / "OCTO美术"
    book = load_workbook(workbook, read_only=True, data_only=True)
    try:
        rows = list(book["角色委托"].iter_rows(min_row=5, values_only=True))
        refs = {str(r[0]): r for r in book["引用明细"].iter_rows(min_row=5, values_only=True)}
    finally:
        book.close()
    names = {r["id"]: r["value"] for r in read_table(master, "m_series", "文本表")["data"]}
    works = {str(r[0]): names[r[2]] for r in read_table(master, "m_series")}
    memberships = defaultdict(list)
    for key, series, order in sorted(read_table(master, "m_series_set"), key=lambda r: r[2]):
        memberships[key].append(str(series))
    units = {r[0]: r for r in read_table(master, "m_unit")}
    characters = {r[0]: r for r in read_table(master, "m_character")}
    unit_names = {r["id"]: r["value"] for r in read_table(master, "m_unit", "文本表")["data"]}
    # Texture2D 可能是带边距的整张图集。路线识别需要相同资源键的原生 Sprite。
    sprites = {}
    with (art / "assets.csv").open(encoding="utf-8-sig", newline="") as stream:
        for r in csv.DictReader(stream):
            if r["type"] == "Sprite" and r["name"].startswith(("ogk_", "cb_")) and r["path"].endswith(".png"):
                sprites[r["name"]] = art / r["path"]

    def copy_sprite(key, folder):
        filename = key + ".png"
        target = resources / "templates" / folder / filename
        source = sprites[key]
        with Image.open(source) as image:
            if image.mode != "RGBA":
                raise ValueError(f"素材缺少透明通道: {source}")
            image.verify()
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            with Image.open(target) as previous, Image.open(source) as current:
                if previous.size == current.size and previous.convert("RGBA").tobytes() == current.tobytes():
                    return "resources/templates/" + folder + "/" + filename
        copyfile(source, target)
        return "resources/templates/" + folder + "/" + filename

    roles = {}
    offers = {}
    unit_ids = set()
    first_deliveries = defaultdict(list)
    role_works = defaultdict(set)
    for r in rows:
        if str(r[2]).startswith("委托开发"):
            role_works[str(r[12])].add(str(refs[str(r[11])][15]))
    for name, _, title, _, kind, amount, _, unit, _, _, _, offer, role, *_ in rows:
        offer, role = str(offer), str(role)
        ref = refs[offer]
        if str(ref[1]) != role or ref[14] != unit or offer in offers:
            raise ValueError(f"委托 {offer} 的角色或机体引用不一致，或委托 ID 重复")
        if role not in roles:
            character = characters[int(role)]
            sprite = "cb_" + character[2]
            roles[role] = dict(Name=name, SeriesIds=memberships[character[15]],
                               Portrait=copy_sprite(sprite, "base/character_requests/portraits"))
        link = dict(RoleId=role, Title=title, Kind=kind, Amount=amount)
        if ref[15]:
            link["SeriesId"] = str(ref[15])
        if unit:
            unit_ids.add(int(unit))
            link["UnitId"] = str(unit)
            if title == "交付机体1":
                for series in role_works[role]:
                    first_deliveries[series].append(int(unit))
        offers[offer] = link

    diagrams = read_table(master, "m_unit_development_diagram")
    pieces = read_table(master, "m_unit_development_piece")
    materials = defaultdict(dict)
    for unit, item, count in read_table(master, "m_unit_development_material"):
        materials[unit][str(item)] = count
    bases = {}
    for work in read_table(master, "m_unit_development_series"):
        series = str(work[0])
        # 挑战入口默认第一张普通路线图。按前置节点、额外材料和单位作品核对可直接生产的根。
        diagram = min((g for g in diagrams if g[1] == work[0] and not g[7]), key=lambda g: (g[6], g[0]))
        roots = [n for n in pieces if n[1] == diagram[0] and n[7] == 0]
        credited = [n for n in roots if series in memberships[units[n[4]][22]]]
        if credited:
            roots = credited
        preferred = first_deliveries[series]
        def cost(n):
            unit = n[4]
            return (sum(materials[unit].values()), units[unit][31], n[3], preferred.index(unit) if unit in preferred else len(preferred), n[2], unit)
        node = min(roots, key=cost)
        unit = node[4]
        unit_ids.add(unit)
        bases[series] = dict(SeriesName=works[series], UnitId=str(unit), UnitName=unit_names[units[unit][6]],
                             DiagramId=str(diagram[0]), NodeId=str(node[0]),
                             Column=node[3], Row=node[2], Capital=units[unit][31], Materials=materials[unit])
    catalog_units = {}
    for unit in sorted(unit_ids):
        row = units[unit]
        # 个别机体的棋子键与立绘键不同，例如高达 00150 和 00100。
        catalog_units[str(unit)] = dict(Name=unit_names[row[6]], SeriesIds=memberships[row[22]],
                                        Roadmap=copy_sprite("ogk_" + row[4], "develop/dev_unit"))
    if set(offers) != set(refs):
        raise ValueError("两张工作表的委托 ID 集合不一致")
    for offer in offers.values():
        if re.fullmatch(r"委托开发\d+", offer["Title"]):
            series = offer["SeriesId"]
            base = bases[series]
            if base["Materials"] or base["Capital"] <= 0 or series not in catalog_units[base["UnitId"]]["SeriesIds"]:
                raise ValueError(f"委托作品 {series} 缺少仅需 CAPITAL 且能计入进度的起点")
    for entity in [*roles.values(), *catalog_units.values()]:
        if not entity["SeriesIds"] or any(s not in works for s in entity["SeriesIds"]):
            raise ValueError(f"未知作品: {entity}")
    store_json(resources / "data/character_request_index.json",
               dict(Series=works, Roles=roles, Units=catalog_units, Offers=offers))
    store_json(resources / "data/series_development_bases.json", bases)
    print(f"{len(roles)} 人物，{len(catalog_units)} 单位，{len(offers)} 委托，{len(bases)} 个作品起点")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workbook", type=Path)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("resources", type=Path, help="GGEA 的 resources/common 目录")
    args = parser.parse_args()
    collect(args.workbook, args.snapshot, args.resources)
