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


OUTPUT = ROOT / "output" / "pdf" / "ooki-grader-onsite-installation-guide-ja.pdf"
SCREEN_DIR = Path(__file__).resolve().parent / "screens"
CAPTURE_DIR = ROOT / "output" / "playwright" / "manual-20260810"
PROCESSED_DIR = Path(__file__).resolve().parent / "processed"
PAGE_W, PAGE_H = A4


def footer(c: canvas.Canvas, page: int) -> None:
    c.setStrokeColor(ui.BORDER)
    c.setLineWidth(0.5)
    c.line(ui.mm(14), ui.mm(12.5), ui.mm(196), ui.mm(12.5))
    ui.draw_text(
        c,
        14,
        7,
        "Ooki Grader  現地インストールガイド / 無料ローカルCA版",
        size=6.5,
        color=ui.MUTED,
    )
    ui.draw_right(c, 196, 7, str(page), size=7, color=ui.MUTED)


def page_header(c: canvas.Canvas, section: str, title: str, page: int) -> None:
    c.setFillColor(ui.PALE)
    c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    ui.draw_text(c, 14, 282.5, section, size=8.1, color=ui.GREEN)
    ui.draw_text(c, 14, 271.5, title, size=18.5, color=ui.DARK)
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
    if tone == "warn":
        fill, stroke = ui.ORANGE_PALE, ui.ORANGE
    elif tone == "info":
        fill, stroke = ui.BLUE_PALE, ui.BLUE
    else:
        fill, stroke = ui.MINT, ui.GREEN
    ui.rounded_box(c, x, y, width, height, fill=fill, stroke=stroke)
    c.setFillColor(stroke)
    c.circle(ui.mm(x + 7), ui.mm(y + height - 9), ui.mm(4), fill=1, stroke=0)
    c.setFillColor(ui.WHITE)
    c.setFont(ui.FONT, 8.7)
    c.drawCentredString(ui.mm(x + 7), ui.mm(y + height - 10.5), str(number))
    ui.draw_text(c, x + 14, y + height - 11.5, title, size=9.2, color=ui.DARK)
    ui.paragraph(
        c,
        body,
        x + 7,
        y + height - 20,
        width - 14,
        size=7.2,
        leading=3.7,
        color=ui.INK,
    )


def steps_grid(
    c: canvas.Canvas,
    steps: list[tuple[str, str, str]],
    y_top: float,
    *,
    box_height: float = 31,
    row_gap: float = 7,
) -> float:
    y = y_top - box_height
    for index, (title, body, tone) in enumerate(steps):
        column = index % 2
        if index > 0 and column == 0:
            y -= box_height + row_gap
        x = 14 if column == 0 else 109
        step_box(c, index + 1, title, body, x, y, 87, box_height, tone=tone)
    if len(steps) % 2 == 1:
        return y - row_gap
    return y - row_gap


def command_box(
    c: canvas.Canvas,
    text: str,
    x: float,
    y: float,
    width: float,
    height: float,
    *,
    size: float = 7.1,
) -> None:
    ui.rounded_box(
        c,
        x,
        y,
        width,
        height,
        fill=colors.HexColor("#1D2926"),
        stroke=ui.DARK,
    )
    cursor = y + height - 8
    for raw in text.splitlines():
        wrapped = ui.wrap_line(raw, size, width - 12) or [""]
        for line in wrapped:
            ui.draw_text(
                c,
                x + 6,
                cursor,
                line,
                size=size,
                color=colors.HexColor("#E9F2EF"),
            )
            cursor -= 3.9


def checklist(
    c: canvas.Canvas,
    items: list[str],
    x: float,
    y: float,
    width: float,
    *,
    size: float = 7.8,
    leading: float = 4.0,
    gap: float = 1.7,
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


def label_value(
    c: canvas.Canvas,
    label: str,
    value: str,
    x: float,
    y: float,
    width: float,
) -> None:
    ui.rounded_box(c, x, y, width, 25, fill=ui.WHITE, stroke=ui.BORDER)
    ui.draw_text(c, x + 6, y + 16, label, size=7.1, color=ui.MUTED)
    ui.draw_text(c, x + 6, y + 7, value, size=9.0, color=ui.DARK)


def build() -> None:
    ui.register_font()
    ui.PROCESSED_DIR = PROCESSED_DIR
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    PROCESSED_DIR.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(OUTPUT), pagesize=A4, pageCompression=1)
    c.setTitle("Ooki Grader 現地インストールガイド")
    c.setAuthor("Ooki Grader")
    c.setSubject("有料証明書を使わないWindowsホスト・職員PCの現地設置手順")
    c.setKeywords(
        "Ooki Grader, Windows, 現地設置, 無料証明書, ローカルCA, HTTPS, インストール"
    )

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
    ui.draw_text(c, 18, 248, "現地インストール", size=26, color=ui.DARK)
    ui.draw_text(c, 18, 233, "ガイド", size=27, color=ui.DARK)
    ui.draw_text(
        c,
        18,
        220,
        "有料証明書なし / Windows 11 / 校内LAN専用",
        size=10.5,
        color=ui.MUTED,
    )
    ui.place_image(c, SCREEN_DIR / "02-dashboard.png", 17, 102, 176, 103, border=ui.GREEN)
    ui.callout(
        c,
        18,
        56,
        84,
        34,
        "この方式の費用",
        "HTTPSはホスト専用の無料ローカルCA。コード署名は購入せず、現地持込と全件チェックサム検査を使います。",
        tone="safe",
    )
    ui.callout(
        c,
        109,
        56,
        84,
        34,
        "設置後の入口",
        "ホストと職員PCは同じ https://ooki-grader.test/ を使います。IP直打ちや証明書警告の回避はしません。",
        tone="info",
    )
    ui.draw_text(c, 18, 34, "設置担当者向け / 2026年8月11日版", size=8, color=ui.MUTED)
    footer(c, 1)
    c.showPage()

    # 2. Trust model
    page_header(c, "01  方式", "無料でもHTTPSを正しく使う", 2)
    ui.paragraph(
        c,
        "有料証明書を使わないことと、暗号化や検査を省略することは別です。用途の違う2種類の証明書を分けて考えます。",
        14,
        256,
        182,
        size=9,
        leading=4.6,
        color=ui.MUTED,
    )
    ui.rounded_box(c, 14, 190, 87, 51, fill=ui.MINT, stroke=ui.GREEN)
    ui.draw_text(c, 20, 228, "ブラウザHTTPS", size=11, color=ui.DARK)
    ui.draw_text(c, 20, 216, "費用  0円", size=14, color=ui.GREEN)
    ui.paragraph(
        c,
        "ホスト上の専用ローカルCAが発行。職員PCへ公開CAだけを信頼登録します。",
        20,
        204,
        75,
        size=7.7,
        leading=4.0,
    )
    ui.rounded_box(c, 109, 190, 87, 51, fill=ui.BLUE_PALE, stroke=ui.BLUE)
    ui.draw_text(c, 115, 228, "Windowsコード署名", size=11, color=ui.DARK)
    ui.draw_text(c, 115, 216, "今回は購入しない", size=12.5, color=ui.BLUE)
    ui.paragraph(
        c,
        "今回は購入しません。署名済み版も任意対応し、その場合は未署名確認を表示しません。未署名版は全SHA-256を検査します。",
        115,
        204,
        75,
        size=7.2,
        leading=4.0,
    )
    y = steps_grid(
        c,
        [
            ("直接持ち込む", "管理下のUSB等を使用。メールや不明な共有リンクから取り直しません。", "safe"),
            ("全件を検査", "インストール前に不足・改変・余分なファイルをチェックサムで検出します。", "safe"),
            ("秘密鍵を配らない", "職員PC用フォルダーには公開CAだけ。PFX/P12があれば中止します。", "warn"),
            ("警告を回避しない", "ブラウザ警告やHTTPS失敗は原因を直し、先へ進む操作で避けません。", "warn"),
        ],
        175,
    )
    ui.callout(
        c,
        14,
        y - 29,
        182,
        28,
        "CA秘密鍵の扱い",
        "ホストの証明書ストアに非エクスポート可能として保存します。ホストを失った場合は、新しいCAを作り全職員PCで再設定します。",
        tone="info",
    )
    c.showPage()

    # 3. Preparation sheet
    page_header(c, "02  準備", "訪問前にそろえるもの", 3)
    ui.draw_text(c, 14, 256, "持ち物", size=11.5, color=ui.DARK)
    ui.rounded_box(c, 14, 177, 87, 69, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "Windows x64リリースフォルダー一式",
            "Microsoft公式PowerShell 7 x64 MSI",
            "管理下のUSBとホスト管理者情報",
            "各職員PCの管理者情報",
            "データ用ドライブ（165GiB以上）",
            "暗号化済みの別バックアップ先",
            "Gemini APIキー（任意）",
        ],
        20,
        233,
        75,
        size=7.5,
    )
    ui.draw_text(c, 109, 256, "塾へ確認", size=11.5, color=ui.DARK)
    ui.rounded_box(c, 109, 177, 87, 69, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "ホスト固定IPまたはDHCP予約",
            "職員LANのサブネット（CIDR）",
            "ホストと職員PCの台数",
            "主管理者と副管理者",
            "バックアップ担当者と保守時間",
            "停電時・故障時の連絡先",
        ],
        115,
        233,
        75,
        size=7.5,
    )
    ui.draw_text(c, 14, 162, "設置記録（例）", size=11.5, color=ui.DARK)
    label_value(c, "正式URL", "https://ooki-grader.test/", 14, 125, 87)
    label_value(c, "ホストIP", "192.168.10.20", 109, 125, 87)
    label_value(c, "許可CIDR", "192.168.10.0/24", 14, 92, 87)
    label_value(c, "DataRoot", "D:\\OokiGraderData", 109, 92, 87)
    label_value(c, "BackupRoot", "E:\\OokiGraderBackup", 14, 59, 87)
    label_value(c, "職員PC", "______ 台", 109, 59, 87)
    ui.callout(
        c,
        14,
        24,
        182,
        25,
        "記録しないもの",
        "APIキー、初期トークン、パスワード、PFXの内容は、紙・写真・チャット・通常のチケットへ残しません。",
        tone="warn",
    )
    c.showPage()

    # 4. Windows preparation
    page_header(c, "03  ホスト準備", "Windowsとネットワークを整える", 4)
    ui.paragraph(
        c,
        "現地セットアップは安全側で事前検査します。ブロッキング失敗を回避せず、ホスト条件を先に整えます。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    steps_grid(
        c,
        [
            ("Windows更新", "Windows 11 Pro x64を更新し、再起動。時刻とAsia/Tokyoを確認します。", "safe"),
            ("LANをPrivateへ", "有線LANを推奨。ネットワークプロファイルをプライベートにします。", "safe"),
            ("IPを予約", "表示予定のIPをルーターでDHCP予約、または固定割当します。", "warn"),
            ("ディスクを確認", "DataRootはローカルNTFS。165GiB以上の空きと暗号化を確認します。", "safe"),
            ("電源を安定", "スリープを無効化。可能ならUPSと停電復帰後の自動起動を用意します。", "info"),
            ("PowerShell 7", "Microsoft公式署名済みx64 MSIで7.4以降を導入。PATH登録、リモート機能は不要。", "info"),
        ],
        243,
        box_height=34,
        row_gap=8,
    )
    command_box(c, "pwsh --version\nGet-Volume\nGet-NetConnectionProfile\nGet-NetIPAddress -AddressFamily IPv4", 14, 48, 182, 39)
    ui.callout(
        c,
        14,
        20,
        182,
        21,
        "不要なもの",
        "学校ホストに.NET SDK、Node.js、ソースコード、Inno Setupは入れません。配布物は自己完結型です。",
        tone="safe",
    )
    c.showPage()

    # 5. Package verification
    page_header(c, "04  配布物", "コピーして全件検査の入口を確認", 5)
    ui.paragraph(
        c,
        "USBのリリースフォルダー全体をローカルへコピーします。同名ファイルだけを後から差し替えないでください。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    command_box(
        c,
        "Set-Location 'C:\\OokiGrader-Setup\\OokiGrader-0.1.0-win-x64'\n"
        "Get-ChildItem release-inventory.json, checksums.txt, `\n"
        "  OokiGrader.Host.exe, OokiGrader.Tool.exe, `\n"
        "  Install-OokiGraderOnSite.ps1",
        14,
        198,
        182,
        44,
    )
    ui.draw_text(c, 14, 184, "この後スクリプトが検査するもの", size=11, color=ui.DARK)
    ui.rounded_box(c, 14, 104, 182, 71, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "release inventoryの形式、製品名、版、win-x64ランタイム",
            "checksums.txtが全配布ファイルを過不足なく含むこと",
            "全ファイルのSHA-256が一致すること",
            "HostとToolが必要な自己完結型ファイルを含むこと",
            "署名済みなら発行者拇印、未署名なら現地持込の明示確認",
        ],
        20,
        160,
        168,
        size=7.8,
    )
    ui.callout(
        c,
        14,
        57,
        88,
        34,
        "失敗したとき",
        "個別ファイルを修復しません。管理元USBからフォルダー全体を取り直し、版名も変えません。",
        tone="warn",
    )
    ui.callout(
        c,
        108,
        57,
        88,
        34,
        "Windowsの警告",
        "不明な配布元の警告を一般利用者に回避させません。設置担当者が配布元と検査結果を確認します。",
        tone="info",
    )
    c.showPage()

    # 6. Run installer
    page_header(c, "05  実行", "現地セットアップを開始する", 6)
    ui.paragraph(
        c,
        "まず引数なしで起動し、自動検出されたDataRoot、IP、CIDRを設置記録と照合します。BackupRootを初回から指定する場合だけ2つ目の例を使います。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    command_box(
        c,
        "# 基本: 検出された既定値を対話で確認\n"
        "pwsh -NoLogo -NoProfile -File .\\Install-OokiGraderOnSite.ps1\n\n"
        "# 暗号化済みバックアップ先も初回から構成\n"
        "pwsh -NoLogo -NoProfile -File .\\Install-OokiGraderOnSite.ps1 `\n"
        "  -BackupRoot 'E:\\OokiGraderBackup' `\n"
        "  -BackupDestinationEncryptionConfirmed",
        14,
        183,
        182,
        61,
        size=6.8,
    )
    ui.draw_text(c, 14, 169, "必要な場合に入力する確認語", size=11, color=ui.DARK)
    label_value(c, "PRIVATE", "信頼できる校内LANへ変更", 14, 131, 56)
    label_value(c, "ENCRYPTED", "バックアップ先を暗号化済み", 77, 131, 56)
    label_value(c, "UNSIGNED", "直接管理する未署名配布物", 140, 131, 56)
    label_value(c, "RESERVED", "表示IPを予約／固定済み", 45, 98, 56)
    label_value(c, "INSTALL", "表示構成で変更を実行", 109, 98, 56)
    ui.callout(
        c,
        14,
        54,
        182,
        36,
        "INSTALLより前に戻る",
        "URL、IP、CIDR、DataRoot、BackupRootのどれかが違う場合はCtrl+Cで中止します。INSTALLより前はサービスや証明書を変更しません。",
        tone="warn",
    )
    ui.callout(
        c,
        14,
        21,
        182,
        25,
        "入力を省略しない",
        "確認語は表示された条件を実際に満たす場合だけ入力します。自動化用スイッチを手入力の代用にしません。",
        tone="safe",
    )
    c.showPage()

    # 7. What changes
    page_header(c, "06  自動構成", "スクリプトが変更する範囲", 7)
    ui.paragraph(
        c,
        "一つのスクリプトが、途中の安全検査を保ったまま証明書、サービス、ネットワーク、職員PC用配布物まで作ります。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    steps_grid(
        c,
        [
            ("配布物検査", "版、全SHA-256、余分なファイル、win-x64を確認します。", "safe"),
            ("無料CA発行", "ooki-grader.testとホストIPを含むHTTPS証明書を作ります。", "safe"),
            ("ホスト名解決", "ホストに127.0.0.1の管理対象hosts行を追加します。", "info"),
            ("サービス構成", "Windows Service、ACL、SQLite、DataRootを構成します。", "safe"),
            ("LANを限定", "指定した校内CIDRだけを許可するFirewall規則を作ります。", "warn"),
            ("バックアップ", "指定済みなら暗号化確認を記録し、スケジュールを有効化します。", "info"),
            ("職員PC用", "公開CA、hosts、URLを固定した秘密鍵なしのフォルダーを作ります。", "safe"),
            ("最終health", "DB、保存先、サービス、実際のHTTPS readinessを検査します。", "safe"),
        ],
        242,
        box_height=31,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        21,
        182,
        28,
        "成功の目印",
        "最後のJSONで state が installed-and-verified、localHealth が healthy、tlsBypassUsed が false であることを記録します。",
        tone="safe",
    )
    c.showPage()

    # 8. Host checks
    page_header(c, "07  ホスト確認", "サービスとHTTPSを再起動後も確認", 8)
    ui.paragraph(
        c,
        "成功表示だけで終わらず、Windowsを一度再起動し、同じ正式URLでサービスとHTTPSを確認します。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    command_box(
        c,
        "Get-Service OokiGrader.Host\n"
        "Get-NetFirewallRule -DisplayName 'Ooki Grader HTTPS'\n"
        "Invoke-WebRequest 'https://ooki-grader.test/health/live' -UseBasicParsing\n"
        "Invoke-WebRequest 'https://ooki-grader.test/health/ready' -UseBasicParsing",
        14,
        195,
        182,
        47,
    )
    ui.rounded_box(c, 14, 94, 182, 87, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "OokiGrader.HostがRunning、スタートアップが遅延自動起動",
            "liveとreadyが成功し、TLS検査を回避していない",
            "正式URLが証明書警告なしで開く",
            "Windows再起動後も同じ結果になる",
            "FirewallのRemoteAddressが承認済みCIDRだけ",
            "DataRoot、InstallRoot、BackupRootが相互に別パス",
            "Applicationイベントログに重大な起動エラーがない",
        ],
        20,
        166,
        168,
        size=7.7,
    )
    ui.callout(
        c,
        14,
        52,
        88,
        29,
        "readyが失敗",
        "サービス、443番、証明書、DataRoot、操作マーカーを確認。データを手動削除しません。",
        tone="warn",
    )
    ui.callout(
        c,
        108,
        52,
        88,
        29,
        "正式URLだけ",
        "localhostやIP直打ちはOriginと証明書が一致しません。ショートカットを使います。",
        tone="info",
    )
    c.showPage()

    # 9. Peer setup
    page_header(c, "08  職員PC", "生成されたフォルダーを1台ずつ実行", 9)
    ui.paragraph(
        c,
        "既定の出力先は Public Documents の OokiGrader-Client-Setup-Packages です。生成された1フォルダーを丸ごと各PCへコピーします。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    steps_grid(
        c,
        [
            ("フォルダーを確認", "peer-trust.json、公開CA、checksums、CMD、PS1、moduleが揃っている。", "safe"),
            ("秘密鍵なし", "PFX/P12が1つでもあれば実行せず、技術担当者へ戻します。", "warn"),
            ("管理者実行", "Install-On-This-PC.cmdを右クリックし、管理者として実行します。", "safe"),
            ("HTTPS検査", "公開CAとhosts設定後、実際の/health/readyが自動で成功する。", "safe"),
            ("ショートカット", "共通デスクトップのOoki Graderを開き、正式URLを確認します。", "info"),
            ("全台で記録", "PC名、実施者、日時、成功結果を設置記録へ残します。", "info"),
        ],
        242,
        box_height=34,
        row_gap=8,
    )
    command_box(
        c,
        "OokiGrader-Client-Setup-ooki-grader.test-xxxxxxxxxxxx\\\n"
        "  Install-On-This-PC.cmd\n"
        "  Install-OokiGraderPeerTrust.ps1\n"
        "  OokiGrader.Windows.psm1\n"
        "  ooki-grader-local-ca.cer\n"
        "  peer-trust.json / checksums.txt / README.txt",
        14,
        47,
        182,
        48,
    )
    ui.callout(
        c,
        14,
        19,
        182,
        21,
        "失敗時",
        "ブラウザ警告を回避せず、固定IP、職員LAN、Firewall CIDR、PC時刻、管理対象hosts行を確認します。",
        tone="warn",
    )
    c.showPage()

    # 10. First login
    page_header(c, "09  初回起動", "ホストで最初の管理者を作る", 10)
    ui.place_image(
        c,
        SCREEN_DIR / "00-first-admin-bootstrap.png",
        14,
        126,
        182,
        126,
        border=ui.GREEN,
    )
    steps_grid(
        c,
        [
            ("正式URLを開く", "ホスト自身で https://ooki-grader.test/ を開きます。", "safe"),
            ("トークンを読む", "DataRoot直下のbootstrap-token.txtをホスト上だけで確認します。", "warn"),
            ("管理者を登録", "ユーザー名、表示名、12文字以上の固有パスワードを入力します。", "safe"),
            ("削除を確認", "完了後、tokenファイルが削除され再利用できないことを確認します。", "safe"),
        ],
        115,
        box_height=31,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        20,
        182,
        24,
        "初期トークンは秘密",
        "写真、チャット、チケット、クリップボード履歴へ残しません。期限切れならサービスの通常再起動で再発行します。",
        tone="warn",
    )
    c.showPage()

    # 11. Staff
    page_header(c, "10  職員", "副管理者と最小権限の職員を登録", 11)
    ui.place_image(c, SCREEN_DIR / "05-admin-staff.png", 14, 124, 182, 128, border=ui.GREEN)
    ui.rounded_box(c, 14, 60, 182, 52, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "異なる担当者の副管理者を1名作る",
            "先生、スキャン担当、閲覧担当は必要な役割だけを付ける",
            "一時パスワードを本人へ直接渡し、期限内の変更を確認する",
            "退職・異動時は削除せず無効化し、既存セッションを失効させる",
        ],
        20,
        99,
        168,
        size=7.8,
    )
    ui.callout(
        c,
        14,
        22,
        182,
        28,
        "最後の管理者",
        "最後の有効な管理者は無効化できません。復旧できる副管理者を用意してから通常運用へ入ります。",
        tone="safe",
    )
    c.showPage()

    # 12. AI
    page_header(c, "11  AI", "Geminiを確認し、4機能を一括で有効化", 12)
    ui.place_image(
        c,
        CAPTURE_DIR / "41-admin-ai-one-step.png",
        14,
        119,
        182,
        133,
        border=ui.BLUE,
    )
    steps_grid(
        c,
        [
            ("AI設定を開く", "管理 > AI設定 > 接続を追加を選びます。", "info"),
            ("キーを入力", "学校管理のAPIキーを画面から一度だけ入力します。", "warn"),
            ("確認して有効化", "保存前に認証、モデル、画像、構造化出力、利用量、画像タスクを確認。", "safe"),
            ("4機能を確認", "成功時だけ暗号化保存。4行すべての「利用できます」を確認。", "safe"),
        ],
        108,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        20,
        182,
        23,
        "失敗時は以前の設定を維持",
        "候補キーは保存されません。交換時は以前の正常なキーと4機能が残り、初回は未設定のままです。秘密値を画面写真、引数、手順書、通常ログへ入れません。",
        tone="warn",
    )
    c.showPage()

    # 13. Backup and health
    page_header(c, "12  バックアップ", "最初の手動バックアップを完全検証", 13)
    ui.place_image(c, SCREEN_DIR / "03-admin-health.png", 14, 116, 182, 136, border=ui.GREEN)
    steps_grid(
        c,
        [
            ("保存先を確認", "現地セットアップで指定したBackupRootが利用可能と表示される。", "safe"),
            ("手動作成", "手動バックアップを押し、完了まで処理状況を確認します。", "info"),
            ("完全検証", "最新レコードの完全検証を実行し、verified時刻を確認します。", "safe"),
            ("復元計画", "復元そのものではなく、必要ファイルと作業を読み取り確認します。", "warn"),
        ],
        105,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        19,
        182,
        23,
        "保存先の設定場所",
        "管理画面は実行・検証・確認用です。BackupRootそのものは現地セットアップの -BackupRoot で構成します。",
        tone="info",
    )
    c.showPage()

    # 14. Acceptance smoke test
    page_header(c, "13  受入テスト", "架空データで実際の作業を一巡", 14)
    ui.draw_text(c, 14, 256, "ひな形の採点ルール", size=10, color=ui.DARK)
    ui.place_image(c, SCREEN_DIR / "08-template-flags.png", 14, 155, 87, 91, border=ui.GREEN)
    ui.draw_text(c, 109, 256, "1ページPDFの順番取込", size=10, color=ui.DARK)
    ui.place_image(c, SCREEN_DIR / "09-ordered-scan.png", 109, 155, 87, 91, border=ui.BLUE)
    ui.rounded_box(c, 14, 55, 182, 88, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "架空生徒を登録する",
            "サンプルHOPまたはSTEPからひな形を作り、問題・正答・配点を照合する",
            "完答・順不同・漢字必須を確認し、実施日を指定して受付を開始する",
            "ひな形の試験名・教科・学年・カテゴリ・コースが受付へ引き継がれることを確認する",
            "1ページPDFを生徒ごとに連続して取り込み、区切りを確認する",
            "生徒名確認、採点確認、確定、帳票PDF作成まで完了する",
            "失敗ジョブ、AI接続、保存容量、バックアップに異常がない",
        ],
        20,
        130,
        168,
        size=7.6,
    )
    ui.callout(
        c,
        14,
        21,
        182,
        25,
        "実データの前に",
        "ホストを再起動し、職員PCのショートカットから再ログインできることまで塾責任者と一緒に確認します。",
        tone="safe",
    )
    c.showPage()

    # 15. Troubleshooting
    page_header(c, "14  設置時の問題", "症状から最初の確認へ", 15)
    ui.rounded_box(c, 14, 65, 182, 184, fill=ui.WHITE, stroke=ui.BORDER)
    cases = [
        ("PowerShell不足", "pwsh --version。64-bit 7.4以降をホストへ導入。"),
        ("checksum失敗", "配布元、USB、不足・余分なファイル。個別差替えをせず一式を取り直す。"),
        ("容量不足", "DataRootのNTFSボリュームに165GiB以上の物理空きがあるか。"),
        ("RESERVED不可", "ルーターのDHCP予約または固定IP設定を先に完了。"),
        ("443競合", "Get-NetTCPConnection -LocalPort 443 -State Listen で所有プロセスを確認。"),
        ("職員PCだけ不可", "peer setup結果、職員LAN、Firewall CIDR、固定IP、PC時刻を確認。"),
        ("証明書警告", "正式URL、公開CA、hosts行、時刻。警告を無視してログインしない。"),
        ("ORIGIN_REJECTED", "IP直打ちや別名をやめ、生成されたHTTPSショートカットを使用。"),
        ("AI接続失敗", "候補キー、画像対応、外向きDNS/HTTPS、予算、クォータ。以前の接続は削除しない。"),
        ("backup未設定", "管理画面では設定不可。技術担当者が保守時間に安全に再構成。"),
    ]
    y = 236
    for title, body in cases:
        ui.draw_text(c, 20, y, title, size=8.2, color=ui.GREEN)
        ui.paragraph(c, body, 55, y, 133, size=7.2, leading=3.7, color=ui.INK)
        y -= 15.7
        c.setStrokeColor(ui.BORDER)
        c.line(ui.mm(20), ui.mm(y + 5), ui.mm(188), ui.mm(y + 5))
    ui.callout(
        c,
        14,
        27,
        182,
        27,
        "残す情報",
        "発生時刻、PC名、URL、エラー全文、相関ID、直前の操作。APIキー、パスワード、実答案は通常の連絡へ添付しません。",
        tone="warn",
    )
    c.showPage()

    # 16. Handover
    page_header(c, "15  引き渡し", "全項目を実演して記録を渡す", 16)
    ui.paragraph(
        c,
        "設置担当者だけが分かる状態を残さず、塾の主管理者と副管理者が同じ確認を再現できるようにします。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    ui.rounded_box(c, 14, 76, 182, 166, fill=ui.WHITE, stroke=ui.BORDER)
    checklist(
        c,
        [
            "版、配布元、全件チェックサム検査結果を記録した",
            "固定IP、正式URL、CIDR、DataRoot、BackupRootを記録した",
            "サービス、Firewall、live、ready、Windows再起動を確認した",
            "CA拇印、期限、非エクスポート可能な秘密鍵の扱いを記録した",
            "全職員PCでpeer setupと実HTTPS検査が成功した",
            "全職員PCで証明書警告なしのショートカットを確認した",
            "初期トークンが削除され、主管理者・副管理者を作った",
            "最小権限の職員とパスワード変更を確認した",
            "Geminiの「接続を確認して有効化」と4機能の利用確認が成功した",
            "手動バックアップ、完全検証、復元計画確認が成功した",
            "架空データのひな形、答案、採点、帳票PDFを実演した",
            "日次・週次・月次担当、保守時間、障害連絡先を決めた",
            "教師向けユーザーガイドとホスト運用ガイドを渡した",
        ],
        20,
        229,
        168,
        size=7.5,
        leading=3.9,
        gap=1.8,
    )
    ui.callout(
        c,
        14,
        37,
        88,
        27,
        "保管",
        "配布USB、APIキー、初期トークンを通常の教室へ放置しません。",
        tone="warn",
    )
    ui.callout(
        c,
        108,
        37,
        88,
        27,
        "次に読む",
        "日常管理、更新、修復、復元はホスト・アプリ運用ガイドへ進みます。",
        tone="safe",
    )

    c.save()
    print(OUTPUT)


if __name__ == "__main__":
    build()
