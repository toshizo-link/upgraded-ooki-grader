# School Manager 自動配信の設定・停止

答案を確定すると、その生徒の答案原本と採点結果をまとめたPDFが、School Managerの保護者宛てに配信されます。初回の連携設定を済ませた後は、普段どおり答案を確認し、確定してください。

## 1. 初回の連携設定

初回だけ設定します。Ooki GraderとSchool Managerのログイン情報を用意し、両方の名簿の生徒番号・氏名が一致していることを確認してください。School Managerでは対象生徒と保護者を関連付けます。

1. 更新用ZIPを展開します。その中の `OokiGrader-0.9.15-win-x64.zip` も展開します。
2. `Set-OokiGraderSchoolManager.ps1` があるフォルダーでPowerShellを開きます。
3. 次の利用URLとログインIDを実際の値に置き換えて実行します。Ooki Graderの利用URLは `https://` から入力します。先生が普段使う管理者アカウントを使用します。

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass `
  -File .\Set-OokiGraderSchoolManager.ps1 `
  -OokiGraderUrl '<Ooki Graderの利用URL>' `
  -AdminUsername '<Ooki GraderのログインID>' `
  -SchoolManagerUsername '<School ManagerのログインID>' `
  -Enable
```

4. 設定確認が表示されたら、対象URLと確認用の設定であることを確認して `Y` を入力します。その後、Ooki GraderとSchool Managerのパスワードを順に入力します。入力した文字は画面に表示されません。
5. 出力が `configured: true`、`enabled: true`、`dryRun: true` であることを確認します。この段階では送信されません。

## 2. 宛先を確認して自動配信を開始する

1. 配信する答案を取り込み、生徒番号・氏名・採点結果を確認して「答案を確定」します。
2. Ooki Graderに管理者としてログインしたブラウザーで、利用URLの末尾に次を付けて開きます。

```text
/api/v1/admin/school-manager/deliveries?limit=200
```

3. 配信候補を確認します。対象の `state` が `ready`、`lastDryRunAt` に日時が入り、`sentAt` と `lastErrorCode` が空欄であることを確認します。番号・氏名と保護者宛先の確認はこの処理で行われます。PDFのアップロードと送信はまだ行われません。
4. **待機中の候補がすべて配信してよい答案であることを確認します。** 実送信への切り替えは、待機中の候補全体に適用されます。候補が200件を超える場合や、宛先・エラーを確認できない場合は、画面を添えて開発担当に連絡してください。
5. 手順1のコマンドの末尾を `-Enable -LiveSending` に変更して実行します。確認が表示されたら、対象URLと実送信への切り替えを確認して `Y` を入力し、両方のパスワードを入力します。
6. 出力が `enabled: true`、`dryRun: false` であることを確認します。待機中の候補も配信が始まります。
7. School Managerの送信済みメッセージで、宛先、件名「採点結果のお知らせ」、添付PDFを確認します。保護者側でPDFを開けることも確認してください。

## 3. 毎日の返却

1. 答案を取り込み、生徒番号・氏名と採点結果を確認します。
2. 「答案を確定」します。確認を終えていない答案は確定しません。
3. School Managerの送信済みメッセージで配信結果を確認します。

自動配信の対象は、連携を有効にした後に確定する個人の結果です。クラス成績表は、この個人結果の自動配信には含まれません。

訂正するときはOoki Graderで答案を再開し、修正してから確定します。送信済みPDFは回収されないため、先にSchool Managerの配信記録を確認してください。

## 4. 自動配信を停止する

手順1のコマンドから `-Enable` と `-LiveSending` を外して実行します。確認が表示されたら対象URLと停止を確認して `Y` を入力し、両方のパスワードを入力します。出力が `enabled: false` であることを確認します。

未送信の候補は取り消されます。送信処理が始まっている候補は、School Managerの送信済みメッセージを確認してください。

配信状況に `message_send_outcome_unknown` が表示された場合も、School Managerの送信済みメッセージを確認してから再送を判断します。
