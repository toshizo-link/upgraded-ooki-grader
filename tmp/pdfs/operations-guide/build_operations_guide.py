from __future__ import annotations

import sys
from pathlib import Path

from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas


ROOT = Path(__file__).resolve().parents[3]
USER_GUIDE_DIR = ROOT / "tmp" / "pdfs" / "user-guide"
INSTALL_GUIDE_DIR = ROOT / "tmp" / "pdfs" / "installation-guide"
sys.path.insert(0, str(USER_GUIDE_DIR))
sys.path.insert(0, str(INSTALL_GUIDE_DIR))

import build_user_guide as ui  # noqa: E402
import build_installation_guide as manual  # noqa: E402


OUTPUT = ROOT / "output" / "pdf" / "ooki-grader-host-operations-guide-ja.pdf"
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
        "Ooki Grader  ホスト・アプリ運用ガイド",
        size=6.7,
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


def build() -> None:
    ui.register_font()
    ui.PROCESSED_DIR = PROCESSED_DIR
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    PROCESSED_DIR.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(OUTPUT), pagesize=A4, pageCompression=1)
    c.setTitle("Ooki Grader ホスト・アプリ運用ガイド")
    c.setAuthor("Ooki Grader")
    c.setSubject("Windowsホストの日常管理、AI、職員、帳票、バックアップ、更新、修復、復元")
    c.setKeywords(
        "Ooki Grader, Windows, 管理, Gemini, 職員, 帳票, 一括出力, バックアップ, 更新, 復元, 障害対応"
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
    ui.draw_text(c, 18, 248, "ホスト・アプリ", size=26, color=ui.DARK)
    ui.draw_text(c, 18, 233, "運用ガイド", size=27, color=ui.DARK)
    ui.draw_text(
        c,
        18,
        220,
        "設置後の管理・バックアップ・保守・復旧",
        size=10.5,
        color=ui.MUTED,
    )
    ui.place_image(
        c,
        SCREEN_DIR / "01-admin-system-current.png",
        17,
        102,
        176,
        103,
        border=ui.GREEN,
    )
    ui.callout(
        c,
        18,
        56,
        84,
        34,
        "毎日の入口",
        "管理 > システム状態でホスト、AI、バックアップを確認し、保存容量と処理状況へ進みます。",
        tone="safe",
    )
    ui.callout(
        c,
        109,
        56,
        84,
        34,
        "対象構成",
        "Windows 11 Proホスト1台、校内LAN、無料ローカルCA、正式URLはooki-grader.testです。",
        tone="info",
    )
    ui.draw_text(c, 18, 34, "システム管理者・Windows担当者向け / 2026年8月11日版", size=8, color=ui.MUTED)
    footer(c, 1)
    c.showPage()

    # 2. Architecture
    page_header(c, "01  全体像", "誰が、何を管理するか", 2)
    ui.paragraph(
        c,
        "1台のWindowsホストがWebアプリ、SQLite、画像、帳票、AI送信、バックアップを管理します。職員PCはブラウザだけを使います。",
        14,
        256,
        182,
        size=9,
        leading=4.6,
        color=ui.MUTED,
    )
    manual.steps_grid(
        c,
        [
            ("Windowsホスト", "Service、DataRoot、SQLite、画像、帳票、資格情報、外部AI接続。", "safe"),
            ("校内LAN", "固定IP、ooki-grader.test、HTTPS 443、許可CIDRだけ。", "info"),
            ("職員PC", "生成済みショートカットから正式URLを開く。秘密鍵やAPIキーは置かない。", "safe"),
            ("外部AI", "ホストだけがGemini/OpenRouterへ送信。インターネットからの受信は不可。", "warn"),
        ],
        242,
        box_height=37,
        row_gap=9,
    )
    ui.draw_text(c, 14, 147, "担当の分離", size=11, color=ui.DARK)
    ui.rounded_box(c, 14, 72, 182, 66, fill=ui.WHITE, stroke=ui.BORDER)
    roles = [
        ("管理者", "職員、AI接続、状態、バックアップ実行、保存容量、処理状況"),
        ("Windows担当", "サービス、証明書、hosts、Firewall、ACL、更新、修復、復元"),
        ("バックアップ担当", "暗号化保存先、完全検証、隔離復元訓練、RPO/RTO"),
        ("先生", "ひな形・採点・確定。AI提案を原本と比較して判断"),
    ]
    y = 125
    for label, body in roles:
        ui.draw_text(c, 20, y, label, size=8.2, color=ui.GREEN)
        ui.paragraph(c, body, 50, y, 138, size=7.5, leading=3.8, color=ui.INK)
        y -= 14
    ui.callout(
        c,
        14,
        32,
        88,
        28,
        "秘密情報",
        "APIキー、パスワード、初期トークンを手順書・写真・通常ログへ残しません。",
        tone="warn",
    )
    ui.callout(
        c,
        108,
        32,
        88,
        28,
        "AIの境界",
        "AIは下書きと候補を作ります。答案受付の開始・採点修正・確定は先生が行います。",
        tone="safe",
    )
    c.showPage()

    # 3. First admin
    page_header(c, "02  初回管理", "ホスト限定の管理者作成を完了", 3)
    ui.place_image(
        c,
        SCREEN_DIR / "00-first-admin-bootstrap-current.png",
        14,
        124,
        182,
        128,
        border=ui.GREEN,
    )
    manual.steps_grid(
        c,
        [
            ("ホストで開く", "https://ooki-grader.test/ をホスト自身で開きます。", "safe"),
            ("トークンを確認", "DataRoot/bootstrap-token.txtをホスト上だけで読みます。", "warn"),
            ("管理者を登録", "固有ユーザー名、表示名、12文字以上のパスワード。", "safe"),
            ("痕跡を確認", "成功後にtokenファイルが削除され、通常ログインへ移る。", "safe"),
        ],
        112,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        20,
        182,
        23,
        "副管理者を先に作る",
        "事故対応のため、異なる担当者の管理者を2名用意します。最後の有効な管理者は無効化できません。",
        tone="safe",
    )
    c.showPage()

    # 4. Daily health
    page_header(c, "03  システム状態", "毎日、対応が必要な項目から確認", 4)
    ui.place_image(
        c,
        SCREEN_DIR / "01-admin-system-current.png",
        14,
        113,
        182,
        139,
        border=ui.GREEN,
    )
    manual.steps_grid(
        c,
        [
            ("全体状態", "正常／要確認と最終確認時刻を見る。", "safe"),
            ("対応項目", "警告本文、対象、エラーコードを読む。", "warn"),
            ("AI接続", "選択接続、モデル、最終確認、画像対応。", "info"),
            ("バックアップ", "最終成功、完全検証、保存先到達性。", "safe"),
        ],
        102,
        box_height=29,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        19,
        182,
        22,
        "警告を消すためだけに再試行しない",
        "項目名、時刻、相関IDを記録し、原因を直してから同じ安全な操作経路で再試行します。",
        tone="warn",
    )
    c.showPage()

    # 5. AI
    page_header(c, "04  AI設定", "候補キーを確認し、4機能を一括で利用可能に", 5)
    ui.place_image(
        c,
        CAPTURE_DIR / "41-admin-ai-one-step.png",
        14,
        112,
        182,
        140,
        border=ui.BLUE,
    )
    manual.steps_grid(
        c,
        [
            ("追加・交換", "キーを入力し「接続を確認して有効化」を1回押す。", "warn"),
            ("保存前確認", "認証、モデル、画像、構造化出力、利用量、画像タスクを確認。", "info"),
            ("成功時", "キーを暗号化し、ひな形・氏名・採点・再確認を一括設定。", "safe"),
            ("失敗時", "候補を保存せず、交換時は以前のキーと4機能を維持。", "safe"),
        ],
        101,
        box_height=29,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        18,
        182,
        22,
        "利用可能でも先生確認は省略しない",
        "AIは通常答案の氏名欄と初回採点を同じ送信で処理します。名簿照合、生徒割当、答案確定は先生が元画像を確認します。OpenRouterは上級者向けで、保存後に手動で「再確認」します。",
        tone="warn",
    )
    c.showPage()

    # 6. Staff
    page_header(c, "05  職員", "追加・再設定・無効化を履歴付きで行う", 6)
    ui.place_image(
        c,
        SCREEN_DIR / "05-admin-staff-current.png",
        14,
        124,
        182,
        128,
        border=ui.GREEN,
    )
    manual.steps_grid(
        c,
        [
            ("追加", "表示名、ユーザー名、最小限の役割、一時パスワード。", "safe"),
            ("初回変更", "本人へ直接渡し、期限内に固有パスワードへ変更。", "warn"),
            ("再設定", "本人確認後に一時パスワードを発行。既存セッションを失効。", "info"),
            ("無効化", "削除せず状態を変更。再有効化後は本人が再ログイン。", "safe"),
        ],
        112,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        20,
        182,
        23,
        "毎月の突合",
        "学校の職員台帳とアプリを照合し、不要な管理者権限、退職者、変更待ちを確認します。",
        tone="safe",
    )
    c.showPage()

    # 7. Backups
    page_header(c, "06  バックアップ", "作成・完全検証・復元計画を分ける", 7)
    ui.place_image(
        c,
        SCREEN_DIR / "08-admin-backup-current.png",
        14,
        119,
        182,
        133,
        border=ui.GREEN,
    )
    manual.steps_grid(
        c,
        [
            ("保存先", "BackupRootは現地設置で設定。画面から場所は変更しません。", "info"),
            ("手動作成", "最新成功時刻と対象版を確認し、完了を待ちます。", "safe"),
            ("完全検証", "integrityがok、verified時刻が更新されたことを確認。", "safe"),
            ("復元計画", "読取専用。必要ファイル、移行、操作を確認します。", "warn"),
        ],
        108,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        19,
        182,
        22,
        "既定では答案画像を含めない",
        "IncludeManagedScansはfalseです。画像復旧が必要なら容量、保持、個人情報を承認してから構成変更します。",
        tone="warn",
    )
    c.showPage()

    # 8. Storage
    page_header(c, "07  保存容量", "管理対象と物理ディスクを別々に確認", 8)
    ui.place_image(
        c,
        SCREEN_DIR / "06-admin-storage-current.png",
        14,
        118,
        182,
        134,
        border=ui.ORANGE,
    )
    manual.steps_grid(
        c,
        [
            ("管理対象", "答案画像の使用量と150GiB上限を確認します。", "info"),
            ("物理空き", "DataRootの実ドライブ空きと5GiB保護予備を見る。", "warn"),
            ("保持処理", "アプリの整理操作だけを使い、対象期間と件数を確認。", "safe"),
            ("履歴保持", "画像削除後も結果、訂正、得点、帳票、監査が残る。", "safe"),
        ],
        107,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        18,
        182,
        22,
        "Explorerから削除しない",
        "DataRoot/objectsやSQLite/WALを手動で移動・削除すると、参照と監査の整合性が壊れます。",
        tone="warn",
    )
    c.showPage()

    # 9. Jobs
    page_header(c, "08  処理状況", "失敗・確認待ち・長時間待機を切り分ける", 9)
    ui.place_image(
        c,
        SCREEN_DIR / "07-admin-jobs-current.png",
        14,
        124,
        182,
        128,
        border=ui.ORANGE,
    )
    manual.steps_grid(
        c,
        [
            ("優先順位", "failed、manual review、長時間retry waitingから確認。", "warn"),
            ("記録", "種別、対象ID、開始／更新時刻、コード、相関ID。", "info"),
            ("原因を直す", "AI接続、容量、入力、予算、外部429/5xxを確認。", "safe"),
            ("安全に再試行", "連打やDB編集をせず、同じ画面の用意された操作を使う。", "safe"),
        ],
        112,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        20,
        182,
        23,
        "同じ失敗が続くとき",
        "新しい取込を止め、発生時刻と相関IDを保存して技術担当者へ渡します。APIキーや実答案は添付しません。",
        tone="warn",
    )
    c.showPage()

    # 10. Windows service
    page_header(c, "09  Windows", "サービス・Firewall・healthを確認", 10)
    ui.paragraph(
        c,
        "アプリ画面を開けない場合は、データを触る前にサービス、ポート、Firewall、イベントログ、readinessの順で確認します。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    manual.command_box(
        c,
        "Get-Service OokiGrader.Host\n"
        "Get-NetTCPConnection -LocalPort 443 -State Listen\n"
        "Get-NetFirewallRule -DisplayName 'Ooki Grader HTTPS'\n"
        "Invoke-WebRequest 'https://ooki-grader.test/health/live' -UseBasicParsing\n"
        "Invoke-WebRequest 'https://ooki-grader.test/health/ready' -UseBasicParsing\n"
        "Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='Ooki Grader' } -MaxEvents 50",
        14,
        181,
        182,
        62,
    )
    manual.steps_grid(
        c,
        [
            ("Service", "Runningか。再起動後も遅延自動起動するか。", "safe"),
            ("Port", "443を別プロセスが所有していないか。", "warn"),
            ("Firewall", "RemoteAddressが承認済み職員CIDRだけか。", "info"),
            ("health", "liveとreadyを分け、readyの失敗項目を読む。", "safe"),
        ],
        166,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        21,
        182,
        22,
        "操作マーカーを削除しない",
        "restore.in-progressやmigration.in-progressがある場合は、直前の復旧境界に従いRepairで上書きしません。",
        tone="warn",
    )
    c.showPage()

    # 11. Certificate and peers
    page_header(c, "10  HTTPS", "無料ローカルCAと職員PCを保守", 11)
    ui.paragraph(
        c,
        "HTTPS証明書とWindowsコード署名は別です。今回のCA秘密鍵はホストに非エクスポート可能として保存されます。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    manual.steps_grid(
        c,
        [
            ("正式名", "https://ooki-grader.test/ とSAN、hostsを一致。", "safe"),
            ("ホスト", "ooki-grader.testは127.0.0.1へ管理対象行で解決。", "info"),
            ("職員PC", "同じ名前を固定ホストIPへ解決し、公開CAだけを信頼。", "safe"),
            ("警告時", "URL、時刻、CA、hosts、期限を直し、続行で回避しない。", "warn"),
            ("PC追加", "既存peer package一式をコピーしCMDを管理者実行。", "safe"),
            ("ホスト喪失", "同じCAを移せない。新CAを作り全PCで再設定。", "warn"),
        ],
        242,
        box_height=34,
        row_gap=8,
    )
    manual.command_box(
        c,
        "# 証明書の期限と拇印を確認\n"
        "Get-ChildItem Cert:\\LocalMachine\\My | `\n"
        "  Where-Object FriendlyName -like 'Ooki Grader*' | `\n"
        "  Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey",
        14,
        48,
        182,
        43,
    )
    ui.callout(
        c,
        14,
        20,
        182,
        21,
        "更新は60日前から",
        "同じ正式名で証明書を更新し、Repair後にホストと全職員PCで警告なしを確認してから旧証明書を退役します。",
        tone="info",
    )
    c.showPage()

    # 12. Cadence
    page_header(c, "11  定期運用", "毎日・毎週・毎月の確認を固定", 12)
    ui.rounded_box(c, 14, 181, 182, 67, fill=ui.MINT, stroke=ui.GREEN)
    ui.draw_text(c, 20, 235, "毎日", size=11, color=ui.DARK)
    manual.checklist(
        c,
        [
            "システム状態と対応が必要な項目",
            "AI接続、失敗ジョブ、保存容量",
            "最新バックアップの成功・完全検証時刻",
            "重大警告時は新規取込を止めて連絡",
        ],
        20,
        222,
        168,
        size=7.7,
    )
    ui.rounded_box(c, 14, 101, 87, 67, fill=ui.BLUE_PALE, stroke=ui.BLUE)
    ui.draw_text(c, 20, 155, "毎週", size=11, color=ui.DARK)
    manual.checklist(
        c,
        [
            "完全検証と復元計画確認",
            "失敗／再試行、予算、クォータ",
            "Windows Update、Defender、時刻、UPS",
            "一時パスワードと不要な権限",
        ],
        20,
        142,
        75,
        size=7.3,
    )
    ui.rounded_box(c, 109, 101, 87, 67, fill=ui.ORANGE_PALE, stroke=ui.ORANGE)
    ui.draw_text(c, 115, 155, "毎月", size=11, color=ui.DARK)
    manual.checklist(
        c,
        [
            "バックアップ世代と容量傾向",
            "証明書期限（60日前から計画）",
            "職員台帳との突合",
            "AIモデル・価格・条件・修正傾向",
        ],
        115,
        142,
        75,
        size=7.3,
    )
    ui.callout(
        c,
        14,
        48,
        88,
        34,
        "四半期",
        "隔離環境で復元訓練。RPO、RTO、欠損、資格情報再入力、担当者の手順を記録します。",
        tone="warn",
    )
    ui.callout(
        c,
        108,
        48,
        88,
        34,
        "記録",
        "実施者、日時、版、結果、次回期限を学校の承認済み運用台帳へ残します。秘密値は残しません。",
        tone="safe",
    )
    c.showPage()

    # 13. Upgrade
    page_header(c, "12  更新", "バックアップ後に別バージョンへ切替", 13)
    ui.paragraph(
        c,
        "現地持込の未署名版では、媒体管理と全チェックサムを再確認し、専用のAllowChecksumVerifiedOnSitePackageを使います。",
        14,
        256,
        182,
        size=8.7,
        leading=4.4,
        color=ui.MUTED,
    )
    manual.steps_grid(
        c,
        [
            ("告知・停止", "先生の操作を止め、保守時間と対象版を記録。", "warn"),
            ("バックアップ", "更新直前に作成し、完全検証と戻し方を確認。", "safe"),
            ("配布物検査", "新PackageRootのinventoryと全SHA-256を検査。", "safe"),
            ("事前health", "DB、容量、操作マーカー、現行版を確認。", "info"),
            ("Upgrade", "新しい版を別ディレクトリへ配置して切替。", "safe"),
            ("受入", "ready、ログイン、画像、AI少量試験、帳票を確認。", "safe"),
        ],
        242,
        box_height=34,
        row_gap=8,
    )
    manual.command_box(
        c,
        "$New = 'C:\\OokiGrader-Releases\\OokiGrader-0.2.0-win-x64'\n"
        "pwsh -File \"$New\\Upgrade-OokiGrader.ps1\" `\n"
        "  -PackageRoot $New -Version '0.2.0' `\n"
        "  -CurrentVersionRoot 'C:\\Program Files\\Ooki Grader\\versions\\0.1.0' `\n"
        "  -InstallRoot 'C:\\Program Files\\Ooki Grader' `\n"
        "  -DataRoot 'D:\\OokiGraderData' -BackupDestination 'E:\\OokiGraderBackup' `\n"
        "  -VerifiedBackupId '<26文字ID>' `\n"
        "  -VerifiedBackupRelativePath 'sets/2026/08/<同じID>' `\n"
        "  -VerifiedBackupManifestSha256 '<64桁SHA-256>' `\n"
        "  -MaintenanceConfirmed -OfflineConfirmed `\n"
        "  -FreshPreUpgradeBackupConfirmed `\n"
        "  -ReadyUri 'https://ooki-grader.test/health/ready' `\n"
        "  -AllowChecksumVerifiedOnSitePackage",
        14,
        44,
        182,
        75,
        size=5.4,
    )
    ui.callout(
        c,
        14,
        18,
        182,
        20,
        "失敗境界",
        "スキーマ変更後の失敗では旧版を無理に起動せず、サービス停止と検証済みバックアップの復元計画へ進みます。",
        tone="warn",
    )
    c.showPage()

    # 14. Repair
    page_header(c, "13  修復", "データを消さず構成を再適用", 14)
    ui.paragraph(
        c,
        "Service、ACL、証明書、Firewall、Production設定の破損時だけRepairを使います。通常のアプリ警告には使いません。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    manual.command_box(
        c,
        "pwsh -File 'C:\\OokiGrader-Setup\\OokiGrader-0.1.0-win-x64\\Repair-OokiGrader.ps1' `\n"
        "  -VersionRoot 'C:\\Program Files\\Ooki Grader\\versions\\0.1.0' `\n"
        "  -DataRoot 'D:\\OokiGraderData' `\n"
        "  -HostCertificatePath 'D:\\OokiGraderData\\certificates\\ooki-grader-host.pfx' `\n"
        "  -SchoolSubnet '192.168.10.0/24' -DnsName 'ooki-grader.test' `\n"
        "  -HttpsPort 443 -AllowChecksumVerifiedOnSitePackage",
        14,
        179,
        182,
        63,
    )
    manual.steps_grid(
        c,
        [
            ("対象を固定", "版、DataRoot、証明書、CIDR、正式名をmanifestと照合。", "info"),
            ("マーカー確認", "restore/migration in-progressがあればRepairを中止。", "warn"),
            ("再適用", "Service、ACL、証明書、設定、Firewallだけを安全に構成。", "safe"),
            ("検証", "Toolの読取healthと実HTTPS readinessの両方を確認。", "safe"),
        ],
        164,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        19,
        182,
        21,
        "データを直接修復しない",
        "SQLite、WAL、DataRoot/objects、操作マーカーを手動編集・削除しません。",
        tone="warn",
    )
    c.showPage()

    # 15. Restore
    page_header(c, "14  復元", "必ずオフライン・明示確認・隔離検証", 15)
    ui.paragraph(
        c,
        "画面の復元計画確認は読み取り専用です。実復元は保守時間にWindows担当者がサービスを止めて実行します。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    manual.steps_grid(
        c,
        [
            ("先生を停止", "新規操作なし、メンテナンス閲覧専用を確認。", "warn"),
            ("再検証", "Backup ID、相対パス、manifest SHA-256を二者照合。", "safe"),
            ("Service停止", "復元スクリプトは稼働サービスを暗黙停止しません。", "warn"),
            ("Restore", "Offline/Maintenance/ConfirmRestoreを明示。", "safe"),
            ("オフラインhealth", "DB、スキーマ、ロールバックスナップショットを確認。", "info"),
            ("資格情報", "別ホストならGeminiキーを再入力し、一括確認を完了。", "info"),
        ],
        242,
        box_height=33,
        row_gap=8,
    )
    manual.command_box(
        c,
        "$Maint = 'C:\\OokiGrader-Setup\\OokiGrader-0.1.0-win-x64'\n"
        "Stop-Service OokiGrader.Host\n"
        "pwsh -File \"$Maint\\Restore-OokiGrader.ps1\" `\n"
        "  -VersionRoot 'C:\\Program Files\\Ooki Grader\\versions\\0.1.0' `\n"
        "  -DataRoot 'D:\\OokiGraderData' `\n"
        "  -BackupDestination 'E:\\OokiGraderBackup' `\n"
        "  -BackupId '<26文字ID>' `\n"
        "  -BackupRelativePath 'sets/2026/08/<同じID>' `\n"
        "  -BackupManifestSha256 '<64桁SHA-256>' `\n"
        "  -MaintenanceConfirmed -OfflineConfirmed `\n"
        "  -AllowChecksumVerifiedOnSitePackage `\n"
        "  -ConfirmRestore '<同じID>'",
        14,
        43,
        182,
        69,
        size=5.4,
    )
    ui.callout(
        c,
        14,
        17,
        182,
        20,
        "成功後も自動再開しない",
        "サービス停止、復元マーカー、ロールバックスナップショットを保ち、承認済みの復元後ランブックで終結します。",
        tone="warn",
    )
    c.showPage()

    # 16. Uninstall
    page_header(c, "15  アンインストール", "アプリを退避し、学校データは保持", 16)
    ui.paragraph(
        c,
        "UninstallはServiceとFirewallを解除し、アプリを回復領域へ移します。DataRootとBackupRootは削除しません。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    manual.steps_grid(
        c,
        [
            ("利用停止", "先生がオフラインで新規処理がないことを確認。", "warn"),
            ("バックアップ", "最新成功と完全検証、保持義務を確認。", "safe"),
            ("Offline確認", "対象InstallRootとDataRootを記録して実行。", "info"),
            ("保持確認", "DataRoot、BackupRoot、職員PCのCA信頼は残る。", "safe"),
        ],
        242,
        box_height=36,
        row_gap=9,
    )
    manual.command_box(
        c,
        "pwsh -File 'C:\\OokiGrader-Setup\\OokiGrader-0.1.0-win-x64\\Uninstall-OokiGrader.ps1' `\n"
        "  -InstallRoot 'C:\\Program Files\\Ooki Grader' `\n"
        "  -DataRoot 'D:\\OokiGraderData' `\n"
        "  -OfflineConfirmed",
        14,
        108,
        182,
        47,
    )
    ui.callout(
        c,
        14,
        65,
        88,
        31,
        "回復可能",
        "アプリは回復領域へ退避。結果確認前に手動削除しません。",
        tone="safe",
    )
    ui.callout(
        c,
        108,
        65,
        88,
        31,
        "データ廃棄",
        "学校の記録保持・個人情報廃棄手順で別承認します。",
        tone="warn",
    )
    ui.callout(
        c,
        14,
        25,
        182,
        28,
        "職員PCの信頼設定",
        "CAやhosts行の撤去は全端末の利用終了とデータ移行を確認してから、別の管理作業として行います。",
        tone="info",
    )
    c.showPage()

    # 17. Troubleshooting
    page_header(c, "16  障害対応", "症状から最短の確認へ進む", 17)
    ui.rounded_box(c, 14, 65, 182, 184, fill=ui.WHITE, stroke=ui.BORDER)
    cases = [
        ("ログイン上限", "それ以上試さず15分待つ。ユーザー名・時刻を確認し、別管理者が必要なら再設定。"),
        ("AI接続失敗", "接続状態、キー、モデル、画像対応、外向きDNS/HTTPS、予算、クォータ。"),
        ("AI結果が不正", "受付開始・確定を止め、元画像、設定、モデル、プロンプト版、教師期待値を固定。"),
        ("証明書警告", "正式名、SAN、公開CA、hosts、期限、PC時刻。警告を無視しない。"),
        ("403 Origin", "IP直打ちや別名をやめ、生成された正式HTTPSショートカットを使用。"),
        ("容量警告", "新規取込を止め、物理空き、管理対象、保持処理、BackupRootを確認。"),
        ("Service停止", "イベントログ、証明書、ACL、設定、容量、操作マーカー。マーカー時はRepairしない。"),
        ("更新後停止", "旧版を無理に起動せず、更新境界と検証済みバックアップの復元計画へ。"),
        ("backup失敗", "保存先の接続、暗号化、権限、容量。SQLiteを手動コピーしない。"),
        ("職員PCだけ不可", "peer setup結果、LAN、Firewall CIDR、固定IP、CAとhosts行。"),
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
        "問い合わせに残す情報",
        "日時、役割、PC名、正式URL、対象ID、エラーコード、相関ID、再現手順、直前の更新。秘密値と実答案は通常連絡へ貼りません。",
        tone="warn",
    )
    c.showPage()

    # 18. Handover/acceptance
    page_header(c, "17  引き渡し", "運用責任者が再現できることを確認", 18)
    ui.paragraph(
        c,
        "設置担当者だけが知る手順を残さず、主管理者・副管理者・Windows担当者・バックアップ担当者の全員が自分の操作を実演します。",
        14,
        256,
        182,
        size=8.8,
        leading=4.5,
        color=ui.MUTED,
    )
    ui.rounded_box(c, 14, 74, 182, 168, fill=ui.WHITE, stroke=ui.BORDER)
    manual.checklist(
        c,
        [
            "版、物理配布元、全件チェックサム検査を記録した",
            "固定IP、正式URL、CIDR、DataRoot、BackupRoot、CA拇印を記録した",
            "サービス、Firewall、live、ready、Windows再起動を確認した",
            "全職員PCでpeer setup、HTTPS検査、ショートカットを確認した",
            "主管理者と副管理者、最小権限の職員を作った",
            "Geminiの一括確認と4機能の「利用できます」、予算を確認した",
            "手動バックアップ、完全検証、復元計画確認を実行した",
            "保存容量と保持後に残る履歴を確認した",
            "架空データでひな形、順番取込、確認、確定、帳票PDFを実演した",
            "隔離復元訓練の担当、期日、RPO、RTOを決めた",
            "更新、修復、復元でAllowChecksumVerifiedOnSitePackageを使う境界を共有した",
            "毎日・毎週・毎月の担当者と代行者を決めた",
            "教師ガイド、現地設置ガイド、本書を校内の管理場所へ保存した",
        ],
        20,
        229,
        168,
        size=7.45,
        leading=3.9,
        gap=1.8,
    )
    ui.callout(
        c,
        14,
        35,
        88,
        27,
        "今回の安全境界",
        "無料ローカルCA + 管理下の現地持込 + 全件チェックサム。HTTPや警告回避は使いません。",
        tone="safe",
    )
    ui.callout(
        c,
        108,
        35,
        88,
        27,
        "残る訓練",
        "故障、停電、外部AI停止、低容量、隔離復元を実機で定期的に練習します。",
        tone="warn",
    )

    c.showPage()

    # 19. Robust lists and durable bulk result export
    page_header(c, "18  帳票運用", "検索条件と一括出力ジョブを安全に管理", 19)
    ui.paragraph(
        c,
        "生徒・実施・ひな形・帳票の一覧は、複数語検索、完全一致フィルター、安定した並び替え、カーソルページングを共通で使います。一括出力は画面を閉じても継続する耐久ジョブです。",
        14,
        256,
        182,
        size=8.5,
        leading=4.3,
        color=ui.MUTED,
    )
    ui.place_image(
        c,
        CAPTURE_DIR / "40-reports-filter-sort.png",
        14,
        139,
        87,
        103,
        border=ui.GREEN,
    )
    ui.place_image(
        c,
        CAPTURE_DIR / "44-reports-bulk-ready.png",
        109,
        139,
        87,
        103,
        border=ui.BLUE,
    )
    manual.steps_grid(
        c,
        [
            ("対象上限", "1ジョブは100名・500結果・512MiB。超過時は条件を分割します。", "warn"),
            ("状態", "queued / rendering / verified / failed / superseded を確認します。", "info"),
            ("耐久性", "同時処理は同一職員2件・全体4件まで。URLの一括出力IDから進捗を復元します。", "safe"),
            ("配布前", "verifiedだけを取得し、PDF件数・manifest.csv・文字化けを抜き取り確認します。", "safe"),
        ],
        126,
        box_height=30,
        row_gap=7,
    )
    ui.callout(
        c,
        14,
        22,
        182,
        27,
        "失敗・対象更新・未割当",
        "部分ZIPや古いZIPは配布しません。氏名割当、確定状態、条件、容量、ジョブを確認し、先生に新しいプレビューから再実行してもらいます。ダウンロード済みZIPは共有PCに残しません。",
        tone="warn",
    )

    c.save()
    print(OUTPUT)


if __name__ == "__main__":
    build()
