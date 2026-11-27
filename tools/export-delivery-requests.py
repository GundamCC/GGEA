"""从角色委托表导出一次交付一台机体的普通委托。"""
import argparse
import json
import re
from pathlib import Path

from openpyxl import load_workbook


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workbook", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    book = load_workbook(args.workbook, read_only=True, data_only=True)
    roles = {}
    for row in book["角色委托"].iter_rows(min_row=5, values_only=True):
        match = re.fullmatch(r"交付机体([0-9]+)", str(row[2]))
        if not match or row[4] != "交付机体" or row[5] != 1 or not str(row[7]).isdigit():
            continue
        requests = roles.setdefault(str(row[12]), {})
        if match[1] in requests:
            raise ValueError(f"重复委托: {row[0]} {row[2]}")
        requests[match[1]] = dict(UnitId=str(row[7]), Unit=row[6], MinimumLevel=row[8])
    book.close()
    args.output.write_text(json.dumps(roles, ensure_ascii=False, indent=2) + "\n", encoding="utf8")


if __name__ == "__main__":
    main()
