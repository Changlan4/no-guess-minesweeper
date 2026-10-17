"""
生成扫雷的应用图标（多尺寸 .ico）。

设计：薄荷绿圆角方块 + 深灰地雷（八根刺 + 左上高光），与「薄荷马卡龙」主题同色系。
在 1024×1024 上绘制再降采样到各尺寸，保证小尺寸下的抗锯齿质量。

用法：
    python tools/make_icon.py
输出：
    src/Minesweeper/app.ico        多尺寸图标（16/24/32/48/64/128/256）
    tools/icon-preview.png         256px 预览图，方便肉眼确认
"""

from pathlib import Path
import math

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ICO_PATH = ROOT / "src" / "Minesweeper" / "app.ico"
PREVIEW_PATH = Path(__file__).resolve().parent / "icon-preview.png"

S = 1024                      # 绘制画布（超采样）
BG_TOP = (158, 210, 186)      # #9ED2BA 薄荷绿（亮）
BG_BOTTOM = (122, 178, 152)   # #7AB298 薄荷绿（暗）
MINE = (62, 74, 69)           # #3E4A45 深灰绿
HIGHLIGHT = (255, 255, 255, 96)
SIZES = [16, 24, 32, 48, 64, 128, 256]


def rounded_background() -> Image.Image:
    """圆角方块 + 竖向渐变"""
    gradient = Image.new("RGBA", (S, S))
    draw = ImageDraw.Draw(gradient)
    for y in range(S):
        t = y / (S - 1)
        color = tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(3))
        draw.line([(0, y), (S, y)], fill=color + (255,))

    mask = Image.new("L", (S, S), 0)
    margin = int(S * 0.045)
    ImageDraw.Draw(mask).rounded_rectangle(
        [margin, margin, S - margin, S - margin],
        radius=int(S * 0.235),
        fill=255,
    )

    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    canvas.paste(gradient, (0, 0), mask)
    return canvas


def draw_mine(img: Image.Image) -> None:
    draw = ImageDraw.Draw(img)
    center = S / 2
    body_radius = S * 0.255
    spike_length = S * 0.105
    spike_width = max(2, int(S * 0.052))

    # 八根刺
    for k in range(8):
        angle = math.radians(k * 45)
        cos_a, sin_a = math.cos(angle), math.sin(angle)
        draw.line(
            [
                (center + cos_a * (body_radius - S * 0.02), center + sin_a * (body_radius - S * 0.02)),
                (center + cos_a * (body_radius + spike_length), center + sin_a * (body_radius + spike_length)),
            ],
            fill=MINE + (255,),
            width=spike_width,
        )

    # 主体
    draw.ellipse(
        [center - body_radius, center - body_radius, center + body_radius, center + body_radius],
        fill=MINE + (255,),
    )

    # 左上柔光（别做成死白圆点，否则小尺寸下像一只眼睛）
    hl_radius = body_radius * 0.235
    hl_x = center - body_radius * 0.38
    hl_y = center - body_radius * 0.40
    draw.ellipse(
        [hl_x - hl_radius, hl_y - hl_radius, hl_x + hl_radius, hl_y + hl_radius],
        fill=HIGHLIGHT,
    )


def main() -> None:
    canvas = rounded_background()
    draw_mine(canvas)

    ICO_PATH.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(ICO_PATH, format="ICO", sizes=[(s, s) for s in SIZES])

    canvas.resize((256, 256), Image.LANCZOS).save(PREVIEW_PATH)

    size_kb = ICO_PATH.stat().st_size / 1024
    print(f"已生成 {ICO_PATH}  ({size_kb:.1f} KB, 含尺寸 {SIZES})")
    print(f"预览图 {PREVIEW_PATH}")

    # 顺带回读确认每个尺寸都真的写进去了
    with Image.open(ICO_PATH) as ico:
        print("ICO 内包含尺寸:", sorted(ico.info.get("sizes", [])))


if __name__ == "__main__":
    main()
