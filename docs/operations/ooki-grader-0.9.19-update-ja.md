# Ooki Grader 0.9.19：ホストの更新とスクマネの確認

### 1. 更新前に、以前のテスト分を確認する

School Managerの「送信済みメッセージ」で、以前のテスト分の宛先・時刻・添付を確認してください。送信済みか判断できないものは、未送信扱いにせず、そのまま残します。以前の答案を再確定したり再送したりする必要はありません。

### 2. ホストPCだけを更新する

1. 採点・取り込み・配信操作を終え、[Windowsホスト更新用ZIP](https://github.com/toshizo-link/upgraded-ooki-grader/releases/download/v0.9.19/OokiGrader-0.9.19-Windows-Host-Update.zip)をダウンロードします。
2. ZIPを右クリックして「すべて展開」。中の **`01-Update-OokiGrader-Host.cmd`** を右クリックして「管理者として実行」します。
3. 更新先が **0.9.19** であることとバックアップの状態を確認し、画面に従います。`UPDATE WITHOUT BACKUP` はデータベースをバックアップから復旧できない条件での更新です。判断できない場合は進めず、開発担当へご連絡ください。
4. `Ooki Grader host update completed successfully.` が出たら、いつものURLで名簿と過去の採点結果を確認します。教室PCの更新・IPアドレス変更・パスワードの再登録は不要です。

### 3. 以前の配信の状態を見る

管理者でOoki Graderにログインしたブラウザーで、普段の利用URLの末尾に `/api/v1/admin/school-manager/deliveries?limit=200` を付けて開きます。更新前から残っていた `sending` は、起動時に `failed`・`message_send_outcome_unknown`（送信結果不明）になります。これは「届いていない」という意味ではありません。この配信は自動再送しません。以前から待機していた**別の配信**には従来の設定が適用されます。

### 4. 新しい試験用の宛先で確認する

まず開発担当と試験用の生徒・保護者を決めます。確認用設定（`dryRun: true`）では宛先を確認するだけで、PDFや通知は送られません。実送信に切り替えると他の待機中の配信も対象になるため、待機分がすべて試験用と確認できなければ切り替えません。試験環境で新規のテスト1件を送り、School Managerの送信済みが1件であることと、試験用保護者側で添付PDFを開けることを確認します。[設定・停止手順](https://github.com/toshizo-link/upgraded-ooki-grader/blob/v0.9.19/docs/operations/school-manager-setup-client-ja.md)
