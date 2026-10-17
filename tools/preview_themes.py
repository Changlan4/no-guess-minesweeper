"""
把 4 套主题的配色渲染成小样张，方便肉眼比较是否刺眼。

颜色直接从 Themes/*.xaml 里解析，所以预览和实际游戏配色永远一致。
用法：
    python tools/preview_themes.py
输出：
    tools/theme-preview.png
"""

from pathlib import Path
import re

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
THEME_DIR = ROOT / "src" / "Minesweeper" / "Themes"
OUT_PATH = Path(__file__).resolve().parent / "theme-preview.png"

THEMES = ["MintNoir", "RoseNoir", "VioletNoir", "OceanNoir"]

CELL = 44
PAD = 16
COLS, ROWS = 6, 5

# 每个格子的内容：None=未翻开, 0=空白, 1..8=数字, 'F'=旗, 'X'=踩中的雷, 'M'=雷
BOARD = [
    [None, 1, 0, 0, 'F', None],
    [None, 1, 0, 0, 1, None],
    [2, 2, 1, 0, 1, None],
    [None, 'F', 1, 1, 2, None],
    [None, None, None, 'M', 'X', None],
]


def load_theme(name: str) -> dict[str, str]:
    text = (THEME_DIR / f"{name}.xaml").read_text(encoding="utf-8")
    return dict(re.findall(r'x:Key="(\w+)"\s+Color="(#[0-9A-Fa-f]{6})"', text))


def make_font(size: int):
    from PIL import ImageFont
    for candidate in ("seguisb.ttf", "segoeui.ttf", "arialbd.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(candidate, size)
        except OSError:
            continue
    return ImageFont.load_default()


def draw_theme(colors: dict[str, str], label: str) -> Image.Image:
    board_w, board_h = COLS * CELL, ROWS * CELL
    width = PAD * 2 + board_w + 12
    height = PAD + 26 + board_h + 12 + PAD + 30

    img = Image.new("RGB", (width, height), colors["WindowBackground"])
    draw = ImageDraw.Draw(img)
    title_font = make_font(15)
    number_font = make_font(int(CELL * 0.52))

    draw.text((PAD, 8), label, fill=colors["TextPrimary"], font=title_font)

    # 棋盘底板
    board_top = PAD + 26
    draw.rectangle(
        [PAD, board_top, PAD + board_w + 12, board_top + board_h + 12],
        fill=colors["BoardBackground"],
    )

    origin_x, origin_y = PAD + 6, board_top + 6
    for row in range(ROWS):
        for col in range(COLS):
            value = BOARD[row][col]
            x = origin_x + col * CELL
            y = origin_y + row * CELL

            if value is None or value == 'F':
                # 未翻开：凸起浮雕
                draw.rectangle(
                    [x + 2, y + 2, x + CELL - 2, y + CELL - 2],
                    fill=colors["CellCoveredFace"],
                )
                draw.line([(x + 1, y + 1), (x + CELL - 1, y + 1)], fill=colors["CellHighlight"], width=2)
                draw.line([(x + 1, y + 1), (x + 1, y + CELL - 1)], fill=colors["CellHighlight"], width=2)
                draw.line([(x + 1, y + CELL - 1), (x + CELL - 1, y + CELL - 1)], fill=colors["CellShadow"], width=2)
                draw.line([(x + CELL - 1, y + 1), (x + CELL - 1, y + CELL - 1)], fill=colors["CellShadow"], width=2)

                if value == 'F':
                    draw.text((x + CELL * 0.28, y + CELL * 0.16), "F",
                              fill=colors["FlagColor"], font=number_font)
                continue

            # 已翻开
            if value == 'X':
                background = colors["ExplodedCell"]
            elif value == 'M':
                background = colors["WrongFlagColor"]
            else:
                background = colors["CellRevealed"]
            draw.rectangle([x, y, x + CELL - 1, y + CELL - 1], fill=background,
                           outline=colors["CellShadow"], width=1)

            if isinstance(value, int) and value > 0:
                draw.text((x + CELL * 0.30, y + CELL * 0.16), str(value),
                          fill=colors[f"Num{value}"], font=number_font)

    # 底部状态栏
    bar_top = board_top + board_h + 20
    draw.rectangle([PAD, bar_top, width - PAD, bar_top + 22], fill=colors["StatusBarBg"])

    return img


def main() -> None:
    tiles = [draw_theme(load_theme(name), name) for name in THEMES]

    gap = 12
    width = sum(t.width for t in tiles) + gap * (len(tiles) - 1)
    height = max(t.height for t in tiles)
    canvas = Image.new("RGB", (width, height), (250, 250, 250))

    x = 0
    for tile in tiles:
        canvas.paste(tile, (x, 0))
        x += tile.width + gap

    canvas.save(OUT_PATH)
    print(f"已生成配色样张 {OUT_PATH}  ({canvas.width}×{canvas.height})")
    for name in THEMES:
        colors = load_theme(name)
        print(f"  {name:14s} 底 {colors['WindowBackground']}  未翻开 {colors['CellCoveredFace']}  已翻开 {colors['CellRevealed']}")


if __name__ == "__main__":
    main()
