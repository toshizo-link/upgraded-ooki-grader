from __future__ import annotations

from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas


ROOT = Path(__file__).resolve().parents[3]
OUT_DIR = Path(__file__).resolve().parent / "fixtures"
PRINT_FONT_PATH = (
    ROOT
    / "src"
    / "OokiGrader.Reports.Pdf"
    / "Assets"
    / "Fonts"
    / "NotoSansJP[wght].ttf"
)
HAND_FONT_PATH = Path(
    "/Users/takamimarsh/Library/Fonts/ShigotoMemogaki-Regular-1-02.ttf"
)

PRINT_FONT = "NotoSansJP"
HAND_FONT = "ShigotoMemogaki"


def register_fonts() -> None:
    global HAND_FONT
    pdfmetrics.registerFont(TTFont(PRINT_FONT, str(PRINT_FONT_PATH)))
    if HAND_FONT_PATH.exists():
        pdfmetrics.registerFont(TTFont(HAND_FONT, str(HAND_FONT_PATH)))
    else:
        HAND_FONT = PRINT_FONT


def mm(value: float) -> float:
    return value * 72 / 25.4


def draw_text(c: canvas.Canvas, x: float, y: float, text: str, size: float = 10) -> None:
    c.setFont(PRINT_FONT, size)
    c.setFillColor(colors.HexColor("#172033"))
    c.drawString(mm(x), mm(y), text)


def draw_centered(
    c: canvas.Canvas, x: float, y: float, width: float, text: str, size: float = 10
) -> None:
    c.setFont(PRINT_FONT, size)
    c.setFillColor(colors.HexColor("#172033"))
    c.drawCentredString(mm(x + width / 2), mm(y), text)


def answer_box(
    c: canvas.Canvas,
    number: int,
    x: float,
    y: float,
    width: float,
    height: float,
    answer: str | None = None,
    font_size: float = 14,
    rotate: float = 0,
) -> None:
    c.setStrokeColor(colors.HexColor("#42526B"))
    c.setLineWidth(0.8)
    c.roundRect(mm(x), mm(y), mm(width), mm(height), mm(1.5), stroke=1, fill=0)
    c.setFillColor(colors.HexColor("#EAF3FF"))
    c.rect(mm(x), mm(y), mm(9), mm(height), stroke=0, fill=1)
    c.setStrokeColor(colors.HexColor("#42526B"))
    c.line(mm(x + 9), mm(y), mm(x + 9), mm(y + height))
    draw_centered(c, x, y + height / 2 - 1.6, 9, f"{number}", 8)
    if not answer:
        return

    c.saveState()
    c.setFont(HAND_FONT, font_size)
    c.setFillColor(colors.HexColor("#1B4F9C"))
    text_width = pdfmetrics.stringWidth(answer, HAND_FONT, font_size)
    available = mm(width - 13)
    if text_width > available:
        adjusted = max(9, font_size * available / text_width)
        c.setFont(HAND_FONT, adjusted)
    c.translate(mm(x + 13), mm(y + height / 2 - 1.7))
    c.rotate(rotate)
    c.drawString(0, 0, answer)
    c.restoreState()


def draw_wrapped(
    c: canvas.Canvas,
    x: float,
    y: float,
    lines: list[str],
    size: float = 9.4,
    leading: float = 5.3,
) -> None:
    for index, line in enumerate(lines):
        draw_text(c, x, y - index * leading, line, size)


def create_exam(
    destination: Path,
    *,
    student_number: str | None = None,
    student_name: str | None = None,
    answers: dict[int, str] | None = None,
) -> None:
    answers = answers or {}
    c = canvas.Canvas(str(destination), pagesize=A4)
    page_width, page_height = A4
    c.setTitle("中学1年 社会科 地理 - アジア州 確認テスト")
    c.setAuthor("大木グレーダー ユーザーガイド用・架空教材")

    # Header band.
    c.setFillColor(colors.HexColor("#0C6E9E"))
    c.roundRect(mm(14), page_height - mm(38), mm(116), mm(23), mm(2), fill=1, stroke=0)
    c.setFillColor(colors.white)
    c.setFont(PRINT_FONT, 17)
    c.drawString(mm(19), page_height - mm(25), "中学1年 社会科 地理")
    c.setFont(PRINT_FONT, 10)
    c.drawString(mm(19), page_height - mm(33), "アジア州 確認テスト（50点）")

    draw_text(c, 139, 272, "生徒番号", 8.5)
    c.setStrokeColor(colors.HexColor("#42526B"))
    c.line(mm(139), mm(269), mm(176), mm(269))
    draw_text(c, 139, 260, "名前", 8.5)
    c.line(mm(139), mm(257), mm(196), mm(257))
    if student_number:
        c.setFillColor(colors.HexColor("#1B4F9C"))
        c.setFont(HAND_FONT, 12)
        c.drawString(mm(158), mm(270), student_number)
    if student_name:
        c.setFillColor(colors.HexColor("#1B4F9C"))
        c.setFont(HAND_FONT, 13)
        c.drawString(mm(153), mm(258), student_name)

    # Instructions.
    c.setFillColor(colors.HexColor("#F4F7FA"))
    c.roundRect(mm(14), mm(239), mm(182), mm(12), mm(1.5), fill=1, stroke=0)
    draw_text(
        c,
        18,
        243,
        "答えは、それぞれの問題のすぐ下にある解答欄へ書きなさい。",
        9.2,
    )

    # Question 1.
    draw_text(c, 15, 231, "1  日本の首都を漢字で書きなさい。［8点］", 10)
    answer_box(c, 1, 20, 214, 176, 12, answers.get(1), rotate=-0.8)

    # Question 2.
    draw_text(
        c,
        15,
        206,
        "2  ASEAN（アセアン）を日本語で何というか。［10点］",
        10,
    )
    answer_box(c, 2, 20, 189, 176, 12, answers.get(2), font_size=13, rotate=0.5)

    # Question 3 and chart.
    draw_text(
        c,
        15,
        181,
        "3  インドで最も多くの人が信仰している宗教を書きなさい。［8点］",
        10,
    )
    answer_box(c, 3, 20, 164, 176, 12, answers.get(3), rotate=-0.4)

    # Question 4, inline multiple-choice with its own answer area.
    draw_text(
        c,
        15,
        156,
        "4  東南アジアの気候として最も適切なものを、ア〜ウから選びなさい。［8点］",
        9.4,
    )
    draw_text(
        c,
        23,
        148,
        "ア：一年中寒冷    イ：雨季と乾季がある    ウ：一年中ほとんど雨が降らない",
        8.7,
    )
    answer_box(c, 4, 20, 131, 176, 12, answers.get(4), rotate=0.7)

    # Source material.
    c.setFillColor(colors.HexColor("#EAF3FF"))
    c.roundRect(mm(15), mm(91), mm(181), mm(32), mm(1.5), fill=1, stroke=0)
    draw_text(c, 19, 116, "資料：ある国の主な輸出品の変化", 9)
    c.setFillColor(colors.HexColor("#FFFFFF"))
    c.rect(mm(20), mm(97), mm(171), mm(14), stroke=0, fill=1)
    c.setFillColor(colors.HexColor("#2C7FB8"))
    c.rect(mm(20), mm(102.5), mm(48), mm(6.5), stroke=0, fill=1)
    c.setFillColor(colors.HexColor("#72B7B2"))
    c.rect(mm(68), mm(102.5), mm(30), mm(6.5), stroke=0, fill=1)
    c.setFillColor(colors.HexColor("#F2A65A"))
    c.rect(mm(98), mm(102.5), mm(22), mm(6.5), stroke=0, fill=1)
    c.setFillColor(colors.HexColor("#D9E2EC"))
    c.rect(mm(120), mm(102.5), mm(71), mm(6.5), stroke=0, fill=1)
    draw_text(c, 22, 104.5, "1980年  天然ゴム 28%", 7.4)
    c.setFillColor(colors.HexColor("#2C7FB8"))
    c.rect(mm(20), mm(97), mm(92), mm(4.5), stroke=0, fill=1)
    c.setFillColor(colors.HexColor("#72B7B2"))
    c.rect(mm(112), mm(97), mm(28), mm(4.5), stroke=0, fill=1)
    c.setFillColor(colors.HexColor("#F2A65A"))
    c.rect(mm(140), mm(97), mm(51), mm(4.5), stroke=0, fill=1)
    draw_text(c, 22, 98.2, "2020年  機械類 54%", 7.2)

    # Question 5 with large integrated response area.
    draw_wrapped(
        c,
        15,
        83,
        [
            "5  資料から読み取れる輸出品の変化を、「工業化」という言葉を使って",
            "   具体的に説明しなさい。［16点］",
        ],
        9.4,
        5.7,
    )
    answer_box(c, 5, 20, 38, 176, 30, answers.get(5), font_size=10.8, rotate=-0.2)

    c.setStrokeColor(colors.HexColor("#B9C5D2"))
    c.line(mm(14), mm(25), mm(196), mm(25))
    draw_text(
        c,
        15,
        18,
        "※このテストはユーザーガイド用に作成した架空の教材です。",
        7.5,
    )
    draw_text(c, 162, 18, "1 / 1", 7.5)
    c.save()


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    register_fonts()
    create_exam(OUT_DIR / "asia-check-test-blank.pdf")
    create_exam(
        OUT_DIR / "asia-check-test-hanako.pdf",
        student_number="S-001",
        student_name="桜井 花子",
        answers={
            1: "東京",
            2: "東南アジア諸国連合",
            3: "ヒンドゥー教",
            4: "イ",
            5: "工業化が進み、天然ゴム中心から機械類中心へ変化した。",
        },
    )
    create_exam(
        OUT_DIR / "asia-check-test-yuta.pdf",
        student_number="S-002",
        student_name="田中 悠太",
        answers={
            1: "東京都",
            2: "アセアン",
            3: "イスラム教",
            4: "イ",
            5: "工場が増え、機械類の輸出割合が大きくなった。",
        },
    )
    print(f"Created demo exam PDFs in {OUT_DIR}")


if __name__ == "__main__":
    main()
