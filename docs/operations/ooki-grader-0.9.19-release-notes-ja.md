# Ooki Grader 0.9.19：School Managerの配信が「送信中」のまま残る問題を修正

画面待ちのタイムアウトを配信の失敗として記録します。送信操作の後に応答が途切れた場合や、ホストの停止・更新によって送信結果を確認できなかった場合は「送信結果不明」として記録し、自動再送しません。過去の配信を送信済み・未送信のどちらかに推定する変更ではありません。

**ダウンロード：** [Windowsホスト更新用ZIP](https://github.com/toshizo-link/upgraded-ooki-grader/releases/download/v0.9.19/OokiGrader-0.9.19-Windows-Host-Update.zip) ／ [更新手順と試験方法](https://github.com/toshizo-link/upgraded-ooki-grader/blob/v0.9.19/docs/operations/ooki-grader-0.9.19-update-ja.md)

以前のテスト分を再送せず、まずSchool Managerの送信済みメッセージを確認してください。実際に届いたかどうかはSchool Manager側で照合します。実サービスの保護者宛ての送信・受信は今回の開発環境での検証には含みません。
