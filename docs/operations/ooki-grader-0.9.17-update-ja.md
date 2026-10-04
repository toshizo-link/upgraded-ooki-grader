# Ooki Grader 0.9.17 更新手順

1. 採点・答案の取り込みを終え、Ooki Graderを設置したPCで[更新用ZIP](https://github.com/toshizo-link/upgraded-ooki-grader/releases/download/v0.9.17/OokiGrader-0.9.17-Windows-Host-Update.zip)をダウンロードします。
2. ZIPを右クリックして「すべて展開」を選びます。展開先の `01-Update-OokiGrader-Host.cmd` を右クリックし、「管理者として実行」を選びます。確認画面は「はい」を押します。
3. 更新先が `0.9.17` であることを確認し、入力を求められたら `UPDATE WITHOUT BACKUP` と入力してEnterを押します。
4. `Ooki Grader host update completed successfully.` と表示されたら、いつものURLで開き直します。
5. 名簿と過去の採点結果を開きます。「管理」→「AI設定」で `gemini-3.8-flash` を確認し、「再確認」を1回押します。保存済みのAPIキーを引き続き使用できます。
6. 接続が正常になったら、ひな形の生成画面で「失敗した項目を再試行」を押します。生成後は問題・正解・配点を原本と照合します。

ひな形作成・採点・再確認は3.8 Flash、氏名読み取りは3.5 Flash-Liteを使います。3.8の利用上限や一時的な混雑時は3.5へ切り替わり、利用できる状態になれば3.8へ戻ります。モデルを手動で切り替える必要はありません。

更新中はPCの電源を切らず、採点・取り込みを行わないでください。エラーが表示された場合は、画面の写真を添えてご連絡ください。

[更新手順PDF](https://github.com/toshizo-link/upgraded-ooki-grader/raw/refs/heads/main/output/pdf/OokiGrader-0.9.17-Update-Guide-JA.pdf)

[教師用ガイドブックPDF](https://github.com/toshizo-link/upgraded-ooki-grader/raw/refs/heads/main/output/pdf/OokiGrader-Teacher-Handbook-JA.pdf)
