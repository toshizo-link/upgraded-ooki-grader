from __future__ import annotations

import sys
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas


ROOT = Path(__file__).resolve().parents[3]
USER_GUIDE_DIR = ROOT / "tmp" / "pdfs" / "user-guide"
sys.path.insert(0, str(USER_GUIDE_DIR))

import build_user_guide as ui  # noqa: E402


OUTPUT = ROOT / "output" / "pdf" / "ooki-grader-host-operations-guide-ja.pdf"
SCREEN_DIR = Path(__file__).resolve().parent / "screens"
PAGE_W, PAGE_H = A4


def footer(c: canvas.Canvas, page: int) -> None:
    c.setStrokeColor(ui.BORDER)
    c.setLineWidth(0.5)
    c.line(ui.mm(14), ui.mm(12.5), ui.mm(196), ui.mm(12.5))
    ui.draw_text(
        c,
        14,
        7,
        "Ooki Grader  ホスト・アプリ セットアップ／運用ガイド",
        size=6.7,
        color=ui.MUTED,
    )
    ui.draw_right(c, 196, 7, str(page), size=7, color=ui.MUTED)


def page_header(c: canvas.Canvas, section: str, title: str, page: int) -> None:
    c.setFillColor(ui.PALE)
    c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    ui.draw_text(c, 14, 282.5, section, size=8.3, color=ui.GREEN)
    ui.draw_text(c, 14, 271.5, title, size=19, color=ui.DARK)
    c.setStrokeColor(ui.BORDER)
    c.setLineWidth(0.8)
    c.line(ui.mm(14), ui.mm(265.5), ui.mm(196), ui.mm(265.5))
    footer(c, page)


def step_box(
    c: canvas.Canvas,
    number: int,
    title: str,
    body: str,
    x: float,
    y: float,
    width: float,
    height: float,
    *,
    tone: str = "safe",
) -> None:
    fill = ui.MINT if tone == "safe" else ui.ORANGE_PALE
    stroke = ui.GREEN if tone == "safe" else ui.ORANGE
    ui.rounded_box(c, x, y, width, height, fill=fill, stroke=stroke)
    c.setFillColor(stroke)
    c.circle(ui.mm(x + 7), ui.mm(y + height - 9), ui.mm(4), fill=1, stroke=0)
    c.setFillColor(ui.WHITE)
    c.setFont(ui.FONT, 9)
    c.drawCentredString(ui.mm(x + 7), ui.mm(y + height - 10.5), str(number))
    ui.draw_text(c, x + 14, y + height - 11.5, title, size=9.6, color=ui.DARK)
    ui.paragraph(
        c,
        body,
        x + 7,
        y + height - 20,
        width - 14,
        size=7.7,
        leading=4.0,
        color=ui.INK,
    )


def command_box(c: canvas.Canvas, text: str, x: float, y: float, width: float, height: float) -> None:
    ui.rounded_box(c, x, y, width, height, fill=colors.HexColor("#1F2B28"), stroke=ui.DARK)
    cursor = y + height - 8
    for raw in text.splitlines():
        for line in ui.wrap_line(raw, 7.0, width - 12):
            ui.draw_text(c, x + 6, cursor, line, size=7.0, color=colors.HexColor("#E8F3EF"))
            cursor -= 3.8


def checklist(
    c: canvas.Canvas,
    items: list[str],
    x: float,
    y: float,
    width: float,
    *,
    size: float = 8.0,
    leading: float = 4.1,
    gap: float = 1.6,
) -> float:
    cursor = y
    for item in items:
        c.setFillColor(ui.WHITE)
        c.setStrokeColor(ui.GREEN)
        c.setLineWidth(0.8)
        c.rect(ui.mm(x), ui.mm(cursor - 1.4), ui.mm(3.4), ui.mm(3.4), fill=1, stroke=1)
        lines = ui.wrap_line(item, size, width - 7)
        for index, line in enumerate(lines):
            ui.draw_text(c, x + 6, cursor - index * leading, line, size=size, color=ui.INK)
        cursor -= leading * len(lines) + gap
    return cursor


def build() -> None:
    ui.register_font()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(OUTPUT), pagesize=A4, pageCompression=1)
    c.setTitle("Ooki Grader ホスト・アプリ セットアップ／運用ガイド")
    c.setAuthor("Ooki Grader")
    c.setSubject("Windowsホストの設置・AI接続設定・運用・復旧")
    c.setKeywords("Ooki Grader, Windows, セットアップ, Gemini, 運用, 復旧")

    # 1. Cover
    c.setFillColor(ui.PALE)
    c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    c.setFillColor(ui.DARK)
    c.rect(0, 0, ui.mm(10), PAGE_H, fill=1, stroke=0)
    c.setFillColor(ui.ORANGE)
    c.circle(ui.mm(29), ui.mm(271), ui.mm(11), fill=1, stroke=0)
    c.setFillColor(ui.WHITE)
    c.setFont(ui.FONT, 19)
    c.drawCentredString(ui.mm(29), ui.mm(264.5), "大")
    ui.draw_text(c, 47, 274, "OOKI GRADER", size=8, color=ui.GREEN)
    ui.draw_text(c, 18, 248, "ホスト・アプリ", size=27, color=ui.DARK)
    ui.draw_text(c, 18, 234, "セットアップ／運用ガイド", size=24, color=ui.DARK)
    ui.draw_text(c, 18, 221, "設置担当・管理者向け", size=11, color=ui.MUTED)
    ui.place_image(c, SCREEN_DIR / "01-admin-system-current.jpg", 17, 107, 176, 101)
    ui.callout(
        c,
        18,
        53,
        84,
        39,
        "日常運用の中心",
        "管理画面の「システム状態」と「AI接続」を確認します。先生は通常、WindowsサービスやAPIキーを操作しません。",
        tone="safe",
    )
    ui.callout(
        c,
        109,
        53,
        84,
        39,
        "対象環境",
        "Windows 11 Pro x64の校内ホスト。HTTPSで校内LANから利用し、インターネットへ直接公開しません。",
        tone="info",
    )
    ui.draw_text(c, 18, 34, "対象: v0.1 系 / 2026年8月6日", size=8, color=ui.MUTED)
    footer(c, 1)
    c.showPage()

    # 2. Scope and architecture
    page_header(c, "01  全体像", "誰が、どこまで管理するか", 2)
    ui.paragraph(
        c,
        "Ooki Graderは、1台のWindowsホストでデータとWebアプリを管理します。職員端末はブラウザだけを使い、外部AIへの送信もホスト経由です。",
        14,
        256,
        182,
        size=9.2,
        leading=4.8,
        color=ui.MUTED,
    )
    step_box(c, 1, "Windowsホスト", "アプリ、SQLite、答案画像、バックアップ処理、AI接続を管理。常時稼働・有線LAN・BitLocker・UPSを推奨。", 14, 206, 54, 35)
    step_box(c, 2, "校内LAN", "固定DNS名とHTTPS証明書を使用。ファイアウォールはPrivateプロファイルと学校サブネットだけに限定。", 78, 206, 54, 35)
    step_box(c, 3, "職員ブラウザ", "Edge / Chromeで同じURLを開く。教員はひな形・採点・確認、管理者は職員・AI接続・保存容量を管理。", 142, 206, 54, 35)
    c.setStrokeColor(ui.GREEN)
    c.setLineWidth(1.2)
    c.line(ui.mm(68), ui.mm(223), ui.mm(78), ui.mm(223))
    c.line(ui.mm(132), ui.mm(223), ui.mm(142), ui.mm(223))
    ui.draw_text(c, 14, 189, "担当の分離", size=11.5, color=ui.DARK)
    ui.rounded_box(c, 14, 116, 182, 66, fill=ui.WHITE, stroke=ui.BORDER)
    roles = [
        ("設置担当", "署名確認、インストール、DNS/TLS、サービス、ACL、復旧試験"),
        ("管理者", "最初の職員、AIキー、モデル、予算、状態、バックアップ確認"),
        ("先生", "テスト画像をアップロードし、AI下書きを原本と比較して公開・採点確認"),
        ("スキャン担当", "実施へ答案をアップロードし、処理状況と重複だけを確認"),
    ]
    row_y = 170
    for role, body in roles:
        ui.draw_text(c, 20, row_y, role, size=8.5, color=ui.GREEN)
        ui.paragraph(c, body, 48, row_y, 140, size=7.8, leading=4.0, color=ui.INK)
        row_y -= 14
    ui.callout(
        c,
        14,
        69,
        88,
        34,
        "秘密情報",
        "AI 接続キーは管理画面から一度だけ入力します。設定ファイル、CLI引数、手順書、スクリーンショット、ログへ書きません。",
        tone="warn",
    )
    ui.callout(
        c,
        108,
        69,
        88,
        34,
        "AIの位置づけ",
        "AIは下書きと採点候補を作ります。現在は自動公開・自動確定を行わず、先生が原本と結果を確認します。",
        tone="safe",
    )
    c.showPage()

    # 3. Preflight and installer build
    page_header(c, "02  導入準備", "インストール前に決めること", 3)
    ui.draw_text(c, 14, 256, "事前記入シート", size=11.5, color=ui.DARK)
    ui.rounded_box(c, 14, 175, 182, 73, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "ホスト: Windows 11 Pro x64、管理者権限、現在サポート中の更新",
            "DNS名: 例 ooki-grader.school.local（導入後は安易に変更しない）",
            "データ: 例 D:\\OokiGraderData（Program Filesとは別、NTFS、暗号化）",
            "バックアップ: 別ドライブ／NASの暗号化領域、十分な空き容量",
            "HTTPS: DNS名を含むサーバー証明書、校内端末が信頼するCA",
            "ネットワーク: 固定IPまたはDHCP予約、Privateサブネット、443番",
        ],
        20,
        235,
        168,
        size=8,
    )
    ui.draw_text(c, 14, 160, "インストーラーを作る（リリース担当）", size=11.5, color=ui.DARK)
    command_box(
        c,
        "# Windows 11 / PowerShell 7.4 / Inno Setup 6\n"
        "dotnet restore OokiGrader.slnx --runtime win-x64\n"
        "pwsh -File installer/New-OokiGraderReleasePackage.ps1 `\n"
        "  -Version 0.1.0 -OutputRoot C:\\OokiGrader-Releases `\n"
        "  -SigningHook C:\\secure\\Sign-Ooki.ps1\n"
        "pwsh -File installer/New-OokiGraderWindowsInstaller.ps1 `\n"
        "  -PackageRoot C:\\OokiGrader-Releases\\OokiGrader-0.1.0-win-x64 `\n"
        "  -Version 0.1.0 -OutputRoot C:\\OokiGrader-Releases `\n"
        "  -ExpectedSignerThumbprint <証明書サムプリント> `\n"
        "  -SigningHook C:\\secure\\Sign-Ooki.ps1",
        14,
        105,
        182,
        51,
    )
    ui.callout(
        c,
        14,
        70,
        88,
        31,
        "配布物",
        "OokiGrader-Setup-<version>-x64.exe と検証用JSON／SHA-256。学校データやAPIキーは含みません。",
        tone="info",
    )
    ui.callout(
        c,
        108,
        70,
        88,
        31,
        "公開前の必須条件",
        "署名済みEXEを管理経路で配布し、別のWindows 11機で署名・ハッシュ・新規導入・再起動を確認します。",
        tone="warn",
    )
    ui.paragraph(
        c,
        "本リポジトリで再現可能なビルドと静的検査は行えますが、実運用用の発行者証明書による署名と、実機での完全な導入試験は別途必要です。",
        14,
        57,
        182,
        size=7.8,
        leading=4.1,
        color=ui.MUTED,
    )
    c.showPage()

    # 4. Installation and bootstrap
    page_header(c, "03  ホスト設定", "インストールから初回ログインまで", 4)
    ui.draw_text(c, 14, 256, "ホスト側の手順", size=11.5, color=ui.DARK)
    step_box(c, 1, "署名を確認", "セットアップEXEの発行者、タイムスタンプ、SHA-256を配布元の記録と照合。異なる場合は実行しません。", 14, 218, 87, 28)
    step_box(c, 2, "管理者として実行", "DNS名、データルート、証明書、学校サブネットを入力。バックアップ先は初回ログイン後に別途設定します。", 109, 218, 87, 28)
    step_box(c, 3, "事前検査を通す", "OS、x64、NTFS、空き容量、443番、保留中の復旧／移行を確認。blocking failureは解消して再実行。", 14, 178, 87, 28)
    step_box(c, 4, "HTTPSで確認", "サービス起動後、ホストで /health/live と /health/ready、次に職員端末でログイン画面と証明書を確認。", 109, 178, 87, 28)
    ui.draw_text(c, 14, 162, "最初の管理者を作る", size=11.5, color=ui.DARK)
    ui.rounded_box(c, 14, 102, 182, 52, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "DataRoot直下の bootstrap-token.txt をホスト上で確認（初回・有効期限内のみ）",
            "ホスト自身のブラウザからセットアップ画面を開く（ループバック限定）",
            "管理者ユーザー名、表示名、12文字以上の固有パスワードを登録",
            "成功後、トークンファイルが削除され、通常ログインへ移ることを確認",
        ],
        20,
        141,
        168,
        size=8,
    )
    ui.callout(
        c,
        14,
        55,
        88,
        34,
        "ログイン制限",
        "連続失敗すると一時的に制限されます。ユーザー名・時刻・URLを確認し、管理者は状態画面と監査記録を確認します。闇雲に再試行しません。",
        tone="warn",
    )
    ui.callout(
        c,
        108,
        55,
        88,
        34,
        "学校名と製品名",
        "この学校向けビルドは学校を大木スクールとして登録します。画面上の製品名はシンプルに「Ooki Grader」です。",
        tone="safe",
    )
    c.showPage()

    # 5. AI provider setup
    page_header(c, "04  AI設定", "既定のGeminiを安全に有効化する", 5)
    ui.place_image(c, SCREEN_DIR / "02-admin-gemini-current.jpg", 14, 132, 182, 124)
    ui.draw_text(c, 14, 119, "設定順", size=11.5, color=ui.DARK)
    ui.rounded_box(c, 14, 60, 182, 52, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "管理者 → システム管理 → AI設定を開く",
            "学校管理のAPIキーを画面から入力し、接続テストを実行",
            "モデルが gemini-3.5-flash-lite であることを確認して保存",
            "接続先は画像対応モデルを指定し、接続成功時のみ採点運用を開始",
        ],
        20,
        99,
        168,
        size=8,
    )
    ui.callout(
        c,
        14,
        25,
        182,
        25,
        "重要",
        "接続成功は精度保証ではありません。実運用では検証済み設定のみを採用し、候補変更時は再評価します。",
        tone="warn",
    )
    c.showPage()

    # 6. Daily operations
    page_header(c, "05  日常運用", "システム状態を短時間で確認する", 6)
    ui.place_image(c, SCREEN_DIR / "01-admin-system-current.jpg", 14, 137, 182, 119)
    ui.draw_text(c, 14, 124, "管理者の確認周期", size=11.5, color=ui.DARK)
    ui.rounded_box(c, 14, 59, 182, 57, fill=ui.WHITE, stroke=ui.BORDER)
    rows = [
        ("毎日", "システム状態、AI接続、失敗処理、保存容量、直近バックアップ"),
        ("毎週", "未完了ジョブ、ログイン／権限変更、証明書期限、バックアップ検証"),
        ("毎月", "容量傾向、保持処理、職員一覧、AI利用量・予算、更新情報"),
        ("四半期", "隔離環境で復元訓練。手順・所要時間・欠損・資格情報再設定を記録"),
    ]
    row_y = 101
    for label, body in rows:
        ui.draw_text(c, 20, row_y, label, size=8.3, color=ui.GREEN)
        ui.paragraph(c, body, 44, row_y, 144, size=7.6, leading=3.9, color=ui.INK)
        row_y -= 12
    ui.callout(
        c,
        14,
        24,
        182,
        25,
        "警告の読み方",
        "意味のない再試行はしません。表示された項目名・時刻・対処を確認します。開発環境の物理保存先／バックアップ警告を、本番の正常性証明として扱わないでください。",
        tone="info",
    )
    c.showPage()

    # 7. Backup and lifecycle
    page_header(c, "06  保守", "バックアップ・更新・修復・復元", 7)
    ui.draw_text(c, 14, 256, "バックアップ", size=11.5, color=ui.DARK)
    ui.rounded_box(c, 14, 202, 182, 45, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "暗号化された別媒体を設定し、最新の成功日時と検証結果を確認",
            "SQLite／WALを手動コピーせず、アプリのオンラインバックアップを使用",
            "復元できた記録がないバックアップを、運用可能なバックアップと見なさない",
        ],
        20,
        234,
        168,
        size=8,
    )
    ui.draw_text(c, 14, 188, "変更作業の共通順序", size=11.5, color=ui.DARK)
    steps = [
        (1, "告知・保守時間", "利用を止め、対象バージョンと担当者を記録。"),
        (2, "検証済みバックアップ", "新しいバックアップと整合性確認を取得。"),
        (3, "署名・事前検査", "配布物、空き容量、復旧マーカーを確認。"),
        (4, "実行・再起動", "更新／修復を実行し、サービス再起動を確認。"),
        (5, "動作確認", "readiness、ログイン、画像表示、選択AIの少量試験。"),
        (6, "記録・解除", "結果、時刻、版、問題を残し、保守を解除。"),
    ]
    y = 154
    for idx, title, body in steps:
        x = 14 if idx % 2 == 1 else 109
        if idx % 2 == 1 and idx > 1:
            y -= 34
        step_box(c, idx, title, body, x, y, 87, 27)
    ui.callout(
        c,
        14,
        47,
        56,
        25,
        "更新",
        "Upgradeスクリプト／セットアップで版を切替。失敗時は旧版へ戻しreadiness確認。",
        tone="safe",
    )
    ui.callout(
        c,
        77,
        47,
        56,
        25,
        "復元",
        "必ずオフライン・隔離・明示確認。完了またはロールバック手順まで実施。",
        tone="warn",
    )
    ui.callout(
        c,
        140,
        47,
        56,
        25,
        "削除",
        "アプリ削除時もデータは既定で保持。データ廃棄は別の承認作業。",
        tone="info",
    )
    c.showPage()

    # 8. Troubleshooting
    page_header(c, "07  障害対応", "症状から最短で切り分ける", 8)
    ui.rounded_box(c, 14, 82, 182, 168, fill=ui.WHITE, stroke=ui.BORDER)
    cases = [
        ("ログイン試行上限", "URL・利用者名・端末時刻を確認。しばらく待ち、管理者が職員状態と監査を確認。再起動やアカウント作り直しを先に行わない。"),
        ("AI接続失敗", "AI接続の状態を確認。キー、モデル名、画像対応、時刻、DNS、到達性、予算上限を順に確認。"),
        ("AI下書きができない", "元画像が表示できるか、ファイル種別が正しいか、処理状況の失敗理由を確認。再実行前に同じ失敗が続く原因を記録。"),
        ("問題／答案画像が見えない", "ブラウザ更新、別端末、対象ファイルの存在、オブジェクト保存先、容量とreadinessを確認。座標や切り抜き設定は不要。"),
        ("保存容量の警告", "アップロードを一時停止。Windows物理空き容量、DataRoot、管理上限、保持処理を確認。データを手動削除しない。"),
        ("処理が止まっている", "処理状況で同一ジョブの重複・保留・失敗を確認。サービス状態と時刻を記録し、保守時間にHealth／Repairを使用。"),
        ("HTTPS警告", "先へ進まず、DNS名、証明書SAN・期限、校内CA、端末時刻を確認。証明書警告を無視してAPIキーを入力しない。"),
        ("更新後に起動しない", "復旧マーカーとログを保全。検証済みバックアップを確認し、旧版ロールバックまたはRepair。データを直接編集しない。"),
    ]
    y = 235
    for title, body in cases:
        ui.draw_text(c, 20, y, title, size=8.5, color=ui.GREEN)
        y = ui.paragraph(c, body, 55, y, 133, size=7.3, leading=3.7, color=ui.INK)
        y -= 4.0
        c.setStrokeColor(ui.BORDER)
        c.line(ui.mm(20), ui.mm(y + 1.5), ui.mm(188), ui.mm(y + 1.5))
        y -= 3.0
    ui.callout(
        c,
        14,
        37,
        182,
        33,
        "問い合わせ時に残す情報",
        "発生日時、利用者の役割、画面URL、対象ID、表示されたエラーコード、再現手順、直前の更新。APIキー・生徒の答案原本・パスワードは通常の連絡へ貼りません。",
        tone="warn",
    )
    c.showPage()

    # 9. Commissioning checklist
    page_header(c, "08  引き渡し", "運用開始前の最終チェック", 9)
    ui.paragraph(
        c,
        "すべてにチェックが付くまで、学校データの唯一の保存先・無人自動採点として使いません。結果と担当者を導入記録へ残します。",
        14,
        256,
        182,
        size=9,
        leading=4.7,
        color=ui.MUTED,
    )
    ui.rounded_box(c, 14, 78, 182, 164, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "セットアップEXEの発行者・タイムスタンプ・SHA-256を別経路で確認",
            "Windowsサービスが再起動・Windows再起動後も正しい版で起動",
            "アプリ、データ、バックアップが分離され、NTFS ACL／BitLockerを確認",
            "校内DNS・HTTPS・ファイアウォールをホストと職員端末の両方で確認",
            "最初の管理者、最小権限の職員、パスワード変更、ログイン制限を確認",
            "Gemini接続テスト、モデル、利用予算、サンプルひな形、採点確認を実施",
            "追加AI接続を使う場合は画像対応・精度ゲート合格を証明。自動切替は無効",
            "問題画像とAI下書きが同じ画面で確認でき、座標入力が不要",
            "バックアップ成功、整合性検査、隔離復元、復元後ログインを確認",
            "更新・失敗時ロールバック・Repair・データ保持Uninstallを実機で確認",
            "停電、低容量、ネット断、外部AI停止時の学校内手順を担当者が実演",
            "教師向けユーザーガイドと、この運用ガイドを校内の管理場所へ保存",
            "自動割当・自動確定は無効。学校ゴールデンセットと責任者承認前に有効化しない",
        ],
        20,
        229,
        168,
        size=7.8,
        leading=4.0,
        gap=2.1,
    )
    ui.callout(
        c,
        14,
        36,
        88,
        30,
        "今回の精度根拠",
        "実際の日本語穴埋め用紙1枚を2分類×3回。6/6回、66/66欄を通過。難しい1例の回帰根拠です。",
        tone="safe",
    )
    ui.callout(
        c,
        108,
        36,
        88,
        30,
        "残る外部ゲート",
        "署名済みWindows実機試験、校内LAN、復旧訓練、複数科目のゴールデンセット、担当者教育。",
        tone="warn",
    )
    c.save()


if __name__ == "__main__":
    build()
