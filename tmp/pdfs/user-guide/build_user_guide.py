from __future__ import annotations

import hashlib
import math
from pathlib import Path
from typing import Iterable

from PIL import Image as PilImage
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas


ROOT = Path(__file__).resolve().parents[3]
SCREEN_DIR = Path(__file__).resolve().parent / "screens"
FIXTURE_DIR = Path(__file__).resolve().parent / "fixtures"
PROCESSED_DIR = Path(__file__).resolve().parent / "processed"
OUTPUT = ROOT / "output" / "pdf" / "ooki-grader-user-guide-ja.pdf"
FONT_PATH = (
    ROOT
    / "src"
    / "OokiGrader.Reports.Pdf"
    / "Assets"
    / "Fonts"
    / "NotoSansJP[wght].ttf"
)

FONT = "NotoSansJP"
PAGE_W, PAGE_H = A4

DARK = colors.HexColor("#173E39")
DEEP = colors.HexColor("#23564F")
GREEN = colors.HexColor("#2E7067")
MINT = colors.HexColor("#E7F2EF")
PALE = colors.HexColor("#F5F7F5")
ORANGE = colors.HexColor("#E78B49")
ORANGE_PALE = colors.HexColor("#FFF2E7")
BLUE = colors.HexColor("#2C7FB8")
BLUE_PALE = colors.HexColor("#EAF3FF")
INK = colors.HexColor("#182823")
MUTED = colors.HexColor("#65736F")
BORDER = colors.HexColor("#D9E0DD")
WHITE = colors.white


def mm(value: float) -> float:
    return value * 72 / 25.4


def register_font() -> None:
    pdfmetrics.registerFont(TTFont(FONT, str(FONT_PATH)))


def text_width(text: str, size: float) -> float:
    return pdfmetrics.stringWidth(text, FONT, size) / mm(1)


def wrap_line(text: str, size: float, width_mm: float) -> list[str]:
    if not text:
        return [""]
    lines: list[str] = []
    current = ""
    for char in text:
        candidate = current + char
        if current and text_width(candidate, size) > width_mm:
            lines.append(current)
            current = char
        else:
            current = candidate
    if current:
        lines.append(current)
    return lines


def draw_text(
    c: canvas.Canvas,
    x: float,
    y: float,
    text: str,
    *,
    size: float = 9.5,
    color=INK,
) -> None:
    c.setFont(FONT, size)
    c.setFillColor(color)
    c.drawString(mm(x), mm(y), text)


def draw_right(
    c: canvas.Canvas,
    x: float,
    y: float,
    text: str,
    *,
    size: float = 8,
    color=MUTED,
) -> None:
    c.setFont(FONT, size)
    c.setFillColor(color)
    c.drawRightString(mm(x), mm(y), text)


def paragraph(
    c: canvas.Canvas,
    text: str,
    x: float,
    y: float,
    width: float,
    *,
    size: float = 9.2,
    leading: float = 4.7,
    color=INK,
) -> float:
    cursor = y
    for raw_line in text.splitlines() or [""]:
        for line in wrap_line(raw_line, size, width):
            draw_text(c, x, cursor, line, size=size, color=color)
            cursor -= leading
    return cursor


def bullet_list(
    c: canvas.Canvas,
    items: Iterable[str],
    x: float,
    y: float,
    width: float,
    *,
    size: float = 8.7,
    leading: float = 4.5,
    gap: float = 2.0,
    color=INK,
    dot_color=ORANGE,
) -> float:
    cursor = y
    for item in items:
        lines = wrap_line(item, size, width - 6)
        c.setFillColor(dot_color)
        c.circle(mm(x + 1.6), mm(cursor + 1.1), mm(1.1), fill=1, stroke=0)
        for index, line in enumerate(lines):
            draw_text(
                c,
                x + 5,
                cursor - index * leading,
                line,
                size=size,
                color=color,
            )
        cursor -= leading * len(lines) + gap
    return cursor


def rounded_box(
    c: canvas.Canvas,
    x: float,
    y: float,
    width: float,
    height: float,
    *,
    fill=WHITE,
    stroke=BORDER,
    radius: float = 2.5,
    line_width: float = 0.7,
) -> None:
    c.setFillColor(fill)
    c.setStrokeColor(stroke)
    c.setLineWidth(line_width)
    c.roundRect(
        mm(x),
        mm(y),
        mm(width),
        mm(height),
        mm(radius),
        fill=1,
        stroke=1,
    )


def callout(
    c: canvas.Canvas,
    x: float,
    y: float,
    width: float,
    height: float,
    title: str,
    body: str,
    *,
    tone: str = "info",
) -> None:
    palette = {
        "info": (BLUE_PALE, BLUE),
        "safe": (MINT, GREEN),
        "warn": (ORANGE_PALE, ORANGE),
        "plain": (PALE, BORDER),
    }
    fill, accent = palette[tone]
    rounded_box(c, x, y, width, height, fill=fill, stroke=accent)
    c.setFillColor(accent)
    c.roundRect(
        mm(x),
        mm(y),
        mm(2.4),
        mm(height),
        mm(1.2),
        fill=1,
        stroke=0,
    )
    draw_text(c, x + 6, y + height - 7, title, size=9.2, color=INK)
    paragraph(
        c,
        body,
        x + 6,
        y + height - 13,
        width - 10,
        size=7.8,
        leading=4.1,
        color=MUTED,
    )


def section_header(
    c: canvas.Canvas, section: str, title: str, page_number: int
) -> None:
    c.setFillColor(PALE)
    c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    draw_text(c, 14, 282.5, section, size=8.3, color=GREEN)
    draw_text(c, 14, 271.5, title, size=20, color=DARK)
    c.setStrokeColor(BORDER)
    c.setLineWidth(0.8)
    c.line(mm(14), mm(265.5), mm(196), mm(265.5))
    footer(c, page_number)


def footer(c: canvas.Canvas, page_number: int) -> None:
    c.setStrokeColor(BORDER)
    c.setLineWidth(0.5)
    c.line(mm(14), mm(12.5), mm(196), mm(12.5))
    draw_text(
        c,
        14,
        7,
        "Ooki Grader ユーザーガイド / 架空データによるデモ画面",
        size=6.7,
        color=MUTED,
    )
    draw_right(c, 196, 7, str(page_number), size=7, color=MUTED)


def processed_image(path: Path, crop: tuple[int, int, int, int] | None) -> Path:
    if crop is None:
        return path
    PROCESSED_DIR.mkdir(parents=True, exist_ok=True)
    source_stat = path.stat()
    token = hashlib.sha1(
        (
            str(path.resolve())
            + repr(crop)
            + str(source_stat.st_mtime_ns)
            + str(source_stat.st_size)
        ).encode("utf-8")
    ).hexdigest()[:12]
    destination = PROCESSED_DIR / f"{path.stem}-{token}.png"
    if destination.exists():
        return destination
    with PilImage.open(path) as image:
        image.crop(crop).save(destination)
    return destination


def place_image(
    c: canvas.Canvas,
    path: Path,
    x: float,
    y: float,
    width: float,
    height: float,
    *,
    crop: tuple[int, int, int, int] | None = None,
    background=WHITE,
    border=BORDER,
) -> tuple[float, float, float, float]:
    source = processed_image(path, crop)
    with PilImage.open(source) as image:
        image_width, image_height = image.size
    available_ratio = width / height
    image_ratio = image_width / image_height
    if image_ratio >= available_ratio:
        drawn_width = width
        drawn_height = width / image_ratio
    else:
        drawn_height = height
        drawn_width = height * image_ratio
    drawn_x = x + (width - drawn_width) / 2
    drawn_y = y + (height - drawn_height) / 2

    c.setFillColor(colors.Color(0, 0, 0, alpha=0.08))
    c.roundRect(
        mm(drawn_x + 1.1),
        mm(drawn_y - 1.1),
        mm(drawn_width),
        mm(drawn_height),
        mm(2),
        fill=1,
        stroke=0,
    )
    c.setFillColor(background)
    c.setStrokeColor(border)
    c.setLineWidth(0.6)
    c.roundRect(
        mm(drawn_x),
        mm(drawn_y),
        mm(drawn_width),
        mm(drawn_height),
        mm(2),
        fill=1,
        stroke=1,
    )
    c.drawImage(
        ImageReader(str(source)),
        mm(drawn_x),
        mm(drawn_y),
        width=mm(drawn_width),
        height=mm(drawn_height),
        preserveAspectRatio=True,
        mask="auto",
    )
    return drawn_x, drawn_y, drawn_width, drawn_height


def marker(c: canvas.Canvas, number: int, x: float, y: float) -> None:
    c.setFillColor(ORANGE)
    c.setStrokeColor(WHITE)
    c.setLineWidth(1.3)
    c.circle(mm(x), mm(y), mm(3.2), fill=1, stroke=1)
    c.setFillColor(WHITE)
    c.setFont(FONT, 8.5)
    c.drawCentredString(mm(x), mm(y - 1.1), str(number))


def label_chip(
    c: canvas.Canvas,
    number: int,
    text: str,
    x: float,
    y: float,
    width: float,
    *,
    color=INK,
) -> None:
    marker(c, number, x + 3.2, y + 3.2)
    draw_text(c, x + 8, y + 1.1, text, size=7.8, color=color)
    c.setStrokeColor(BORDER)
    c.line(mm(x + 8), mm(y - 1), mm(x + width), mm(y - 1))


def role_table(c: canvas.Canvas, x: float, y: float, width: float) -> None:
    rows = [
        ("管理者", "全機能、職員、AI、保存容量、ジョブ、バックアップ"),
        ("先生", "生徒、ひな形、実施、氏名照合、採点、確定、帳票"),
        ("スキャン担当", "受付中の実施への答案アップロードと処理状況"),
        ("閲覧担当", "確定済み結果と帳票の閲覧"),
    ]
    row_height = 14
    rounded_box(c, x, y - row_height * len(rows), width, row_height * len(rows))
    for index, (role, scope) in enumerate(rows):
        row_top = y - index * row_height
        if index:
            c.setStrokeColor(BORDER)
            c.line(
                mm(x),
                mm(row_top),
                mm(x + width),
                mm(row_top),
            )
        c.setFillColor(MINT if index % 2 == 0 else WHITE)
        c.rect(
            mm(x + 0.5),
            mm(row_top - row_height + 0.5),
            mm(34),
            mm(row_height - 1),
            fill=1,
            stroke=0,
        )
        draw_text(c, x + 5, row_top - 8.7, role, size=9, color=DARK)
        paragraph(
            c,
            scope,
            x + 39,
            row_top - 6.8,
            width - 44,
            size=7.8,
            leading=4,
            color=INK,
        )


def flow(c: canvas.Canvas, x: float, y: float, width: float) -> None:
    labels = [
        "ひな形",
        "テスト実施",
        "アップロード",
        "氏名確認",
        "採点確認",
        "確定",
        "帳票",
    ]
    gap = 3
    box_width = (width - gap * 6) / 7
    for index, label in enumerate(labels):
        box_x = x + index * (box_width + gap)
        rounded_box(
            c,
            box_x,
            y,
            box_width,
            18,
            fill=MINT if index in {0, 3, 5} else WHITE,
            stroke=GREEN,
            radius=2,
        )
        draw_text(c, box_x + 2.2, y + 10.5, f"{index + 1}", size=7, color=ORANGE)
        lines = wrap_line(label, 7.2, box_width - 4)
        for line_index, line in enumerate(lines[:2]):
            draw_text(
                c,
                box_x + 2.2,
                y + 5.7 - line_index * 3.5,
                line,
                size=7.2,
                color=DARK,
            )
        if index < len(labels) - 1:
            c.setStrokeColor(MUTED)
            c.setLineWidth(0.8)
            start = box_x + box_width + 0.5
            c.line(mm(start), mm(y + 9), mm(start + gap - 1), mm(y + 9))
            c.line(
                mm(start + gap - 2),
                mm(y + 10.3),
                mm(start + gap - 1),
                mm(y + 9),
            )
            c.line(
                mm(start + gap - 2),
                mm(y + 7.7),
                mm(start + gap - 1),
                mm(y + 9),
            )


def new_page(c: canvas.Canvas) -> None:
    c.showPage()


def build() -> None:
    register_font()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    PROCESSED_DIR.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(OUTPUT), pagesize=A4, pageCompression=1)
    c.setTitle("Ooki Grader ユーザーガイド")
    c.setAuthor("Ooki Grader")
    c.setSubject("先生・管理者・スキャン担当向け 日本語ユーザーガイド")
    c.setKeywords("Ooki Grader, ユーザーガイド, 採点, 日本語")

    # 1. Cover
    c.setFillColor(PALE)
    c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    c.setFillColor(DARK)
    c.rect(0, 0, mm(10), PAGE_H, fill=1, stroke=0)
    c.setFillColor(ORANGE)
    c.circle(mm(29), mm(271), mm(11), fill=1, stroke=0)
    c.setFillColor(WHITE)
    c.setFont(FONT, 19)
    c.drawCentredString(mm(29), mm(264.5), "大")
    draw_text(c, 47, 274, "OOKI GRADER", size=8, color=GREEN)
    draw_text(c, 18, 248, "Ooki Grader", size=28, color=DARK)
    draw_text(c, 18, 234, "ユーザーガイド", size=28, color=DARK)
    draw_text(
        c,
        18,
        222,
        "先生・管理者・スキャン担当向け",
        size=11,
        color=MUTED,
    )
    place_image(
        c,
        SCREEN_DIR / "03-dashboard.png",
        16,
        85,
        178,
        123,
    )
    place_image(
        c,
        FIXTURE_DIR / "rendered" / "hanako.png",
        137,
        35,
        58,
        78,
        border=ORANGE,
    )
    callout(
        c,
        18,
        40,
        112,
        31,
        "このガイドの画面について",
        "分離した開発用デモ環境で、実際に登録・採点・確定・PDF作成まで操作して収録しました。学校名・生徒名・答案はすべて架空です。",
        tone="safe",
    )
    draw_text(c, 18, 25, "対象バージョン  v0.1", size=7.8, color=MUTED)
    draw_right(c, 195, 25, "2026年7月28日", size=7.8, color=MUTED)
    footer(c, 1)
    new_page(c)

    # 2. Roles and flow
    section_header(c, "01  はじめに", "役割と毎日の流れ", 2)
    paragraph(
        c,
        "画面に表示されるメニューは、職員へ割り当てられた役割で変わります。まず自分の担当範囲を確認してください。",
        14,
        257,
        182,
        size=9.3,
        leading=4.8,
        color=MUTED,
    )
    draw_text(c, 14, 239, "役割ごとの主な操作", size=12, color=DARK)
    role_table(c, 14, 232, 182)
    draw_text(c, 14, 164, "標準の作業順", size=12, color=DARK)
    flow(c, 14, 139, 182)
    callout(
        c,
        14,
        94,
        88,
        35,
        "先生の確認が中心です",
        "AIや規則判定は提案を作ります。生徒名の割り当て、自信の低い採点、確定は職員が確認します。",
        tone="warn",
    )
    callout(
        c,
        108,
        94,
        88,
        35,
        "データは学校内ホストへ",
        "ブラウザを閉じても送信済み処理は続きます。接続中の表示とシステム状態を確認してください。",
        tone="safe",
    )
    rounded_box(c, 14, 38, 182, 43, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 70, "本書で使用する架空データ", size=10, color=DARK)
    bullet_list(
        c,
        [
            "学校: 大木スクール / 管理者: 佐藤 管理",
            "テスト: 中1社会 アジア州 確認テスト（50点）",
            "生徒: 桜井 花子、田中 悠太ほか",
            "問題用紙と解答欄が同じ紙面に混在する形式",
        ],
        20,
        62,
        168,
        size=8,
        leading=4.2,
        gap=1.4,
    )
    new_page(c)

    # 3. Bootstrap and sign-in
    section_header(c, "02  利用開始", "初回設定と職員ログイン", 3)
    draw_text(c, 14, 256, "初回のみ: 最初の管理者を作成", size=11, color=DARK)
    rounded_box(c, 14, 174, 116, 78, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 239, "初回セットアップ", size=11, color=DARK)
    bullet_list(
        c,
        [
            "学校名は「大木スクール」として登録されます。",
            "管理者名・ユーザー名・12文字以上のパスワードを設定します。",
            "初期設定トークンはホスト管理者から受け取ります。",
        ],
        20,
        228,
        102,
        size=8,
        leading=4.2,
        gap=1.5,
    )
    draw_text(c, 136, 242, "1", size=10, color=ORANGE)
    paragraph(
        c,
        "学校内ホストで一度だけ表示されます。学校名の入力は不要です。管理者アカウントだけを登録します。",
        142,
        242,
        54,
        size=8,
        leading=4.2,
        color=INK,
    )
    draw_text(c, 136, 205, "2", size=10, color=ORANGE)
    paragraph(
        c,
        "初期設定トークンはホスト管理者から受け取り、共用端末へ保存しないでください。",
        142,
        205,
        54,
        size=8,
        leading=4.2,
        color=INK,
    )
    draw_text(c, 14, 160, "通常時: 職員ログイン", size=11, color=DARK)
    login_rect = place_image(
        c,
        SCREEN_DIR / "02-login.png",
        14,
        96,
        112,
        63,
    )
    marker(c, 3, login_rect[0] + 61, login_rect[1] + 33)
    rounded_box(c, 132, 96, 64, 63, fill=WHITE, stroke=BORDER)
    label_chip(c, 3, "ユーザー名とパスワード", 137, 143, 54)
    bullet_list(
        c,
        [
            "学校から発行された個人アカウントを使います。",
            "一時パスワードで入った場合は、案内に従って変更します。",
            "離席時は右上の利用者メニューからログアウトします。",
            "共有PCではブラウザのパスワード保存を使わないでください。",
        ],
        137,
        135,
        54,
        size=7.3,
        leading=3.8,
        gap=1.5,
    )
    callout(
        c,
        14,
        39,
        182,
        42,
        "ログインできないとき",
        "ユーザー名の入力、全角・半角、Caps Lockを確認します。繰り返し失敗した場合は、管理者が職員アカウント画面から有効状態とパスワード再設定を確認します。",
        tone="warn",
    )
    new_page(c)

    # 4. Dashboard
    section_header(c, "03  全体把握", "ダッシュボードの見方", 4)
    paragraph(
        c,
        "ログイン後の起点です。上から「今日の件数」「次にすること」「受付中のテスト」「システム状況」を確認します。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    dashboard_rect = place_image(
        c,
        SCREEN_DIR / "03-dashboard.png",
        14,
        83,
        182,
        163,
    )
    marker(c, 1, dashboard_rect[0] + 23, dashboard_rect[1] + dashboard_rect[3] - 38)
    marker(c, 2, dashboard_rect[0] + 105, dashboard_rect[1] + dashboard_rect[3] - 63)
    marker(c, 3, dashboard_rect[0] + 115, dashboard_rect[1] + 75)
    marker(c, 4, dashboard_rect[0] + 167, dashboard_rect[1] + 24)
    label_chip(c, 1, "担当メニュー", 14, 67, 41)
    label_chip(c, 2, "確認待ち件数", 57, 67, 43)
    label_chip(c, 3, "次の作業", 102, 67, 38)
    label_chip(c, 4, "ホスト状態", 142, 67, 54)
    callout(
        c,
        14,
        29,
        182,
        27,
        "「要確認」は故障とは限りません",
        "開発環境では、Gemini未設定、バックアップ未設定、証明書省略などが要確認になります。運用開始時は管理画面で各項目の理由を確認してください。",
        tone="info",
    )
    new_page(c)

    # 5. Students
    section_header(c, "04  生徒台帳", "生徒登録・検索・CSV取り込み", 5)
    student_rect = place_image(
        c,
        SCREEN_DIR / "04b-students-desktop.png",
        14,
        159,
        182,
        102,
    )
    marker(c, 1, student_rect[0] + 103, student_rect[1] + 74)
    marker(c, 2, student_rect[0] + 148, student_rect[1] + 84)
    marker(c, 3, student_rect[0] + 173, student_rect[1] + 84)
    add_rect = place_image(
        c,
        SCREEN_DIR / "05c-student-add.png",
        14,
        102,
        88,
        50,
    )
    csv_rect = place_image(
        c,
        SCREEN_DIR / "06b-student-csv-dialog.png",
        108,
        102,
        88,
        50,
    )
    draw_text(c, 14, 94, "個別登録", size=8.5, color=DARK)
    draw_text(c, 108, 94, "CSV取り込み（3手順）", size=8.5, color=DARK)
    bullet_list(
        c,
        [
            "生徒番号は学校内で重複しない値にします。",
            "氏名とカナは答案の照合候補に使われます。変更は慎重に行います。",
            "CSVは UTF-8 BOM付き または Shift_JIS。ファイル全体を検証し、1行でもエラーがあれば適用しません。",
            "在籍終了にしても過去の結果は残りますが、新しい照合候補から外れます。",
        ],
        14,
        82,
        182,
        size=8,
        leading=4.2,
        gap=1.6,
    )
    new_page(c)

    # 6. Automated template creation
    section_header(c, "05  採点基準", "問題用紙からひな形を自動作成する", 6)
    paragraph(
        c,
        "問題用紙と解答資料をまとめて追加するだけで、資料区分・基本情報・問題・解答欄・採点基準の下書きを自動作成します。",
        14,
        258,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    create_rect = place_image(
        c,
        SCREEN_DIR / "10-template-create.png",
        14,
        174,
        182,
        75,
    )
    marker(c, 1, create_rect[0] + 109, create_rect[1] + 32)
    marker(c, 2, create_rect[0] + 107, create_rect[1] + 17)
    draw_text(
        c,
        14,
        166,
        "1  PDF・画像をまとめて追加　　2  Gemini が使える場合は自動で下書きを開始",
        size=7.8,
        color=DARK,
    )
    match_rect = place_image(
        c,
        SCREEN_DIR / "33-template-auto-match.png",
        14,
        72,
        182,
        86,
    )
    marker(c, 3, match_rect[0] + 118, match_rect[1] + 27)
    marker(c, 4, match_rect[0] + 149, match_rect[1] + 20)
    draw_text(
        c,
        14,
        64,
        "3  資料区分が違う場合だけ変更　　4  完全一致した公開済み版はそのまま再利用",
        size=7.8,
        color=DARK,
    )
    callout(
        c,
        14,
        24,
        182,
        28,
        "先生が直すのは例外だけ",
        "資料区分は5秒間だけ修正できます。AI完了後は、安全に一括確認できる提案をまとめて採用し、正答不足・低信頼・記述式などだけを個別確認します。公開は先生が行います。",
        tone="info",
    )
    new_page(c)

    # 7. Interwoven answer areas
    section_header(c, "06  交互配置の答案", "問題用紙内の解答欄を登録する", 7)
    paragraph(
        c,
        "問題文と解答欄が同じ紙面に混在していても、論理上の問題ごとに別々の解答領域を登録します。",
        14,
        257,
        182,
        size=9,
        leading=4.6,
        color=MUTED,
    )
    blank_exam = place_image(
        c,
        FIXTURE_DIR / "rendered" / "blank.png",
        14,
        75,
        86,
        174,
        border=BLUE,
    )
    completed_exam = place_image(
        c,
        FIXTURE_DIR / "rendered" / "hanako.png",
        110,
        75,
        86,
        174,
        border=ORANGE,
    )
    # Approximate positions of the visible integrated answer areas.
    marker(c, 1, blank_exam[0] + 23, blank_exam[1] + blank_exam[3] - 49)
    marker(c, 2, blank_exam[0] + 23, blank_exam[1] + blank_exam[3] - 65)
    marker(c, 3, blank_exam[0] + 23, blank_exam[1] + blank_exam[3] - 103)
    draw_text(c, 14, 65, "空欄の問題用紙", size=8.5, color=BLUE)
    draw_text(c, 110, 65, "記入済み答案（架空）", size=8.5, color=ORANGE)
    bullet_list(
        c,
        [
            "1: 短答欄も問題番号ごとに独立させる",
            "2: 選択式も記号を書いた枠を解答領域にする",
            "3: 記述欄は余白を含めて十分な高さを確保する",
        ],
        14,
        54,
        182,
        size=7.8,
        leading=4,
        gap=1.2,
    )
    new_page(c)

    # 8. Template editor and publishing
    section_header(c, "07  ひな形編集", "採点方法・座標・公開を確認する", 8)
    editor_rect = place_image(
        c,
        SCREEN_DIR / "09-template-editor-q5.png",
        14,
        151,
        182,
        103,
    )
    marker(c, 1, editor_rect[0] + 43, editor_rect[1] + 54)
    marker(c, 2, editor_rect[0] + 106, editor_rect[1] + 31)
    marker(c, 3, editor_rect[0] + 159, editor_rect[1] + 49)
    q1_rect = place_image(
        c,
        SCREEN_DIR / "08-template-editor-q1.png",
        14,
        91,
        88,
        50,
    )
    rounded_box(c, 108, 91, 88, 50, fill=WHITE, stroke=BORDER)
    draw_text(c, 114, 132, "公開前チェック", size=9.2, color=DARK)
    bullet_list(
        c,
        [
            "問題番号・配点・正解",
            "問題の種類と採点方法",
            "漢字の扱い・別表記",
            "解答欄のページ・左・上・幅・高さ",
            "記述問題の確認要否と先生メモ",
        ],
        114,
        123,
        76,
        size=7.3,
        leading=3.7,
        gap=1,
    )
    label_chip(c, 1, "問題一覧", 14, 77, 42)
    label_chip(c, 2, "解答領域", 57, 77, 42)
    label_chip(c, 3, "採点設定", 100, 77, 42)
    callout(
        c,
        14,
        31,
        182,
        34,
        "公開後の版は変更できません",
        "検証でブロック項目がないことを確認して公開します。あとで修正するときは新しい版を作成します。採点開始済みの答案は、開始時の版を使い続けます。",
        tone="warn",
    )
    new_page(c)

    # 9. Sessions
    section_header(c, "08  テスト実施", "受付を作成して名簿を設定する", 9)
    sessions_rect = place_image(
        c,
        SCREEN_DIR / "11b-sessions-desktop.png",
        14,
        162,
        182,
        102,
    )
    marker(c, 1, sessions_rect[0] + 171, sessions_rect[1] + 83)
    marker(c, 2, sessions_rect[0] + 70, sessions_rect[1] + 39)
    create_session_rect = place_image(
        c,
        SCREEN_DIR / "12b-session-create-dialog.png",
        14,
        57,
        182,
        102,
    )
    marker(c, 3, create_session_rect[0] + 80, create_session_rect[1] + 69)
    marker(c, 4, create_session_rect[0] + 59, create_session_rect[1] + 24)
    bullet_list(
        c,
        [
            "公開済みのひな形を選び、実施名・実施日・クラス・コースを入力します。",
            "費用優先は待ち時間が長くなる場合があります。優先処理は費用が高くなる可能性があります。",
            "「作成後すぐに受付」をオンにすると、スキャン担当もアップロードできます。",
            "受付を終了すると新規アップロードは止まりますが、送信済み処理は続きます。",
        ],
        14,
        47,
        182,
        size=7.7,
        leading=4,
        gap=1.2,
    )
    new_page(c)

    # 10. Upload
    section_header(c, "09  答案受付", "答案をアップロードする", 10)
    paragraph(
        c,
        "受付中のテストを開き、PDF・JPEG・PNG・TIFFを選択します。複数ファイルをまとめて送信できます。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    upload_rect = place_image(
        c,
        SCREEN_DIR / "13-upload-board.png",
        14,
        75,
        182,
        170,
    )
    marker(c, 1, upload_rect[0] + 115, upload_rect[1] + upload_rect[3] - 68)
    marker(c, 2, upload_rect[0] + 112, upload_rect[1] + 73)
    marker(c, 3, upload_rect[0] + 171, upload_rect[1] + 27)
    label_chip(c, 1, "ドロップ領域", 14, 60, 50)
    label_chip(c, 2, "状態フィルター", 68, 60, 54)
    label_chip(c, 3, "詳細を開く", 126, 60, 50)
    callout(
        c,
        14,
        27,
        182,
        25,
        "重複と画像品質",
        "同じファイルを再送した場合は、既存を使う / 別受験回として追加 / 取り消すを選びます。品質警告は採点前に解決してください。",
        tone="warn",
    )
    new_page(c)

    # 11. Name review
    section_header(c, "10  生徒名確認", "答案を生徒へ割り当てる", 11)
    paragraph(
        c,
        "自動割り当ては行いません。答案の氏名、生徒番号、名簿候補を見比べて職員が決定します。",
        14,
        257,
        182,
        size=9,
        leading=4.5,
        color=MUTED,
    )
    name_rect = place_image(
        c,
        SCREEN_DIR / "14-name-review.png",
        14,
        109,
        182,
        137,
    )
    marker(c, 1, name_rect[0] + 93, name_rect[1] + 66)
    marker(c, 2, name_rect[0] + 159, name_rect[1] + 56)
    rounded_box(c, 14, 34, 182, 61, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 84, "確認手順", size=10, color=DARK)
    bullet_list(
        c,
        [
            "左の確認待ち一覧から答案を選びます。",
            "氏名または生徒番号で名簿を検索し、候補を1名選びます。",
            "一致を確認して「この生徒に割り当てる」を押します。",
            "判読できない場合や生徒の答案でない場合は、専用ボタンで扱いを記録します。",
            "重複受験は「第N回として登録」と「代表答案にする」を混同しないでください。",
        ],
        20,
        75,
        168,
        size=7.6,
        leading=3.9,
        gap=1,
    )
    new_page(c)

    # 12. Grading review
    section_header(
        c,
        "11  採点確認",
        "手動で読み取り・点数・理由を確定する",
        12,
    )
    grading_rect = place_image(
        c,
        SCREEN_DIR / "16-grading-partial.png",
        14,
        68,
        182,
        184,
        crop=(258, 0, 1265, 1191),
    )
    marker(c, 1, grading_rect[0] + 78, grading_rect[1] + 132)
    marker(c, 2, grading_rect[0] + 145, grading_rect[1] + 105)
    marker(c, 3, grading_rect[0] + 160, grading_rect[1] + 65)
    marker(c, 4, grading_rect[0] + 161, grading_rect[1] + 28)
    label_chip(c, 1, "問題と正解", 14, 54, 41)
    label_chip(c, 2, "読み取り修正", 57, 54, 46)
    label_chip(c, 3, "点数・理由", 105, 54, 42)
    label_chip(c, 4, "確定して次へ", 149, 54, 47)
    callout(
        c,
        14,
        25,
        182,
        19,
        "プロバイダー不要の手動フォールバック",
        "AI接続を使わない場合やAI提案が利用できない場合も、先生が読み取り、点数、理由を直接確認・修正できます。部分点やその他の判断は短いメモに残します。",
        tone="info",
    )
    new_page(c)

    # 13. Finalization
    section_header(c, "12  答案確定", "最終チェック後に結果へ反映する", 13)
    finalize_rect = place_image(
        c,
        SCREEN_DIR / "17-finalize-queue.png",
        14,
        144,
        182,
        106,
        crop=(258, 0, 1265, 739),
    )
    marker(c, 1, finalize_rect[0] + 67, finalize_rect[1] + 53)
    marker(c, 2, finalize_rect[0] + 150, finalize_rect[1] + 39)
    confirm_rect = place_image(
        c,
        SCREEN_DIR / "18-finalize-confirm.png",
        14,
        76,
        112,
        65,
        crop=(258, 0, 1265, 739),
    )
    rounded_box(c, 132, 76, 64, 65, fill=WHITE, stroke=BORDER)
    draw_text(c, 138, 130, "確定時の再確認", size=9.3, color=DARK)
    bullet_list(
        c,
        [
            "生徒または未特定の扱い",
            "全問題の採点結果",
            "要確認項目の解決",
            "合計点の整合性",
        ],
        138,
        121,
        52,
        size=7.4,
        leading=3.8,
        gap=1.2,
    )
    callout(
        c,
        14,
        33,
        182,
        31,
        "確定後の修正",
        "確定すると進捗と帳票へ反映されます。変更が必要な場合は、結果画面から理由を記録して開き直し、修正後に再確定します。",
        tone="warn",
    )
    new_page(c)

    # 14. Reports and results
    section_header(c, "13  結果確認", "帳票一覧と問題ごとの結果", 14)
    reports_rect = place_image(
        c,
        SCREEN_DIR / "19b-reports-desktop.png",
        14,
        164,
        182,
        102,
    )
    marker(c, 1, reports_rect[0] + 82, reports_rect[1] + 39)
    marker(c, 2, reports_rect[0] + 172, reports_rect[1] + 39)
    result_rect = place_image(
        c,
        SCREEN_DIR / "20-result-detail.png",
        14,
        61,
        182,
        99,
        crop=(258, 0, 1265, 720),
    )
    marker(c, 3, result_rect[0] + 147, result_rect[1] + 79)
    marker(c, 4, result_rect[0] + 84, result_rect[1] + 42)
    label_chip(c, 1, "得点・得点率", 14, 47, 43)
    label_chip(c, 2, "結果詳細", 59, 47, 36)
    label_chip(c, 3, "採点を修正", 97, 47, 43)
    label_chip(c, 4, "問題別結果", 142, 47, 45)
    paragraph(
        c,
        "画像の保存期間が過ぎても、問題文・読み取り結果・点数・履歴などの構造化結果は残ります。",
        14,
        33,
        182,
        size=8,
        leading=4.2,
        color=MUTED,
    )
    new_page(c)

    # 15. Result PDF
    section_header(c, "14  結果PDF", "日本語PDFを作成・確認する", 15)
    paragraph(
        c,
        "結果画面の「結果PDF」から出力内容を確認し、作成完了後にダウンロードします。",
        14,
        257,
        182,
        size=9,
        leading=4.5,
        color=MUTED,
    )
    pdf_dialog_rect = place_image(
        c,
        SCREEN_DIR / "21-result-pdf-dialog.png",
        14,
        151,
        88,
        96,
        crop=(258, 240, 1050, 1040),
    )
    pdf_ready_rect = place_image(
        c,
        SCREEN_DIR / "22-result-pdf-ready.png",
        108,
        151,
        88,
        96,
        crop=(258, 240, 1050, 1040),
    )
    marker(c, 1, pdf_dialog_rect[0] + 67, pdf_dialog_rect[1] + 20)
    marker(c, 2, pdf_ready_rect[0] + 68, pdf_ready_rect[1] + 19)
    draw_text(c, 14, 140, "作成内容を確認", size=8.5, color=DARK)
    draw_text(c, 108, 140, "検証済みPDFをダウンロード", size=8.5, color=DARK)
    rounded_box(c, 14, 76, 88, 51, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 117, "PDFに含まれるもの", size=9, color=DARK)
    bullet_list(
        c,
        [
            "学校名・生徒名・テスト名・実施日",
            "合計点・得点率",
            "問題文・読み取り済み解答・配点",
            "現在の訂正済み採点結果",
        ],
        20,
        108,
        76,
        size=7.3,
        leading=3.7,
        gap=1,
        dot_color=GREEN,
    )
    rounded_box(c, 108, 76, 88, 51, fill=ORANGE_PALE, stroke=ORANGE)
    draw_text(c, 114, 117, "PDFに含まれないもの", size=9, color=DARK)
    bullet_list(
        c,
        [
            "答案画像",
            "内部の確信度",
            "職員向けメモ",
            "秘密鍵や内部処理情報",
        ],
        114,
        108,
        76,
        size=7.3,
        leading=3.7,
        gap=1,
        dot_color=ORANGE,
    )
    callout(
        c,
        14,
        32,
        182,
        31,
        "閲覧担当の権限",
        "閲覧担当は確定済み結果を表示できますが、PDF作成や採点修正はできません。作成済みPDFの扱いは学校の保存方針に従ってください。",
        tone="info",
    )
    new_page(c)

    # 16. Gemini administration
    section_header(
        c,
        "15  AI管理",
        "Gemini 3.5 Flash-Lite の実接続を確認する",
        16,
    )
    paragraph(
        c,
        "分離したデモ環境で、通常APIへの実接続と「Gemini ひな形抽出」プロファイルの有効化を確認しました。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    ai_connection_rect = place_image(
        c,
        SCREEN_DIR / "29-admin-ai-connected.png",
        14,
        137,
        88,
        108,
        crop=(280, 300, 1410, 800),
    )
    marker(
        c,
        1,
        ai_connection_rect[0] + 25,
        ai_connection_rect[1] + 25,
    )
    ai_profile_rect = place_image(
        c,
        SCREEN_DIR / "30-admin-ai-profiles-active.png",
        108,
        137,
        88,
        108,
        crop=(280, 1500, 1410, 2020),
    )
    marker(
        c,
        2,
        ai_profile_rect[0] + 30,
        ai_profile_rect[1] + 73,
    )
    label_chip(c, 1, "通常API: passed", 14, 122, 82)
    label_chip(c, 2, "ひな形抽出: 有効", 108, 122, 88)
    callout(
        c,
        14,
        75,
        88,
        35,
        "実際に確認した範囲",
        "Gemini 3.5 Flash-Lite の通常APIを使用し、日本語の社会科試験画像から先生確認用のひな形下書きを作る流れを確認しました。",
        tone="safe",
    )
    callout(
        c,
        108,
        75,
        88,
        35,
        "Batch は使っていません",
        "任意の Batch 接続確認はこのデモでは失敗しました。今回の実行は passed の通常APIだけを使っています。",
        tone="warn",
    )
    callout(
        c,
        14,
        29,
        182,
        34,
        "評価記録はデモ専用",
        "プロファイル有効化に使った合成評価記録は画面説明用です。学校導入前に必要な実答案のゴールデンセット検証を満たすものではなく、秘密値そのものも本書には掲載していません。",
        tone="info",
    )
    new_page(c)

    # 17. Live Gemini template extraction
    section_header(
        c,
        "16  AI下書き",
        "実際のAI処理を確認し、先生が仕上げる",
        17,
    )
    paragraph(
        c,
        "通常APIを使い、問題と解答欄が混在する日本語・中学1年社会の問題用紙から、16問のひな形下書きを作成した実画面です。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    generating_rect = place_image(
        c,
        SCREEN_DIR / "31-template-ai-generating.png",
        14,
        156,
        182,
        88,
    )
    marker(
        c,
        1,
        generating_rect[0] + generating_rect[2] * 0.61,
        generating_rect[1] + generating_rect[3] * 0.71,
    )
    draw_text(
        c,
        14,
        147,
        "1  追加後: generating の間も画面を閉じられ、処理は学校内ホストで続きます。",
        size=8.1,
        color=DARK,
    )
    proposal_rect = place_image(
        c,
        SCREEN_DIR / "32-template-editor-ai-proposal.png",
        14,
        59,
        182,
        82,
    )
    marker(
        c,
        2,
        proposal_rect[0] + proposal_rect[2] * 0.70,
        proposal_rect[1] + proposal_rect[3] * 0.66,
    )
    draw_text(
        c,
        14,
        50,
        "2  完了後: 例外だけ一覧に残ります。この例は別紙解答がないため、16問すべて要確認です。",
        size=8.1,
        color=DARK,
    )
    callout(
        c,
        14,
        21,
        182,
        20,
        "公開は先生が決める",
        "安全な客観式提案はまとめて確認できますが、正答不足・低信頼・記述式・領域不明は個別確認を残します。先生が検証してから公開し、AIは自動公開しません。",
        tone="warn",
    )
    new_page(c)

    # 18. Operations and troubleshooting
    section_header(c, "17  システム管理", "状態・保存容量・ジョブ・バックアップ", 18)
    health_rect = place_image(
        c,
        SCREEN_DIR / "23-admin-health.png",
        14,
        147,
        182,
        117,
        crop=(0, 0, 1440, 850),
    )
    marker(c, 1, health_rect[0] + 77, health_rect[1] + 83)
    marker(c, 2, health_rect[0] + 143, health_rect[1] + 28)
    storage_rect = place_image(
        c,
        SCREEN_DIR / "27-admin-storage.png",
        14,
        91,
        88,
        50,
    )
    jobs_rect = place_image(
        c,
        SCREEN_DIR / "28-admin-jobs.png",
        108,
        91,
        88,
        50,
    )
    draw_text(c, 14, 81, "保存容量・保持設定", size=8.2, color=DARK)
    draw_text(c, 108, 81, "処理・ジョブ・安全な再試行", size=8.2, color=DARK)
    callout(
        c,
        14,
        36,
        88,
        35,
        "バックアップ",
        "有効化・保存先・暗号化確認・到達確認が揃うまで手動バックアップは使えません。ブラウザは復元計画を検証し、実復元は停止したホストと OokiGrader.Tool で行います。",
        tone="warn",
    )
    callout(
        c,
        108,
        36,
        88,
        35,
        "問題が解決しないとき",
        "画面の相関ID、実施名、ファイル名、発生時刻、操作内容を控えます。失敗ジョブは原因を確認してから安全な再試行を使い、同じ答案を何度も送りません。",
        tone="info",
    )
    draw_text(
        c,
        14,
        25,
        "注意: スキャン画像のクリーンアップは取り消せません。採点結果・履歴・帳票は残ります。",
        size=7.4,
        color=ORANGE,
    )

    c.save()
    print(OUTPUT)


def automation_step(
    c: canvas.Canvas,
    number: int,
    title: str,
    body: str,
    x: float,
    y: float,
    width: float,
    *,
    tone: str = "safe",
) -> None:
    palette = {
        "safe": (MINT, GREEN),
        "info": (BLUE_PALE, BLUE),
        "warn": (ORANGE_PALE, ORANGE),
        "plain": (WHITE, BORDER),
    }
    fill, accent = palette[tone]
    rounded_box(c, x, y, width, 31, fill=fill, stroke=accent)
    c.setFillColor(accent)
    c.circle(mm(x + 8), mm(y + 21), mm(4.6), fill=1, stroke=0)
    c.setFillColor(WHITE)
    c.setFont(FONT, 9.5)
    c.drawCentredString(mm(x + 8), mm(y + 19.5), str(number))
    draw_text(c, x + 16, y + 21, title, size=9.4, color=DARK)
    paragraph(
        c,
        body,
        x + 16,
        y + 14,
        width - 21,
        size=7.4,
        leading=3.8,
        color=MUTED,
    )


def ai_pipeline(
    c: canvas.Canvas,
    labels: list[str],
    x: float,
    y: float,
    width: float,
) -> None:
    gap = 3
    box_width = (width - gap * (len(labels) - 1)) / len(labels)
    for index, label in enumerate(labels):
        box_x = x + index * (box_width + gap)
        rounded_box(
            c,
            box_x,
            y,
            box_width,
            22,
            fill=MINT if index not in {0, len(labels) - 1} else WHITE,
            stroke=GREEN,
            radius=2,
        )
        draw_text(
            c,
            box_x + 2.2,
            y + 14.8,
            f"{index + 1}",
            size=7,
            color=ORANGE,
        )
        lines = wrap_line(label, 7.1, box_width - 4.4)
        for line_index, line in enumerate(lines[:2]):
            draw_text(
                c,
                box_x + 2.2,
                y + 9.2 - line_index * 3.6,
                line,
                size=7.1,
                color=DARK,
            )
        if index < len(labels) - 1:
            start = box_x + box_width + 0.5
            c.setStrokeColor(MUTED)
            c.setLineWidth(0.8)
            c.line(mm(start), mm(y + 11), mm(start + gap - 1), mm(y + 11))
            c.line(
                mm(start + gap - 2),
                mm(y + 12.3),
                mm(start + gap - 1),
                mm(y + 11),
            )
            c.line(
                mm(start + gap - 2),
                mm(y + 9.7),
                mm(start + gap - 1),
                mm(y + 11),
            )


def build_ai_first_legacy() -> None:
    register_font()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    PROCESSED_DIR.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(OUTPUT), pagesize=A4, pageCompression=1)
    c.setTitle("Ooki Grader AI活用ユーザーガイド")
    c.setAuthor("Ooki Grader")
    c.setSubject("AIによるひな形作成・氏名読み取り・採点支援の日本語ガイド")
    c.setKeywords(
        "Ooki Grader, AI, Gemini, ひな形, 採点, 日本語, ユーザーガイド"
    )

    # 1. AI-first cover
    c.setFillColor(PALE)
    c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    c.setFillColor(DARK)
    c.rect(0, 0, mm(10), PAGE_H, fill=1, stroke=0)
    c.setFillColor(ORANGE)
    c.circle(mm(29), mm(271), mm(11), fill=1, stroke=0)
    c.setFillColor(WHITE)
    c.setFont(FONT, 19)
    c.drawCentredString(mm(29), mm(264.5), "大")
    draw_text(c, 47, 274, "OOKI GRADER", size=8, color=GREEN)
    draw_text(c, 18, 248, "AI活用", size=29, color=DARK)
    draw_text(c, 18, 234, "ユーザーガイド", size=29, color=DARK)
    draw_text(
        c,
        18,
        221,
        "作成と採点を自動化し、先生は例外だけ確認",
        size=11.5,
        color=MUTED,
    )
    cover_rect = place_image(
        c,
        SCREEN_DIR / "32b-template-editor-ai-proposal-wide.png",
        16,
        83,
        178,
        126,
        border=GREEN,
    )
    marker(
        c,
        1,
        cover_rect[0] + cover_rect[2] * 0.50,
        cover_rect[1] + cover_rect[3] * 0.86,
    )
    marker(
        c,
        2,
        cover_rect[0] + cover_rect[2] * 0.89,
        cover_rect[1] + cover_rect[3] * 0.61,
    )
    callout(
        c,
        18,
        39,
        176,
        31,
        "このガイドで覚えること",
        "資料を一度まとめて追加し、Geminiの下書きを待ち、安全な提案をまとめて確認します。先生が個別に触るのは正答不足・低信頼・記述式などの例外だけです。",
        tone="safe",
    )
    draw_text(
        c,
        18,
        25,
        "Gemini 3.5 Flash-Lite 実接続画面を含む / 対象バージョン v0.1",
        size=7.6,
        color=MUTED,
    )
    draw_right(c, 195, 25, "2026年8月5日", size=7.6, color=MUTED)
    footer(c, 1)
    new_page(c)

    # 2. Shortest path
    section_header(c, "01  最短ルート", "先生が行うのは3つだけ", 2)
    paragraph(
        c,
        "通常運用では、設定画面を順番に埋める必要はありません。ファイルをまとめて追加し、要確認だけを処理し、最後に公開・確定します。",
        14,
        257,
        182,
        size=9,
        leading=4.6,
        color=MUTED,
    )
    automation_step(
        c,
        1,
        "資料をまとめて追加",
        "問題用紙と手元の解答資料を一度に選びます。各ファイルの資料区分は自動提案され、違う時だけ直します。",
        14,
        211,
        56,
        tone="info",
    )
    automation_step(
        c,
        2,
        "要確認だけを見る",
        "処理中は画面を閉じて構いません。完了後は「要確認」だけを開き、安全な客観式は一括確認します。",
        77,
        211,
        56,
        tone="safe",
    )
    automation_step(
        c,
        3,
        "先生が公開・確定",
        "AIは下書きと提案を作ります。ひな形の公開、答案の割り当て、最終確定は職員が行います。",
        140,
        211,
        56,
        tone="warn",
    )
    draw_text(c, 14, 193, "Ooki Grader が自動で進める範囲", size=11, color=DARK)
    ai_pipeline(
        c,
        [
            "資料を追加",
            "区分・題名を推定",
            "既存ひな形を照合",
            "問題・正答を抽出",
            "氏名・答案を読み取り",
            "採点・再確認",
            "例外だけ表示",
        ],
        14,
        160,
        182,
    )
    rounded_box(c, 14, 82, 88, 61, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 132, "手作業を増やさない", size=10, color=DARK)
    bullet_list(
        c,
        [
            "任意の基本情報欄は、推定が誤っている時だけ開く",
            "記入済み答案は、正答か生徒答案かだけ確認する",
            "同じ資料が見つかったら、既存の公開済み版を使う",
            "処理中に待たず、ダッシュボードへ戻る",
        ],
        20,
        122,
        76,
        size=7.5,
        leading=3.8,
        gap=1.2,
        dot_color=GREEN,
    )
    rounded_box(c, 108, 82, 88, 61, fill=ORANGE_PALE, stroke=ORANGE)
    draw_text(c, 114, 132, "先生が判断を残す場面", size=10, color=DARK)
    bullet_list(
        c,
        [
            "正答資料がない、またはAIの確信が低い",
            "記述式、部分点、複数の解釈があり得る",
            "氏名候補が複数、または読み取り不能",
            "公開・結果確定など取り消しに注意が必要",
        ],
        114,
        122,
        76,
        size=7.5,
        leading=3.8,
        gap=1.2,
        dot_color=ORANGE,
    )
    callout(
        c,
        14,
        31,
        182,
        35,
        "最も効く時短",
        "模範解答がある時は同時に追加してください。生徒などの記入済み答案しかない時は「AIが正答を作成」を選べば、書かれた答えを正答にせずAIが独立して解きます。",
        tone="safe",
    )
    new_page(c)

    # 3. Input bundle
    section_header(c, "02  入力準備", "AIが迷わない資料の渡し方", 3)
    paragraph(
        c,
        "紙面の枠や座標を登録する必要はありません。追加した各ファイルについて、次の4区分から最も近いものを選びます。通常は自動提案の確認だけです。",
        14,
        257,
        182,
        size=9,
        leading=4.6,
        color=MUTED,
    )
    exam_rect = place_image(
        c,
        FIXTURE_DIR / "rendered" / "blank.png",
        14,
        104,
        78,
        142,
        border=BLUE,
    )
    marker(
        c,
        1,
        exam_rect[0] + exam_rect[2] * 0.30,
        exam_rect[1] + exam_rect[3] * 0.70,
    )
    marker(
        c,
        2,
        exam_rect[0] + exam_rect[2] * 0.30,
        exam_rect[1] + exam_rect[3] * 0.50,
    )
    rows = [
        (
            "問題のみ（未記入）",
            "空欄の用紙。AIが問題を読み、自分で正答を作ります。",
            BLUE_PALE,
            BLUE,
        ),
        (
            "模範解答入り",
            "書かれた答えを正答として読み取ります。",
            MINT,
            GREEN,
        ),
        (
            "記入済み答案（AIが正答を作成）",
            "書かれた答えは正答に使わず、AIが問題を独立して解きます。",
            ORANGE_PALE,
            ORANGE,
        ),
        (
            "別紙の模範解答",
            "別紙の正答・別表記・配点・記述式の観点を使います。",
            ORANGE_PALE,
            ORANGE,
        ),
    ]
    for index, (title, body, fill, accent) in enumerate(rows):
        y = 213 - index * 35
        rounded_box(c, 101, y, 95, 29, fill=fill, stroke=accent)
        marker(c, index + 1, 108, y + 20)
        draw_text(c, 116, y + 20, title, size=8.3, color=DARK)
        paragraph(
            c,
            body,
            116,
            y + 12,
            74,
            size=6.9,
            leading=3.5,
            color=MUTED,
        )
    callout(
        c,
        14,
        67,
        182,
        25,
        "ファイル名だけ整える",
        "例: 中1_社会_アジア州_問題.pdf / 中1_社会_アジア州_模範解答.pdf / 中1_社会_生徒答案.pdf。分かりやすい名前ほど資料区分の提案が安定します。",
        tone="info",
    )
    rounded_box(c, 14, 28, 182, 28, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 46, "対応形式", size=8.5, color=DARK)
    draw_text(
        c,
        48,
        46,
        "PDF / JPEG / PNG / TIFF",
        size=8.5,
        color=GREEN,
    )
    paragraph(
        c,
        "複数ページTIFFも1資料として扱われます。問題用紙と解答資料は、同じ画面でまとめて選択してください。",
        20,
        38,
        168,
        size=7.6,
        leading=4,
        color=MUTED,
    )
    new_page(c)

    # 4. One-time AI setup
    section_header(c, "03  管理者のみ", "Geminiを一度だけ接続する", 4)
    paragraph(
        c,
        "先生はAPIキーを扱いません。管理者が接続テスト、評価記録、価格、予算を設定すると、承認済みAIプロファイルが各画面から自動的に使われます。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    connection_rect = place_image(
        c,
        SCREEN_DIR / "29-admin-ai-connected.png",
        14,
        141,
        88,
        101,
        crop=(280, 444, 486, 681),
    )
    profile_rect = place_image(
        c,
        SCREEN_DIR / "30-admin-ai-profiles-active.png",
        108,
        141,
        88,
        101,
        crop=(280, 1530, 1410, 2020),
    )
    marker(
        c,
        1,
        connection_rect[0] + connection_rect[2] * 0.50,
        connection_rect[1] + connection_rect[3] * 0.12,
    )
    marker(
        c,
        2,
        profile_rect[0] + profile_rect[2] * 0.70,
        profile_rect[1] + profile_rect[3] * 0.18,
    )
    label_chip(c, 1, "通常APIが passed", 14, 126, 82)
    label_chip(c, 2, "ひな形抽出が使用中", 108, 126, 88)
    rounded_box(c, 14, 73, 182, 41, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 104, "初回チェック", size=9.5, color=DARK)
    bullet_list(
        c,
        [
            "管理 > Gemini AI でAPIキーを登録し、「接続・能力テスト」を実行",
            "gemini-3.5-flash-lite と通常APIの passed を確認",
            "学校の評価記録を承認後、ひな形・氏名・採点プロファイルを有効化",
            "公式価格スナップショットと日次・月次の予算上限を保存",
        ],
        20,
        95,
        168,
        size=7.4,
        leading=3.8,
        gap=1,
    )
    callout(
        c,
        14,
        30,
        88,
        31,
        "秘密値は表示されません",
        "画面・監査ログ・本書にはAPIキーそのものを出しません。交換時も管理者が新しいキーへ置き換えます。",
        tone="safe",
    )
    callout(
        c,
        108,
        30,
        88,
        31,
        "通常APIが標準",
        "このガイドの実演は通常APIです。Batchは任意で、能力テストに合格した環境だけで有効にします。",
        tone="info",
    )
    new_page(c)

    # 5. Upload and reuse
    section_header(c, "04  ひな形自動作成", "資料を一度まとめて追加する", 5)
    create_rect = place_image(
        c,
        SCREEN_DIR / "10-template-create.png",
        14,
        166,
        182,
        91,
    )
    marker(
        c,
        1,
        create_rect[0] + create_rect[2] * 0.60,
        create_rect[1] + create_rect[3] * 0.49,
    )
    draw_text(
        c,
        14,
        157,
        "1  テストひな形 > 自動作成で、問題用紙と解答資料を同時にドロップ",
        size=8,
        color=DARK,
    )
    match_rect = place_image(
        c,
        SCREEN_DIR / "34-template-role-review.png",
        14,
        62,
        182,
        87,
        crop=(350, 555, 1340, 880),
    )
    marker(
        c,
        2,
        match_rect[0] + match_rect[2] * 0.70,
        match_rect[1] + match_rect[3] * 0.50,
    )
    marker(
        c,
        3,
        match_rect[0] + match_rect[2] * 0.70,
        match_rect[1] + match_rect[3] * 0.14,
    )
    draw_text(
        c,
        14,
        53,
        "2  資料区分は違う時だけ変更　　3  記入済み答案を正答にしない説明を確認",
        size=7.8,
        color=DARK,
    )
    callout(
        c,
        14,
        17,
        182,
        25,
        "分類だけ確認する",
        "題名・教科などの任意欄は通常開きません。記入済み答案が模範解答でない時だけ「AIが正答を作成」を選びます。同じファイル・同じ資料区分の公開版だけが再利用候補になります。",
        tone="safe",
    )
    new_page(c)

    # 6. Live generation
    section_header(c, "05  AI処理", "待たずに、完了後だけ戻る", 6)
    paragraph(
        c,
        "実際のGemini 3.5 Flash-Lite通常APIで、日本語の中学1年社会の問題用紙を処理した画面です。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    generating_rect = place_image(
        c,
        SCREEN_DIR / "31-template-ai-generating.png",
        14,
        160,
        182,
        85,
    )
    marker(
        c,
        1,
        generating_rect[0] + generating_rect[2] * 0.57,
        generating_rect[1] + generating_rect[3] * 0.82,
    )
    draw_text(
        c,
        14,
        151,
        "1  generating を確認したら画面を閉じて構いません。学校内ホストで処理が続きます。",
        size=7.9,
        color=DARK,
    )
    proposal_rect = place_image(
        c,
        SCREEN_DIR / "32b-template-editor-ai-proposal-wide.png",
        14,
        55,
        182,
        89,
    )
    marker(
        c,
        2,
        proposal_rect[0] + proposal_rect[2] * 0.14,
        proposal_rect[1] + proposal_rect[3] * 0.63,
    )
    marker(
        c,
        3,
        proposal_rect[0] + proposal_rect[2] * 0.88,
        proposal_rect[1] + proposal_rect[3] * 0.63,
    )
    draw_text(
        c,
        14,
        46,
        "2  問題を自動抽出　　3  問題文・種類・配点・正答候補を編集可能な下書きで表示",
        size=7.8,
        color=DARK,
    )
    callout(
        c,
        14,
        20,
        182,
        17,
        "この実演の結果",
        "問題用紙だけだったため16問すべて個別確認になりました。次回は別紙解答も同時に追加すると、正答根拠がある安全な客観式を一括確認できます。",
        tone="warn",
    )
    new_page(c)

    # 7. Exception-only review
    section_header(c, "06  例外レビュー", "「すべて」ではなく「要確認」だけを見る", 7)
    editor_rect = place_image(
        c,
        SCREEN_DIR / "32b-template-editor-ai-proposal-wide.png",
        14,
        111,
        182,
        142,
        crop=(240, 110, 1440, 1000),
        border=GREEN,
    )
    marker(
        c,
        1,
        editor_rect[0] + editor_rect[2] * 0.06,
        editor_rect[1] + editor_rect[3] * 0.82,
    )
    marker(
        c,
        2,
        editor_rect[0] + editor_rect[2] * 0.91,
        editor_rect[1] + editor_rect[3] * 0.72,
    )
    label_chip(c, 1, "要確認フィルター", 14, 97, 82)
    label_chip(c, 2, "提案を採用", 108, 97, 82)
    rounded_box(c, 14, 48, 88, 37, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 75, "まとめて確認できるもの", size=9, color=DARK)
    bullet_list(
        c,
        [
            "選択式・真偽・数値・完全一致短答",
            "正答根拠があり、確信度が高い",
            "内容の矛盾や警告がない",
        ],
        20,
        66,
        76,
        size=7.1,
        leading=3.6,
        gap=0.8,
        dot_color=GREEN,
    )
    rounded_box(c, 108, 48, 88, 37, fill=ORANGE_PALE, stroke=ORANGE)
    draw_text(c, 114, 75, "個別確認に残るもの", size=9, color=DARK)
    bullet_list(
        c,
        [
            "記述式・部分点・正答不足",
            "低信頼・未対応形式・内容不足",
            "先生の判断を必須にした問題",
        ],
        114,
        66,
        76,
        size=7.1,
        leading=3.6,
        gap=0.8,
        dot_color=ORANGE,
    )
    callout(
        c,
        14,
        20,
        182,
        17,
        "最後だけ先生",
        "例外を解決したら「検証」を実行し、「確認して公開」を押します。AIが自動公開することはありません。",
        tone="safe",
    )
    new_page(c)

    # 8. Japanese interwoven layouts
    section_header(c, "07  日本語レイアウト", "問題と解答欄が混在していても任せる", 8)
    paragraph(
        c,
        "大木スクールの用紙のように、問題文・地図・表・短答欄・長い記述欄が1枚に混在していても、答案全体を見て問題番号と解答を対応付けます。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    blank_exam = place_image(
        c,
        FIXTURE_DIR / "rendered" / "blank.png",
        14,
        76,
        83,
        169,
        border=BLUE,
    )
    completed_exam = place_image(
        c,
        FIXTURE_DIR / "rendered" / "hanako.png",
        113,
        76,
        83,
        169,
        border=ORANGE,
    )
    marker(
        c,
        1,
        blank_exam[0] + blank_exam[2] * 0.28,
        blank_exam[1] + blank_exam[3] * 0.71,
    )
    marker(
        c,
        2,
        blank_exam[0] + blank_exam[2] * 0.29,
        blank_exam[1] + blank_exam[3] * 0.47,
    )
    marker(
        c,
        3,
        completed_exam[0] + completed_exam[2] * 0.31,
        completed_exam[1] + completed_exam[3] * 0.32,
    )
    draw_text(c, 14, 66, "空欄の問題用紙", size=8.3, color=BLUE)
    draw_text(c, 113, 66, "記入済み答案（架空）", size=8.3, color=ORANGE)
    callout(
        c,
        14,
        25,
        182,
        28,
        "枠も座標も作りません",
        "問題用紙も記入済み答案もページ全体をAIへ渡します。先生は問題番号・問題文・正答・配点だけを確認し、画像上で四角を描いたり位置を数値入力したりしません。",
        tone="safe",
    )
    new_page(c)

    # 9. Submission AI pipeline
    section_header(c, "08  答案処理", "まとめてアップロードし、AIへ流す", 9)
    paragraph(
        c,
        "公開済みひな形からテスト実施を1回作り、答案をまとめて追加します。送信後は、画像処理から採点候補までバックグラウンドで進みます。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    upload_rect = place_image(
        c,
        SCREEN_DIR / "13-upload-board.png",
        14,
        137,
        182,
        107,
        crop=(250, 70, 1440, 920),
    )
    marker(
        c,
        1,
        upload_rect[0] + upload_rect[2] * 0.50,
        upload_rect[1] + upload_rect[3] * 0.49,
    )
    draw_text(
        c,
        14,
        128,
        "1  PDF・JPEG・PNG・TIFFを複数選択。画面を閉じても送信済み処理は継続します。",
        size=7.9,
        color=DARK,
    )
    ai_pipeline(
        c,
        [
            "画像品質を自動確認",
            "氏名・番号を読み取り",
            "客観式を規則採点",
            "記述式をGemini採点",
            "あいまい答案を再確認",
            "例外だけ待ち行列へ",
        ],
        14,
        90,
        182,
    )
    rounded_box(c, 14, 31, 88, 44, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 64, "自動で完了するもの", size=9, color=DARK)
    bullet_list(
        c,
        [
            "完全一致など決定的な客観式",
            "承認済み基準で高信頼なAI提案",
            "安全な再確認で一致した答案",
        ],
        20,
        55,
        76,
        size=7.2,
        leading=3.6,
        gap=0.8,
        dot_color=GREEN,
    )
    rounded_box(c, 108, 31, 88, 44, fill=ORANGE_PALE, stroke=ORANGE)
    draw_text(c, 114, 64, "先生へ回すもの", size=9, color=DARK)
    bullet_list(
        c,
        [
            "氏名候補が不明または複数",
            "部分点・判読困難・根拠不足",
            "再確認でも判断が一致しない",
        ],
        114,
        55,
        76,
        size=7.2,
        leading=3.6,
        gap=0.8,
        dot_color=ORANGE,
    )
    new_page(c)

    # 10. Teacher review queue
    section_header(c, "09  確認待ち", "AIが迷った答案だけ処理する", 10)
    paragraph(
        c,
        "「採点待ち・確認」はAIの失敗一覧ではなく、人の判断が必要な例外一覧です。タブの件数だけを処理します。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    queue_rect = place_image(
        c,
        SCREEN_DIR / "14-name-review.png",
        14,
        150,
        182,
        96,
        border=GREEN,
    )
    marker(
        c,
        1,
        queue_rect[0] + queue_rect[2] * 0.08,
        queue_rect[1] + queue_rect[3] * 0.24,
    )
    label_chip(c, 1, "答案全体を見て候補だけ確認", 14, 136, 182)

    rounded_box(c, 14, 78, 182, 45, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 113, "氏名確認 — AIの候補から1回決定", size=9.2, color=DARK)
    ai_pipeline(
        c,
        [
            "氏名・番号を読取",
            "名簿を絞込",
            "候補を順位付け",
            "先生が最終確認",
        ],
        20,
        85,
        168,
    )
    draw_text(
        c,
        20,
        81,
        "答案全体を見ながら候補を選択。候補が出ない時だけ名簿検索します。",
        size=7.1,
        color=MUTED,
    )

    rounded_box(c, 14, 24, 182, 45, fill=BLUE_PALE, stroke=BLUE)
    draw_text(c, 20, 59, "採点確認 — AIの提案を初期値にする", size=9.2, color=DARK)
    ai_pipeline(
        c,
        [
            "答案を読取",
            "点数・理由を提案",
            "安全な再確認",
            "例外だけ先生へ",
        ],
        20,
        31,
        168,
    )
    draw_text(
        c,
        20,
        27,
        "先生は提案が不確かな項目だけ修正し、未解決0件で結果を確定します。",
        size=7.1,
        color=MUTED,
    )
    new_page(c)

    # 11. Monitoring and failures
    section_header(c, "10  AI運用", "費用・失敗・再試行を管理する", 11)
    metrics_rect = place_image(
        c,
        SCREEN_DIR / "29-admin-ai-connected.png",
        14,
        154,
        88,
        91,
        crop=(850, 280, 1410, 1120),
    )
    jobs_rect = place_image(
        c,
        SCREEN_DIR / "28-admin-jobs.png",
        108,
        154,
        88,
        91,
        crop=(250, 80, 1440, 820),
    )
    label_chip(c, 1, "使用量・費用", 14, 139, 82)
    label_chip(c, 2, "対応が必要な処理", 108, 139, 88)
    rounded_box(c, 14, 65, 182, 61, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 115, "状態ごとの対応", size=9.5, color=DARK)
    rows = [
        ("generating / 処理中", "待つ。画面を閉じてもよい。再アップロードしない。"),
        ("要確認", "故障ではない。例外内容を読み、該当項目だけ処理する。"),
        ("予算で停止", "管理者が価格と上限を確認。勝手に上限を外さない。"),
        ("失敗・結果不明", "処理・ジョブで相関IDを確認し、安全な再試行または照合を使う。"),
    ]
    for index, (state, action) in enumerate(rows):
        y = 104 - index * 11
        draw_text(c, 20, y, state, size=7.6, color=GREEN)
        paragraph(
            c,
            action,
            66,
            y,
            122,
            size=7.3,
            leading=3.8,
            color=MUTED,
        )
    callout(
        c,
        14,
        31,
        88,
        23,
        "同じ処理を連打しない",
        "結果不明時は既存処理を照合します。重複送信は費用と重複データを増やします。",
        tone="warn",
    )
    callout(
        c,
        108,
        31,
        88,
        23,
        "AI停止中も作業可能",
        "手動フォールバックで継続できます。元画像と途中状態は保持され、後から再開できます。",
        tone="info",
    )
    new_page(c)

    # 12. Quick reference
    section_header(c, "11  クイックリファレンス", "最小操作チェックリスト", 12)
    rounded_box(c, 14, 184, 88, 72, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 245, "ひな形を作る先生", size=10, color=DARK)
    bullet_list(
        c,
        [
            "問題用紙 + 手元の解答資料を同時に追加",
            "資料区分が違う時だけ修正",
            "生徒答案なら「AIが正答を作成」を選択",
            "完全一致が出たら既存の公開済み版を使用",
            "要確認だけ処理し、安全な客観式は一括確認",
        ],
        20,
        235,
        76,
        size=7.4,
        leading=3.8,
        gap=1.2,
        dot_color=GREEN,
    )
    rounded_box(c, 108, 184, 88, 72, fill=BLUE_PALE, stroke=BLUE)
    draw_text(c, 114, 245, "答案を処理する職員", size=10, color=DARK)
    bullet_list(
        c,
        [
            "同じ実施へ答案をまとめてアップロード",
            "処理中は待たず、確認待ち件数だけを見る",
            "氏名候補は選択し、読めない時だけ検索",
            "採点提案は例外だけ修正",
            "未解決0件を確認して結果を確定",
        ],
        114,
        235,
        76,
        size=7.4,
        leading=3.8,
        gap=1.2,
        dot_color=BLUE,
    )
    draw_text(c, 14, 169, "やらないこと", size=10.5, color=DARK)
    rounded_box(c, 14, 111, 182, 47, fill=ORANGE_PALE, stroke=ORANGE)
    bullet_list(
        c,
        [
            "AIの推定が正しいのに、任意の基本情報をすべて手入力する",
            "処理中に同じ資料・答案を繰り返しアップロードする",
            "「すべて」を上から順に開き、安全な自動処理済み項目まで再確認する",
            "正答資料を持っているのに、空欄の問題用紙だけを追加する",
            "生徒の記入済み答案を「模範解答入り」として扱う",
            "低信頼・記述式・部分点を根拠なしで一括確認する",
        ],
        20,
        147,
        168,
        size=7.5,
        leading=3.8,
        gap=1.1,
        dot_color=ORANGE,
    )
    callout(
        c,
        14,
        67,
        182,
        29,
        "実画面で確認済み",
        "Gemini 3.5 Flash-Lite通常APIで、問題と解答欄が混在する日本語社会科用紙から問題の下書きを作成しました。枠・座標・切り出しの設定は使用していません。",
        tone="safe",
    )
    rounded_box(c, 14, 29, 182, 26, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 45, "困ったときに控える情報", size=8.7, color=DARK)
    paragraph(
        c,
        "実施名 / ファイル名 / 発生時刻 / 画面の状態 / 相関ID / 行った操作。APIキーや生徒情報をメールへ貼り付けないでください。",
        20,
        37,
        168,
        size=7.4,
        leading=3.8,
        color=MUTED,
    )

    c.save()
    print(OUTPUT)


def build_ai_first() -> None:
    """Build the teacher-first guide for the simplified AI workflow."""

    register_font()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    PROCESSED_DIR.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(OUTPUT), pagesize=A4, pageCompression=1)
    c.setTitle("Ooki Grader AI活用ユーザーガイド")
    c.setAuthor("Ooki Grader")
    c.setSubject("先生向けのひな形作成・答案受付・AI採点ガイド")
    c.setKeywords(
        "Ooki Grader, AI, ひな形, 答案受付, 自動採点, 日本語, ユーザーガイド"
    )

    # 1. Cover
    c.setFillColor(PALE)
    c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    c.setFillColor(DARK)
    c.rect(0, 0, mm(10), PAGE_H, fill=1, stroke=0)
    c.setFillColor(ORANGE)
    c.circle(mm(29), mm(271), mm(11), fill=1, stroke=0)
    c.setFillColor(WHITE)
    c.setFont(FONT, 19)
    c.drawCentredString(mm(29), mm(264.5), "大")
    draw_text(c, 47, 274, "OOKI GRADER", size=8, color=GREEN)
    draw_text(c, 18, 248, "AI活用", size=29, color=DARK)
    draw_text(c, 18, 234, "ユーザーガイド", size=29, color=DARK)
    draw_text(
        c,
        18,
        221,
        "資料をまとめて追加し、先生は要修正だけ確認",
        size=11.5,
        color=MUTED,
    )
    cover_rect = place_image(
        c,
        SCREEN_DIR / "32b-template-editor-ai-proposal-wide.png",
        16,
        83,
        178,
        126,
        border=GREEN,
    )
    marker(
        c,
        1,
        cover_rect[0] + cover_rect[2] * 0.37,
        cover_rect[1] + cover_rect[3] * 0.60,
    )
    marker(
        c,
        2,
        cover_rect[0] + cover_rect[2] * 0.85,
        cover_rect[1] + cover_rect[3] * 0.64,
    )
    callout(
        c,
        18,
        39,
        176,
        31,
        "このガイドで行うこと",
        "問題用紙からAI下書きを作り、元の資料と見比べて公開します。その後は答案をまとめて追加し、AIが迷った項目だけを確認します。",
        tone="safe",
    )
    draw_text(
        c,
        18,
        25,
        "実画面による先生向け手順 / 2026年8月版 / 座標指定なし",
        size=7.6,
        color=MUTED,
    )
    draw_right(c, 195, 25, "2026年8月5日", size=7.6, color=MUTED)
    footer(c, 1)
    new_page(c)

    # 2. Shortest path
    section_header(c, "01  最短ルート", "AIに任せて、先生は例外だけ確認", 2)
    paragraph(
        c,
        "通常は、細かな設定画面を順番に埋める必要はありません。ひな形作成、答案受付、確認待ちの3場面だけ覚えれば使えます。",
        14,
        257,
        182,
        size=9,
        leading=4.6,
        color=MUTED,
    )
    automation_step(
        c,
        1,
        "資料を一度に追加",
        "問題用紙と解答資料をまとめて選び、分類が違う時だけ直します。",
        14,
        212,
        56,
        tone="info",
    )
    automation_step(
        c,
        2,
        "AI下書きを確認",
        "元の資料と見比べ、要修正だけ直して「公開する」を押します。",
        77,
        212,
        56,
        tone="safe",
    )
    automation_step(
        c,
        3,
        "答案の例外を確認",
        "答案は自動採点へ進みます。採点待ち・確認の件数だけ処理します。",
        140,
        212,
        56,
        tone="warn",
    )
    rounded_box(c, 14, 104, 88, 90, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 183, "ひな形を作る", size=10, color=DARK)
    bullet_list(
        c,
        [
            "問題用紙と解答資料をまとめて追加",
            "資料の種類は、提案が違う時だけ変更",
            "「元の資料」とAI下書きを見比べる",
            "左の「要修正」だけを直す",
            "警告のない問題は「まとめて確認」",
            "最後に「公開する」",
        ],
        20,
        172,
        76,
        size=7.6,
        leading=3.8,
        gap=1.4,
        dot_color=GREEN,
    )
    rounded_box(c, 108, 104, 88, 90, fill=BLUE_PALE, stroke=BLUE)
    draw_text(c, 114, 183, "答案を採点する", size=10, color=DARK)
    bullet_list(
        c,
        [
            "「答案受付を開始」で公開済みひな形を選択",
            "答案をまとめてアップロード",
            "氏名・解答の読み取りと採点は自動",
            "「採点待ち・確認」の件数だけ処理",
            "未解決がなくなった答案を確定",
        ],
        114,
        172,
        76,
        size=7.6,
        leading=3.8,
        gap=1.4,
        dot_color=BLUE,
    )
    callout(
        c,
        14,
        34,
        182,
        49,
        "迷ったら、この2つのボタン",
        "ひな形の内容が整ったら「公開する」。答案を受け取る準備ができたら「答案受付を開始」。途中のAI処理は画面を閉じても続きます。",
        tone="safe",
    )
    new_page(c)

    # 3. Source classifications
    section_header(c, "02  資料の種類", "4つから選ぶだけ", 3)
    paragraph(
        c,
        "AIが資料の種類を提案します。先生は提案が違う時だけ変更してください。とくに、記入済み答案を模範解答として扱わないことが重要です。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    exam_rect = place_image(
        c,
        FIXTURE_DIR / "rendered" / "blank.png",
        14,
        91,
        70,
        151,
        border=BLUE,
    )
    marker(
        c,
        1,
        exam_rect[0] + exam_rect[2] * 0.28,
        exam_rect[1] + exam_rect[3] * 0.71,
    )
    marker(
        c,
        2,
        exam_rect[0] + exam_rect[2] * 0.29,
        exam_rect[1] + exam_rect[3] * 0.48,
    )
    source_rows = [
        (
            "問題のみ（未記入）",
            "空欄の問題用紙です。AIが問題を読み、正解候補を作ります。",
            BLUE_PALE,
            BLUE,
        ),
        (
            "模範解答入り",
            "記入された答えを、かな・漢字も画像どおり正解として読み取ります。",
            MINT,
            GREEN,
        ),
        (
            "記入済み答案（AIが正答を作成）",
            "書かれた答えは正解に使わず、AIが問題を独立して解きます。",
            ORANGE_PALE,
            ORANGE,
        ),
        (
            "別紙の模範解答",
            "別紙に正解、配点、採点の考え方が載っている資料です。",
            WHITE,
            GREEN,
        ),
    ]
    for index, (title, body, fill, accent) in enumerate(source_rows):
        y = 210 - index * 38
        rounded_box(c, 92, y, 104, 32, fill=fill, stroke=accent)
        marker(c, index + 1, 99, y + 22)
        draw_text(c, 107, y + 22, title, size=8.5, color=DARK)
        paragraph(
            c,
            body,
            107,
            y + 13,
            82,
            size=7.1,
            leading=3.6,
            color=MUTED,
        )
    callout(
        c,
        14,
        28,
        182,
        47,
        "記入済み答案で迷った時",
        "生徒の答案、途中まで解いた用紙、正誤が保証されていない用紙は「記入済み答案（AIが正答を作成）」を選びます。書かれた内容は正解として使われません。",
        tone="warn",
    )
    new_page(c)

    # 4. Upload and classification
    section_header(c, "03  ひな形を作成", "資料をまとめて追加し、分類だけ確認", 4)
    paragraph(
        c,
        "「テストひな形を作成」でPDF・画像をまとめて追加します。アップロード後、資料の種類が自動で提案されます。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    role_rect = place_image(
        c,
        SCREEN_DIR / "34-template-role-review.png",
        14,
        132,
        182,
        111,
        border=GREEN,
    )
    marker(
        c,
        1,
        role_rect[0] + role_rect[2] * 0.62,
        role_rect[1] + role_rect[3] * 0.28,
    )
    marker(
        c,
        2,
        role_rect[0] + role_rect[2] * 0.73,
        role_rect[1] + role_rect[3] * 0.16,
    )
    label_chip(c, 1, "ファイルごとの「資料の種類」を確認", 14, 118, 88)
    label_chip(c, 2, "違う時だけ選び直す", 108, 118, 88)
    rounded_box(c, 14, 62, 182, 43, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 95, "画面に表示される4つの選択肢", size=9.2, color=DARK)
    draw_text(c, 20, 83, "問題のみ（未記入）", size=7.7, color=BLUE)
    draw_text(c, 75, 83, "模範解答入り", size=7.7, color=GREEN)
    draw_text(
        c,
        20,
        72,
        "記入済み答案（AIが正答を作成）",
        size=7.7,
        color=ORANGE,
    )
    draw_text(c, 115, 72, "別紙の模範解答", size=7.7, color=GREEN)
    callout(
        c,
        14,
        27,
        182,
        25,
        "入力を増やさない",
        "通常は題名や教科を手入力しません。自動提案が違う項目だけ直すと、そのままAI下書きへ進みます。",
        tone="safe",
    )
    new_page(c)

    # 5. Compare source and AI draft
    section_header(c, "04  AI下書きを確認", "元の資料と見比べ、要修正だけ直す", 5)
    paragraph(
        c,
        "画面は「問題一覧」「元の資料」「選択中の問題」の3つだけです。AIがページ全体と自動拡大を照合するため、先生が問題枠や座標を描く必要はありません。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    editor_rect = place_image(
        c,
        SCREEN_DIR / "32b-template-editor-ai-proposal-wide.png",
        14,
        116,
        182,
        128,
        border=GREEN,
    )
    marker(
        c,
        1,
        editor_rect[0] + editor_rect[2] * 0.18,
        editor_rect[1] + editor_rect[3] * 0.72,
    )
    marker(
        c,
        2,
        editor_rect[0] + editor_rect[2] * 0.51,
        editor_rect[1] + editor_rect[3] * 0.70,
    )
    marker(
        c,
        3,
        editor_rect[0] + editor_rect[2] * 0.86,
        editor_rect[1] + editor_rect[3] * 0.65,
    )
    marker(
        c,
        4,
        editor_rect[0] + editor_rect[2] * 0.92,
        editor_rect[1] + editor_rect[3] * 0.91,
    )
    label_chip(c, 1, "問題一覧", 14, 102, 40)
    label_chip(c, 2, "元の資料", 57, 102, 40)
    label_chip(c, 3, "選択中の問題", 100, 102, 49)
    label_chip(c, 4, "公開する", 152, 102, 44)
    rounded_box(c, 14, 51, 88, 38, fill=ORANGE_PALE, stroke=ORANGE)
    draw_text(c, 20, 79, "要修正だけ直す", size=9, color=DARK)
    bullet_list(
        c,
        [
            "左の「要修正」から順に開く",
            "右側で問題文・正解・配点を修正",
            "複数空欄や繰り返し番号も原本と照合",
        ],
        20,
        69,
        76,
        size=7.1,
        leading=3.6,
        gap=0.8,
        dot_color=ORANGE,
    )
    rounded_box(c, 108, 51, 88, 38, fill=MINT, stroke=GREEN)
    draw_text(c, 114, 79, "警告なしはまとめて確認", size=9, color=DARK)
    bullet_list(
        c,
        [
            "元の資料とAI下書きを見比べる",
            "警告のない問題は「まとめて確認」",
            "要修正の問題は対象外のまま残る",
        ],
        114,
        69,
        76,
        size=7.1,
        leading=3.6,
        gap=0.8,
        dot_color=GREEN,
    )
    callout(
        c,
        14,
        23,
        182,
        18,
        "公開",
        "内容が整ったら、画面右上の「公開する」を押します。",
        tone="safe",
    )
    new_page(c)

    # 6. Start receiving answers
    section_header(c, "05  答案受付", "「答案受付を開始」からまとめて追加", 6)
    paragraph(
        c,
        "ひな形を公開したら「テスト実施」を開き、「答案受付を開始」を押します。公開済みひな形、実施名、実施日を選ぶだけです。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    automation_step(
        c,
        1,
        "ひな形を選ぶ",
        "公開済みのひな形を選び、実施名と実施日を入力します。",
        14,
        211,
        56,
        tone="info",
    )
    automation_step(
        c,
        2,
        "受付を開始",
        "「答案受付を開始」で追加画面を開きます。",
        77,
        211,
        56,
        tone="safe",
    )
    automation_step(
        c,
        3,
        "答案をまとめて追加",
        "PDF・画像を一度に選び、その後はAIに任せます。",
        140,
        211,
        56,
        tone="safe",
    )
    upload_rect = place_image(
        c,
        SCREEN_DIR / "13-upload-board.png",
        14,
        89,
        182,
        108,
        crop=(250, 365, 1440, 1065),
        border=BLUE,
    )
    marker(
        c,
        1,
        upload_rect[0] + upload_rect[2] * 0.52,
        upload_rect[1] + upload_rect[3] * 0.70,
    )
    marker(
        c,
        2,
        upload_rect[0] + upload_rect[2] * 0.52,
        upload_rect[1] + upload_rect[3] * 0.16,
    )
    label_chip(c, 1, "答案をここにドロップ", 14, 75, 88)
    label_chip(c, 2, "処理状況は下に表示", 108, 75, 88)
    callout(
        c,
        14,
        28,
        182,
        34,
        "アップロード後は待たなくて大丈夫",
        "複数ファイルをまとめて選べます。画面を閉じても、送信済みの処理は続きます。同じ答案をもう一度追加しないでください。",
        tone="info",
    )
    new_page(c)

    # 7. Automatic grading
    section_header(c, "06  自動採点", "アップロード後はAIが進める", 7)
    paragraph(
        c,
        "答案を送信すると、氏名と解答の読み取り、採点、あいまいな結果の再確認まで自動で進みます。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    ai_pipeline(
        c,
        [
            "答案を受信",
            "氏名・番号を読む",
            "解答を読む",
            "採点する",
            "あいまいな結果を再確認",
            "例外だけ確認待ちへ",
        ],
        14,
        217,
        182,
    )
    status_rect = place_image(
        c,
        SCREEN_DIR / "13-upload-board.png",
        14,
        104,
        182,
        96,
        crop=(285, 690, 1410, 1065),
        border=GREEN,
    )
    marker(
        c,
        1,
        status_rect[0] + status_rect[2] * 0.73,
        status_rect[1] + status_rect[3] * 0.42,
    )
    label_chip(c, 1, "答案ごとの状態を自動更新", 14, 90, 182)
    rounded_box(c, 14, 39, 88, 38, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 67, "先生の操作がいらないもの", size=9, color=DARK)
    bullet_list(
        c,
        [
            "読み取りが明確な氏名・番号",
            "正解が明確な客観式",
            "採点結果が一致した答案",
        ],
        20,
        57,
        76,
        size=7.1,
        leading=3.6,
        gap=0.8,
        dot_color=GREEN,
    )
    rounded_box(c, 108, 39, 88, 38, fill=ORANGE_PALE, stroke=ORANGE)
    draw_text(c, 114, 67, "確認待ちに残るもの", size=9, color=DARK)
    bullet_list(
        c,
        [
            "氏名候補がない、または複数",
            "判読が難しい解答・部分点",
            "再確認でも判断が分かれる項目",
        ],
        114,
        57,
        76,
        size=7.1,
        leading=3.6,
        gap=0.8,
        dot_color=ORANGE,
    )
    new_page(c)

    # 8. Review only exceptions
    section_header(c, "07  採点待ち・確認", "確認待ちの件数だけ処理", 8)
    paragraph(
        c,
        "不確かな項目だけが「採点待ち・確認」に並びます。最初から全答案を見直す必要はありません。",
        14,
        257,
        182,
        size=8.8,
        leading=4.5,
        color=MUTED,
    )
    review_rect = place_image(
        c,
        SCREEN_DIR / "14-name-review.png",
        14,
        116,
        182,
        128,
        crop=(250, 65, 1440, 925),
        border=GREEN,
    )
    marker(
        c,
        1,
        review_rect[0] + review_rect[2] * 0.12,
        review_rect[1] + review_rect[3] * 0.83,
    )
    marker(
        c,
        2,
        review_rect[0] + review_rect[2] * 0.45,
        review_rect[1] + review_rect[3] * 0.83,
    )
    marker(
        c,
        3,
        review_rect[0] + review_rect[2] * 0.79,
        review_rect[1] + review_rect[3] * 0.83,
    )
    automation_step(
        c,
        1,
        "生徒名",
        "候補がある時は選択。ない時だけ名簿を検索します。",
        14,
        69,
        56,
        tone="info",
    )
    automation_step(
        c,
        2,
        "採点",
        "AI提案が不確かな問題だけを修正します。",
        77,
        69,
        56,
        tone="warn",
    )
    automation_step(
        c,
        3,
        "確定",
        "「この答案を確定」で完了します。",
        140,
        69,
        56,
        tone="safe",
    )
    callout(
        c,
        14,
        25,
        182,
        30,
        "見るのは数字が付いたタブだけ",
        "生徒名、採点、確定の各タブに表示される件数だけ処理します。判断内容は履歴に残ります。",
        tone="safe",
    )
    new_page(c)

    # 9. Quick reference
    section_header(c, "08  クイックリファレンス", "最小操作チェックリスト", 9)
    rounded_box(c, 14, 167, 88, 89, fill=MINT, stroke=GREEN)
    draw_text(c, 20, 245, "ひな形を作る先生", size=10, color=DARK)
    bullet_list(
        c,
        [
            "問題用紙と解答資料をまとめて追加",
            "資料の種類が違う時だけ変更",
            "問題枠・座標は描かない",
            "元の資料とAI下書きを見比べる",
            "「要修正」だけを直す",
            "警告なしは「まとめて確認」",
            "最後に「公開する」",
        ],
        20,
        234,
        76,
        size=7.5,
        leading=3.8,
        gap=1.3,
        dot_color=GREEN,
    )
    rounded_box(c, 108, 167, 88, 89, fill=BLUE_PALE, stroke=BLUE)
    draw_text(c, 114, 245, "答案を処理する先生", size=10, color=DARK)
    bullet_list(
        c,
        [
            "「答案受付を開始」で実施を作る",
            "答案をまとめてアップロード",
            "自動処理中は待たずに画面を閉じる",
            "採点待ち・確認の件数だけ処理",
            "未解決がない答案を確定",
        ],
        114,
        234,
        76,
        size=7.5,
        leading=3.8,
        gap=1.3,
        dot_color=BLUE,
    )
    callout(
        c,
        14,
        109,
        182,
        43,
        "分類で間違えやすい点",
        "学校の正式な正解が書かれた用紙だけが「模範解答入り」です。生徒の答案や正誤不明の用紙は「記入済み答案（AIが正答を作成）」を選びます。",
        tone="warn",
    )
    rounded_box(c, 14, 38, 182, 57, fill=WHITE, stroke=BORDER)
    draw_text(c, 20, 84, "やらないこと", size=9.5, color=DARK)
    bullet_list(
        c,
        [
            "自動提案が正しいのに任意項目をすべて手入力する",
            "「すべて」を上から順に開き、警告のない問題まで個別確認する",
            "処理中の同じ資料・答案を繰り返しアップロードする",
            "生徒答案を「模範解答入り」として公開する",
        ],
        20,
        73,
        168,
        size=7.5,
        leading=3.8,
        gap=1.2,
        dot_color=ORANGE,
    )
    c.save()
    print(OUTPUT)


if __name__ == "__main__":
    build_ai_first()
