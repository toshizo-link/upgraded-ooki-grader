from __future__ import annotations

from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas

import build_user_guide as ui


ROOT = Path(__file__).resolve().parents[3]
CAPTURE_DIR = ROOT / "output" / "playwright" / "manual-20260810"
LEGACY_SCREEN_DIR = Path(__file__).resolve().parent / "screens"
OUTPUT = ROOT / "output" / "pdf" / "ooki-grader-user-guide-ja.pdf"


def capture(name: str, fallback: str | None = None) -> Path:
    current = CAPTURE_DIR / name
    if current.exists():
        return current
    if fallback is not None:
        older = LEGACY_SCREEN_DIR / fallback
        if older.exists():
            return older
    raise FileNotFoundError(f"Manual screenshot is missing: {current}")


def page_header(c: canvas.Canvas, page: int, section: str, title: str) -> None:
    ui.section_header(c, section, title, page)


def screenshot_page(
    c: canvas.Canvas,
    *,
    page: int,
    section: str,
    title: str,
    intro: str,
    image: Path,
    steps: list[str],
    note_title: str,
    note: str,
    tone: str = "safe",
    image_crop: tuple[int, int, int, int] | None = None,
) -> None:
    page_header(c, page, section, title)
    ui.paragraph(c, intro, 14, 257, 182, size=8.3, leading=4.2, color=ui.MUTED)
    ui.place_image(
        c,
        image,
        14,
        99,
        182,
        144,
        crop=image_crop,
        border=ui.GREEN,
    )
    ui.rounded_box(c, 14, 25, 113, 64, fill=ui.WHITE, stroke=ui.BORDER)
    ui.draw_text(c, 20, 80, "画面で行うこと", size=9.2, color=ui.DARK)
    ui.bullet_list(
        c,
        steps,
        20,
        70,
        101,
        size=7.25,
        leading=3.65,
        gap=1.0,
        dot_color=ui.GREEN,
    )
    ui.callout(c, 132, 25, 64, 64, note_title, note, tone=tone)
    ui.new_page(c)


def two_screen_page(
    c: canvas.Canvas,
    *,
    page: int,
    section: str,
    title: str,
    intro: str,
    left_image: Path,
    left_title: str,
    left_body: str,
    right_image: Path,
    right_title: str,
    right_body: str,
    note_title: str,
    note: str,
    tone: str = "info",
    left_crop: tuple[int, int, int, int] | None = None,
    right_crop: tuple[int, int, int, int] | None = None,
) -> None:
    page_header(c, page, section, title)
    ui.paragraph(c, intro, 14, 257, 182, size=8.3, leading=4.2, color=ui.MUTED)
    ui.place_image(
        c,
        left_image,
        14,
        130,
        87,
        110,
        crop=left_crop,
        border=ui.GREEN,
    )
    ui.place_image(
        c,
        right_image,
        109,
        130,
        87,
        110,
        crop=right_crop,
        border=ui.BLUE,
    )
    ui.rounded_box(c, 14, 78, 87, 43, fill=ui.MINT, stroke=ui.GREEN)
    ui.draw_text(c, 20, 111, left_title, size=8.8, color=ui.DARK)
    ui.paragraph(c, left_body, 20, 103, 75, size=7.15, leading=3.65, color=ui.INK)
    ui.rounded_box(c, 109, 78, 87, 43, fill=ui.BLUE_PALE, stroke=ui.BLUE)
    ui.draw_text(c, 115, 111, right_title, size=8.8, color=ui.DARK)
    ui.paragraph(c, right_body, 115, 103, 75, size=7.15, leading=3.65, color=ui.INK)
    ui.callout(c, 14, 25, 182, 42, note_title, note, tone=tone)
    ui.new_page(c)


def build_detailed_user_guide() -> None:
    ui.register_font()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    ui.PROCESSED_DIR.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(OUTPUT), pagesize=A4, pageCompression=1)
    c.setTitle("Ooki Grader 先生向けユーザーガイド")
    c.setAuthor("Ooki Grader")
    c.setSubject("実画面で学ぶ、ひな形作成から答案確定・帳票出力までの操作手順")
    c.setKeywords(
        "Ooki Grader, 先生, ひな形, 順番取り込み, 氏名確認, AI採点, 一括出力, 操作マニュアル"
    )

    # 1 — cover
    c.setFillColor(ui.PALE)
    c.rect(0, 0, ui.PAGE_W, ui.PAGE_H, fill=1, stroke=0)
    c.setFillColor(ui.DARK)
    c.rect(0, 0, ui.mm(10), ui.PAGE_H, fill=1, stroke=0)
    c.setFillColor(ui.ORANGE)
    c.circle(ui.mm(29), ui.mm(271), ui.mm(11), fill=1, stroke=0)
    c.setFillColor(ui.WHITE)
    c.setFont(ui.FONT, 19)
    c.drawCentredString(ui.mm(29), ui.mm(264.5), "大")
    ui.draw_text(c, 47, 274, "OOKI GRADER", size=8, color=ui.GREEN)
    ui.draw_text(c, 18, 248, "先生向け", size=28, color=ui.DARK)
    ui.draw_text(c, 18, 234, "ユーザーガイド", size=28, color=ui.DARK)
    ui.draw_text(
        c,
        18,
        221,
        "ひな形作成・順番取り込み・氏名確認・採点・確定・帳票出力",
        size=10.4,
        color=ui.MUTED,
    )
    ui.place_image(c, capture("22-ordered-scan-completed.png", "38-ordered-scan-step.png"), 16, 82, 178, 126, border=ui.GREEN)
    ui.callout(
        c,
        18,
        37,
        176,
        32,
        "この冊子の前提",
        "画面はすべて架空の生徒・試験を使った実動作です。答案は1ページPDFで、スキャナーの順番が保たれる運用を説明します。",
        tone="safe",
    )
    ui.draw_text(c, 18, 24, "詳細版 / 2026年8月11日 / 第1版", size=7.6, color=ui.MUTED)
    ui.footer(c, 1)
    ui.new_page(c)

    # 2 — complete workflow
    page_header(c, 2, "00  はじめに", "先生が行う仕事と全体の流れ")
    ui.paragraph(
        c,
        "通常の操作は、次の7段階です。Geminiの接続と4機能の準備は管理者が一度だけ行い、先生はAPIキーやAI機能の承認・有効化を扱いません。AIは下書きと候補を作りますが、氏名・採点・受付開始・確定は画面の状態を見て先生が判断します。",
        14,
        257,
        182,
        size=8.6,
        leading=4.4,
        color=ui.MUTED,
    )
    ui.flow(c, 14, 227, 182)
    ui.place_image(c, capture("02-dashboard.png", "03-dashboard.png"), 14, 91, 182, 123, border=ui.GREEN)
    ui.rounded_box(c, 14, 25, 87, 54, fill=ui.MINT, stroke=ui.GREEN)
    ui.draw_text(c, 20, 68, "先生が必ず確認すること", size=8.8, color=ui.DARK)
    ui.bullet_list(
        c,
        ["正しいひな形・実施を選んだか", "スキャン順と答案区切りが正しいか", "氏名候補と採点の警告が解決したか"],
        20,
        58,
        75,
        size=7.2,
        leading=3.6,
        gap=1.0,
        dot_color=ui.GREEN,
    )
    ui.callout(
        c,
        109,
        25,
        87,
        54,
        "個人情報",
        "本番では学校から指定されたPCだけを使います。共有PCを離れるときは必ずログアウトし、PDFをダウンロードフォルダーに残しません。",
        tone="warn",
    )
    ui.new_page(c)

    screenshot_page(
        c,
        page=3,
        section="01  サインイン",
        title="学校内URLを開いてログイン",
        intro="管理者から案内されたショートカットを開きます。ブラウザーに証明書警告が出る場合は先へ進まず、管理者へ連絡してください。",
        image=capture("01-login.png", "02-login.png"),
        steps=[
            "ユーザー名とパスワードを入力する",
            "「サインイン」を1回押し、画面が切り替わるまで待つ",
            "作業後は右上のメニューからログアウトする",
        ],
        note_title="入れないとき",
        note="URL・時刻・LAN接続を確認します。何度も試さず、表示されたエラーを管理者へ伝えます。",
        tone="warn",
    )

    screenshot_page(
        c,
        page=4,
        section="02  ダッシュボード",
        title="今日の未処理件数から始める",
        intro="最初の画面には、氏名確認・採点確認・確定待ちなど、先生の判断が必要な件数が表示されます。",
        image=capture("02-dashboard.png", "03-dashboard.png"),
        steps=[
            "件数のあるカードを選び、該当キューを開く",
            "上部メニューから生徒・ひな形・実施・結果へ移動する",
            "処理中の件数は自動更新を待ち、連打や再アップロードをしない",
        ],
        note_title="優先順位",
        note="先に氏名確認、次に採点確認、最後に確定待ちを処理すると、同じ答案を何度も開かずに済みます。",
    )

    screenshot_page(
        c,
        page=5,
        section="03  生徒",
        title="生徒を1人ずつ登録する",
        intro="氏名照合の候補は生徒名簿から作られます。姓・名・学年・所属を確認し、同姓同名には識別しやすい情報を入れます。",
        image=capture("04-student-add-filled.png", "05c-student-add.png"),
        steps=[
            "「生徒を追加」を開く",
            "必須の生徒番号と画面表示名を入力する",
            "姓・名・ふりがな・学年・所属を入力する",
            "保存後に生徒詳細を開き、誤字がないか確認する",
        ],
        note_title="名簿更新",
        note="退塾した生徒は履歴を消さず無効化します。復帰時は再有効化できます。大量登録はCSVを使います。",
    )

    two_screen_page(
        c,
        page=6,
        section="03  生徒",
        title="一覧で検索し、詳細で履歴を守る",
        intro="一覧では氏名・カナ・番号・別名を検索し、在籍・クラス・コース・学年を組み合わせて絞り込みます。生徒を完全削除せず、有効／無効の状態で管理します。",
        left_image=capture("46-students-filter-sort.png", "04b-students-desktop.png"),
        left_title="1  一覧を検索",
        left_body="適用中の条件をチップで確認し、氏名・番号・更新日時の並び順と25〜200件の表示件数を選びます。",
        right_image=capture("05-student-detail.png", "05-student-add.png"),
        right_title="2  詳細を確認",
        right_body="氏名、学年、所属、別名を確認します。過去結果がある生徒は履歴を残します。",
        note_title="CSVを使う場合",
        note="最初は少人数のファイルで文字コードと列を確認し、プレビューの追加・更新・エラー件数を確認してから確定します。",
    )

    screenshot_page(
        c,
        page=7,
        section="04  ひな形",
        title="試験タイプを先に選ぶ",
        intro="PDFの内容から試験タイプを推測させません。「新しいひな形」を開き、種類・教科・必要な設定を先に選択します。",
        image=capture("09-template-create-settings-hop.png", "35-template-settings-first.png"),
        steps=[
            "HOP・STEP・クラス分け・その他のいずれかを選ぶ",
            "教科を選び、「その他」では通常／穴埋めも選ぶ",
            "設定内容を読み直してから試験PDFを追加する",
        ],
        note_title="混ぜない",
        note="1回の作成で追加するPDFは1件です。種類や学年の違う試験を同じPDFにまとめないでください。",
        tone="warn",
    )

    # 8 — type matrix
    page_header(c, 8, "04  ひな形", "4種類の分割と答案ページ数")
    ui.paragraph(
        c,
        "ひな形作成時の分割方法と、採点時に生徒1人分としてまとめるページ数は別の概念です。確定済み版の答案ページ数が順番取り込みの区切りになります。",
        14,
        257,
        182,
        size=8.5,
        leading=4.3,
        color=ui.MUTED,
    )
    rows = [
        ("HOP", "元PDFを1ページずつ別ひな形へ分割", "1ページ", "各PDFが1答案"),
        ("STEP", "6ページ単位を -1 / -2 / -3 に分割", "2ページ", "1,2 / 3,4 … の順"),
        ("クラス分け", "元PDF全体を1つのひな形として登録", "1〜50ページ", "確定済み版のN枚で1組"),
        ("その他", "元PDF全体を1つのひな形として登録", "1〜50ページ", "3・4ページ以上も同じ仕組み"),
    ]
    x_positions = [14, 43, 106, 143, 196]
    top = 225
    row_h = 31
    for index in range(4):
        ui.rounded_box(
            c,
            x_positions[index],
            top,
            x_positions[index + 1] - x_positions[index],
            14,
            fill=ui.DARK,
            stroke=ui.DARK,
            radius=0,
        )
    headers = ["種類", "ひな形作成", "答案", "取り込み"]
    header_x = [18, 47, 110, 147]
    for text, x in zip(headers, header_x, strict=True):
        ui.draw_text(c, x, top + 4.5, text, size=7.4, color=ui.WHITE)
    for idx, row in enumerate(rows):
        y = top - (idx + 1) * row_h
        ui.rounded_box(c, 14, y, 182, row_h, fill=ui.MINT if idx % 2 == 0 else ui.WHITE, stroke=ui.BORDER, radius=0)
        ui.draw_text(c, 18, y + 18, row[0], size=8.0, color=ui.DARK)
        ui.paragraph(c, row[1], 47, y + 22, 55, size=6.9, leading=3.5, color=ui.INK)
        ui.paragraph(c, row[2], 110, y + 22, 29, size=6.9, leading=3.5, color=ui.INK)
        ui.paragraph(c, row[3], 147, y + 22, 45, size=6.9, leading=3.5, color=ui.INK)
    ui.place_image(c, capture("10-template-upload-plan.png", "36-template-step-plan.png"), 14, 35, 111, 54, border=ui.GREEN)
    ui.callout(
        c,
        132,
        35,
        64,
        54,
        "重要",
        "STEPの登録単位は -1、-2、-3 の各2ページです。採点時に6ページを1答案へまとめません。",
        tone="warn",
    )
    ui.new_page(c)

    screenshot_page(
        c,
        page=9,
        section="04  ひな形",
        title="固定された作成予定を確認する",
        intro="PDFを追加すると、種類に応じてページ範囲と作成予定が表示されます。ここで件数・範囲・STEP番号を確認してから生成を始めます。",
        image=capture("10-template-upload-plan.png", "36-template-step-plan.png"),
        steps=[
            "元ファイル名・ページ数・作成予定件数を確認する",
            "HOPは1ページ単位、STEPは2ページ単位になっているか確認する",
            "STEPでは各セットに -1 / -2 / -3 が揃っているか確認する",
        ],
        note_title="直せない場合",
        note="分割予定そのものは手動変更しません。種類の選択や元PDFが誤っている場合は、この作成を取り消してやり直します。",
        tone="warn",
    )

    screenshot_page(
        c,
        page=10,
        section="04  ひな形",
        title="学年を確定し、自動名称を確認",
        intro="まずファイル名・紙面の証拠から学年を確定します。既知の種類は教科・学年・分割番号から名称が自動作成され、AIが読んだ紙面名は参照用にだけ表示されます。",
        image=capture("40-template-deterministic-names.png"),
        steps=[
            "最初に学年の欠落・不一致を解決する",
            "HOP: 教科＋学年＋「年HOP」＋連番",
            "STEP: 教科＋学年＋「年STEPセット」＋セット番号-枝番",
            "クラス分け: 教科＋学年＋「年クラス分けテスト」",
            "すべての必須確認が終わったら、ひな形を作成する",
        ],
        note_title="編集できる名称",
        note="「その他」だけはAIが読んだ紙面名を候補として名称を編集できます。HOP・STEP・クラス分けの名称は変更できません。",
        tone="safe",
        image_crop=(250, 65, 1410, 1030),
    )

    page_header(c, 11, "05  採点ルール", "AI判定を基本に、必要な問題だけ変更")
    ui.paragraph(
        c,
        "新しい問題と、先生が問題形式を変更した問題は「AIで判定（おすすめ）」が基本です。部分点の単位は1点、「採点後に必ず先生が確認する」はオフで始まります。",
        14,
        257,
        182,
        size=8.3,
        leading=4.2,
        color=ui.MUTED,
    )
    ui.place_image(
        c,
        capture("49-template-ai-defaults.png"),
        14,
        110,
        58,
        126,
        crop=(1415, 330, 1744, 1027),
        border=ui.GREEN,
    )
    ui.place_image(
        c,
        capture("50-template-ai-defaults-details.png"),
        76,
        110,
        58,
        126,
        crop=(1415, 205, 1744, 920),
        border=ui.BLUE,
    )
    ui.place_image(
        c,
        capture("51-template-review-default-off.png"),
        138,
        110,
        58,
        126,
        crop=(1415, 340, 1744, 1027),
        border=ui.ORANGE,
    )
    ui.draw_text(c, 22, 101, "AIで判定（おすすめ）", size=7.4, color=ui.DARK)
    ui.draw_text(c, 84, 101, "部分点の単位：1点", size=7.4, color=ui.DARK)
    ui.draw_text(c, 144, 101, "必ず先生確認：オフ", size=7.4, color=ui.DARK)
    ui.rounded_box(c, 14, 25, 113, 62, fill=ui.WHITE, stroke=ui.BORDER)
    ui.draw_text(c, 20, 77, "必要な問題だけ変更", size=9.0, color=ui.DARK)
    ui.bullet_list(
        c,
        [
            "通常は「AIで判定」のまま使い、正解と採点基準を照合する",
            "必要時だけ完全一致・数値・選択式・先生が採点へ変更する",
            "完答・順不同・漢字必須は「詳細設定」で個別に指定する",
            "常時確認したい問題だけ、先生確認をオンにする",
        ],
        20,
        67,
        101,
        size=7.1,
        leading=3.55,
        gap=0.8,
        dot_color=ui.GREEN,
    )
    ui.callout(
        c,
        132,
        25,
        64,
        62,
        "既存設定は保持",
        "読み込み・コピー・取込済みの問題に明示された採点方法は勝手に置き換えません。AI判定も曖昧・部分点・別解を自動確定せず、採点確認へ回します。",
        tone="warn",
    )
    ui.new_page(c)

    screenshot_page(
        c,
        page=12,
        section="05  採点ルール",
        title="すべての問題を確認して受付開始",
        intro="原本と下書きを照合したら、画面上部の「すべての問題を確認」で入力が揃った問題をまとめて確認済みにします。確認だけでは受付は始まりません。",
        image=capture("50-template-ai-defaults-details.png"),
        steps=[
            "問題文・正解・配点・採点基準を原本と照合する",
            "「すべての問題を確認」を1回押す",
            "確認済み件数と、残った問題の理由を読む",
            "不足項目を直して再確認し、残件ゼロなら受付を開始する",
        ],
        note_title="残る問題",
        note="問題文・必須正解・採点基準などが不足する問題は確認済みになりません。先頭の未確認理由へ移動して直します。受付開始エラーが全体条件なら、問題の未確認と取り違えず表示文を管理者へ伝えます。",
        tone="warn",
        image_crop=(250, 205, 1744, 1027),
    )

    two_screen_page(
        c,
        page=13,
        section="05  ひな形整理",
        title="削除ではなくアーカイブして復元",
        intro="使わないひな形はアーカイブします。既存の受付・答案・結果・版履歴は残るため、監査や過去結果の表示を壊しません。",
        left_image=capture("14-template-archive-confirmation.png", "07-templates.png"),
        left_title="1  アーカイブ",
        left_body="確認文を読み、一覧から隠します。AI下書きの生成中は、処理の完了または失敗を待ちます。",
        right_image=capture("16-template-restore-confirmation.png", "07b-templates-desktop.png"),
        right_title="2  復元",
        right_body="アーカイブ一覧から復元します。確定済み版があれば利用中、なければ下書きに戻ります。",
        note_title="消えないもの",
        note="問題、版、使用済み答案、確定結果、監査履歴は削除されません。誤って整理しても復元できます。",
        tone="safe",
    )

    screenshot_page(
        c,
        page=14,
        section="06  テスト実施",
        title="確認済みひな形で受付を開始",
        intro="ひな形の試験名・教科・学年・カテゴリ・コースは固定表示されます。実施日と、必要な場合だけ対象クラスを入力すると、そのまま受付中の画面へ移ります。",
        image=capture("18-session-create-filled.png", "12b-session-create-dialog.png"),
        steps=[
            "ひな形の試験名・教科・学年・カテゴリ・コースを確認する",
            "実施日と、必要な場合だけ対象クラスを入力する",
            "「受付を開始」を押す。下書きなら版も同時に確定される",
        ],
        note_title="選択ミス",
        note="違うひな形へ答案を送ると自動で直せません。答案送信前なら誤った受付を終了し、正しいひな形から新しい受付を作ります。送信後は管理者へ相談します。",
        tone="warn",
    )

    screenshot_page(
        c,
        page=15,
        section="07  順番取り込み",
        title="1ページPDFをスキャン順に追加",
        intro="Ooki Graderは1ページPDFをファイル名の自然順で最初に並べ、先生が確認した順番を固定します。並列アップロードの完了順では組み替えません。",
        image=capture("19-session-ordered-scan-empty.png", "13-upload-board.png"),
        steps=[
            "生徒Aの1ページ目、2ページ目…を連続してスキャンする",
            "次に生徒Bを同じページ順でスキャンする",
            "一括選択後、ファイル名順になった画面をスキャナー順と照合する",
            "違う行は左右矢印で移動し、送信前に答案区切りを確認する",
        ],
        note_title="氏名欄",
        note="順番が守られる前提では、氏名は各答案の1ページ目だけで構いません。後続ページの本人性は順番に依存します。",
        tone="warn",
    )

    screenshot_page(
        c,
        page=16,
        section="07  順番取り込み",
        title="答案区切りをプレビューする",
        intro="追加したファイルは、HOPなら1枚、STEPなら2枚、クラス分け／その他なら確定済み版のページ数N枚ごとに枠で表示されます。",
        image=LEGACY_SCREEN_DIR / "38-ordered-scan-step.png",
        steps=[
            "各枠が1人分になっているか確認する",
            "各枠の中が1ページ目、2ページ目…の順か確認する",
            "その他の3・4ページ以上も、同じ方法でN枚ずつ確認する",
            "「この順番でページを送信」の前だけ、移動・削除・追加で直す",
        ],
        note_title="枚数の端数",
        note="総枚数がNで割り切れない場合は確定できません。抜け・余分・二重選択を確認し、足りないPDFを追加します。",
        tone="warn",
    )

    two_screen_page(
        c,
        page=17,
        section="07  順番取り込み",
        title="全ページ受信後に組み立てを確定",
        intro="アップロード中は画面を閉じずに待ちます。全項目が受信済みになったら順番を再確認し、組み立てを確定します。",
        left_image=capture("21-ordered-scan-pages-received.png", "13-upload-board.png"),
        left_title="1  受信を確認",
        left_body="すべての行が受信済みで、エラーや未送信がないことを確認します。",
        right_image=capture("22-ordered-scan-completed.png", "38-ordered-scan-step.png"),
        right_title="2  組み立て完了",
        right_body="作成された答案数と警告を確認します。以後は氏名確認・採点処理へ進みます。",
        note_title="再送の注意",
        note="失敗した行だけを再試行します。新しい取り込みを重ねると重複候補になるため、同じ一式を最初から送らないでください。",
        tone="warn",
    )

    # 18 — recovery
    page_header(c, 18, "07  順番取り込み", "順番が崩れたときの直し方")
    ui.paragraph(
        c,
        "Ooki Graderはページの見た目をひな形と照合して、明らかなページ順違い・重複・別試験・不足を止めます。ただし同じ形式の別生徒ページを見分けることはできません。",
        14,
        257,
        182,
        size=8.3,
        leading=4.2,
        color=ui.MUTED,
    )
    ui.place_image(c, capture("20-ordered-scan-grouping.png", "38-ordered-scan-step.png"), 14, 122, 182, 121, border=ui.ORANGE)
    cases = [
        ("送信前の不足", "「ページを追加」で不足PDFを選び、左右矢印で正しい位置へ移動します。"),
        ("送信前の余分", "元PDFと照合し、×で余分な行だけ一覧から外します。"),
        ("受信だけ失敗", "固定した順番は変えず、「失敗したページを再送」を使います。"),
        ("needsReview／失敗", "「取り消して次のバッチを追加」で破棄し、元PDFから正しい順で作り直します。"),
    ]
    y = 115
    for index, (title, body) in enumerate(cases, start=1):
        fill = ui.MINT if index % 2 else ui.BLUE_PALE
        stroke = ui.GREEN if index % 2 else ui.BLUE
        ui.rounded_box(c, 14, y - 18, 182, 16, fill=fill, stroke=stroke)
        ui.marker(c, index, 21, y - 10)
        ui.draw_text(c, 29, y - 8, title, size=8.0, color=ui.DARK)
        ui.paragraph(c, body, 61, y - 7, 128, size=7.1, leading=3.5, color=ui.INK)
        y -= 20
    ui.callout(c, 14, 16, 182, 16, "処理中・完了後は取り消し不可", "処理中は完了を待ちます。完了後に作成済み答案の誤りへ気づいた場合は、管理者へ訂正を依頼します。", tone="warn")
    ui.new_page(c)

    two_screen_page(
        c,
        page=19,
        section="08  氏名確認",
        title="1ページ目の氏名候補を名簿へ割り当て",
        intro="通常は最初のAI採点と同じ送信で、答案の論理1ページ目にある氏名欄も読みます。名簿はAIへ送られず、候補は自動確定されません。先生が所属や原稿も見て決定します。",
        left_image=capture("23-name-review-unassigned.png", "14-name-review.png"),
        left_title="1  未割り当て",
        left_body="転記文字、候補、所属、画像を見比べます。読めない場合は未特定のまま扱えます。",
        right_image=capture("24-name-review-student-selected.png", "14-name-review.png"),
        right_title="2  生徒を選択",
        right_body="同姓同名や似た氏名に注意し、正しい生徒を選んで確認します。",
        note_title="名前だけを信じない",
        note="所属・学年・試験日・原稿も確認します。候補が違う場合は検索し、無理に近い名前へ割り当てません。",
        tone="warn",
    )

    screenshot_page(
        c,
        page=20,
        section="09  採点確認",
        title="1人分のPDFと全問題を同じ画面で確認",
        intro="「この答案をまとめて確認」を開くと、STEPの2ページを含む答案全体のPDF、全問題、読取結果、判定、点数、理由を1画面で確認できます。問題を選ぶと根拠ページへ移動します。",
        image=capture("52-submission-grading-workspace.png", "16-grading-partial.png"),
        steps=[
            "左のPDFまたはページ画像で答案全体を確認する",
            "右の問題を選び、正解・読み取り・判定・点数を見比べる",
            "誤りだけ修正し、理由を選んで「この採点を保存・確認」を押す",
        ],
        note_title="複数ページも1答案",
        note="順番取り込みで組み立てたSTEPやその他の多ページ答案は、1件のPDFとして表示されます。PDFが表示されないときは「ページ画像」へ切り替えます。保存期間終了後は画像を表示できませんが、採点履歴は残ります。",
        tone="warn",
        image_crop=(0, 0, 1744, 1027),
    )

    screenshot_page(
        c,
        page=21,
        section="09  採点確認",
        title="修正と一括確認を使い分ける",
        intro="誤りがある問題は個別に修正します。表示中の答案で未確認の全問題が妥当なら、点数や読み取りを変えず一括で確認済みにできます。",
        image=capture("53-submission-grading-bulk-confirm.png", "16-grading-partial.png"),
        steps=[
            "個別修正では判定、点数、読取結果、理由を確認して保存する",
            "全部正しければ「未確認N問を一括確認」を押し、件数を確認する",
            "確認済みのチェックを入れて保存する。これは答案の確定ではない",
            "別の先生の変更が表示されたら再読込し、最新内容を見直す",
        ],
        note_title="推測で一括確認しない",
        note="一括確認は、表示された全問題を先生が実際に確認した場合だけ使います。曖昧、判読困難、別解、ページ欠けがあれば個別に残し、最後の答案確定とは分けて扱います。",
        tone="warn",
    )

    two_screen_page(
        c,
        page=22,
        section="10  確定",
        title="未解決ゼロを確認して答案を確定",
        intro="氏名と全設問の確認が終わり、未解決警告がなくなった答案だけを確定します。確定後の変更は再開操作と監査理由が必要です。",
        left_image=capture("33-finalize-queue.png", "17-finalize-queue.png"),
        left_title="1  確定待ち",
        left_body="生徒、得点、未解決件数、重複状態を確認し、対象答案を開きます。",
        right_image=capture("34-finalize-confirmation.png", "18-finalize-confirm.png"),
        right_title="2  最終確認",
        right_body="確認ダイアログを読み、氏名・得点が正しい場合だけ確定します。",
        note_title="確定後の訂正",
        note="権限のある先生が理由付きで再開し、修正後に再確定します。古い状態と操作履歴は残ります。",
        tone="warn",
    )

    screenshot_page(
        c,
        page=23,
        section="11  結果",
        title="検索・絞り込み・並び替えを組み合わせる",
        intro="帳票には確定済み答案だけが表示されます。検索語と完全一致の条件を組み合わせ、現在の絞り込みと件数を確認してから結果を開きます。",
        image=capture("40-reports-filter-sort.png"),
        steps=[
            "生徒名・番号・テスト名を、空白区切りの複数語で検索する",
            "生徒・ひな形・教科・カテゴリ・コース・クラス・日付で絞る",
            "実施日・確定日時・生徒名・テスト名と昇順／降順を選ぶ",
            "条件チップ、約件数、25〜200件の表示件数を確認する",
        ],
        note_title="条件が分からないとき",
        note="候補にない値も入力できます。0件なら条件を1つずつ解除します。URLを再読込しても検索条件は保たれます。",
    )

    two_screen_page(
        c,
        page=24,
        section="11  結果",
        title="行を選ぶか、絞り込み全件を選ぶ",
        intro="一括出力には2つの対象指定があります。選択中の件数と、現在の絞り込みを見直してから使い分けます。",
        left_image=capture("41-reports-selected.png"),
        left_title="1  選択した結果",
        left_body="チェックした確定結果だけを出力します。ページをまたぐ未選択行は含みません。",
        right_image=capture("45-reports-filtered-preview.png"),
        right_title="2  絞り込み結果",
        right_body="今の条件に一致する全ページを対象にします。画面に見える1ページだけではありません。",
        note_title="出力前プレビュー",
        note="ホストが対象を再計算し、生徒数と結果件数を表示します。意図と違う場合は開始せず、閉じて条件を直します。",
        tone="info",
    )

    two_screen_page(
        c,
        page=25,
        section="11  結果",
        title="件数を確認し、検証済みZIPを受け取る",
        intro="一括出力はバックグラウンドで行われます。最大100名・500件までを、生徒ごとの詳細結果PDFとUTF-8の一覧CSV（manifest.csv）にまとめます。",
        left_image=capture("42-reports-bulk-preview.png"),
        left_title="1  件数を確認",
        left_body="生徒数・確定結果数と対象説明を読み、正しい場合だけ確認欄にチェックします。",
        right_image=capture("44-reports-bulk-ready.png"),
        right_title="2  完了後に保存",
        right_body="作成済み・完了件数を確認し、検証済みZIPだけをダウンロードします。",
        note_title="再読込と更新",
        note="処理状況は再読込後も戻せます。結果が途中で更新された場合は古いZIPを使わず、新しいプレビューから作り直します。",
        tone="warn",
    )

    two_screen_page(
        c,
        page=26,
        section="11  結果",
        title="1件の詳細と日本語PDFを確認する",
        intro="1人・1テストだけを渡す場合は結果詳細を開き、従来の日本語PDFを生成します。一括ZIP内の各PDFも同じ確定結果から作られます。",
        left_image=capture("36-result-detail.png", "20-result-detail.png"),
        left_title="1  結果詳細",
        left_body="得点率、設問別得点、先生の修正、確定日時を確認します。",
        right_image=capture("38-result-pdf-ready.png", "22-result-pdf-ready.png"),
        right_title="2  PDF完成",
        right_body="氏名、試験名、得点、文字化け、ページ欠けがないか確認してから印刷します。",
        note_title="個人情報と保存先",
        note="PDF／ZIPを共有端末へ残しません。印刷・受渡し後に不要なファイルを削除し、外部メールへ添付しません。",
        tone="warn",
    )

    page_header(c, 27, "12  実施終了", "受付を終了し、条件が揃ったらアーカイブ")
    ui.paragraph(
        c,
        "アーカイブは、答案受付を終了し、すべての答案・順番取り込み・採点ジョブが完了した実施を読み取り専用へ整理する操作です。",
        14,
        257,
        182,
        size=8.5,
        leading=4.3,
        color=ui.MUTED,
    )
    archive_checks = [
        ("1  受付を終了", "実施詳細で受付を終了します。受付中のままではアーカイブできません。"),
        ("2  取込を完了", "アップロード中、重複確認、未完了の順番取り込みをすべて解決します。"),
        ("3  答案を完了", "実答案は確定し、原稿でない物は氏名確認で「生徒の答案ではない」にします。"),
        ("4  ジョブを完了", "待機中・実行中・再試行待ちの採点処理がゼロになるまで待ちます。"),
        ("5  アーカイブ", "確認ダイアログを読み、エラーがなければアーカイブします。"),
    ]
    y = 229
    for index, (title, body) in enumerate(archive_checks):
        fill = ui.MINT if index % 2 == 0 else ui.BLUE_PALE
        stroke = ui.GREEN if index % 2 == 0 else ui.BLUE
        ui.rounded_box(c, 14, y - 31, 182, 27, fill=fill, stroke=stroke)
        ui.draw_text(c, 21, y - 14, title, size=8.7, color=ui.DARK)
        ui.paragraph(c, body, 60, y - 12, 128, size=7.2, leading=3.6, color=ui.INK)
        y -= 35
    ui.callout(
        c,
        14,
        25,
        182,
        27,
        "アーカイブ後は読み取り専用",
        "過去結果は閲覧できますが、氏名・採点・確定などの変更はできません。409エラー時は、表示された未完了項目を先に解決します。",
        tone="warn",
    )
    ui.new_page(c)

    # 28 — compact reference
    page_header(c, 28, "13  困ったとき", "操作を止める基準と連絡メモ")
    ui.paragraph(
        c,
        "迷ったときは推測で確定せず、画面を閉じる前に対象ID・試験名・実施日・表示メッセージを控えます。元PDFは問題が解決するまで保存してください。",
        14,
        257,
        182,
        size=8.5,
        leading=4.3,
        color=ui.MUTED,
    )
    items = [
        ("証明書／接続警告", "先へ進まない。LAN接続、PC時刻、URLを確認して管理者へ連絡。"),
        ("アップロード失敗", "失敗した行だけ再試行。同じ一式を新規バッチで重ねて送らない。"),
        ("順番・本人が不明", "組み立てを確定しない。スキャン原稿と元PDFで並びを確認。"),
        ("氏名が読めない", "無理に候補へ割り当てず未特定にする。原稿を担当先生へ回す。"),
        ("採点基準が不明", "確認待ちのままにし、ひな形責任者へ確認。推測で点数を入れない。"),
        ("処理／一括出力", "長い時は連打せず更新。失敗時は対象更新・上限・未割当を確認して再プレビュー。"),
    ]
    y = 229
    for index, (title, body) in enumerate(items, start=1):
        fill = ui.MINT if index % 2 else ui.BLUE_PALE
        stroke = ui.GREEN if index % 2 else ui.BLUE
        ui.rounded_box(c, 14, y - 26, 182, 22, fill=fill, stroke=stroke)
        ui.marker(c, index, 22, y - 15)
        ui.draw_text(c, 30, y - 11, title, size=8.3, color=ui.DARK)
        ui.paragraph(c, body, 30, y - 18, 158, size=7.2, leading=3.6, color=ui.INK)
        y -= 30
    ui.callout(
        c,
        14,
        25,
        182,
        22,
        "管理者へ伝える4点",
        "①発生時刻　②実施／生徒／答案　③表示されたエラー文　④直前に行った操作。生徒答案そのものを外部メールへ添付しません。",
        tone="safe",
    )
    c.save()
    print(OUTPUT)


if __name__ == "__main__":
    build_detailed_user_guide()
