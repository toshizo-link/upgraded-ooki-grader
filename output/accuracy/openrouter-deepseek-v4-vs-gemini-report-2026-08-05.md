# Ooki Grader OpenRouter DeepSeek V4 Flash / Gemini 3.5 Flash Lite 比較レポート

作成日: 2026-08-05  
対象: 日本語の手書き穴埋め答案を含む、Ooki Grader の画像ベースのテンプレート生成経路

## 結論

現行の Ooki Grader では **Gemini 3.5 Flash Lite の方が明確に適合している**。DeepSeek V4 Flash は OpenRouter 経由の構造化テキスト応答には成功したが、公式モデルメタデータ上も実API上も画像入力に対応していない。同じ日本語理科プリントを渡すと、推論前に HTTP 404 `No endpoints found that support image input` で拒否された。

したがって、画像から問題・解答欄・手書き文字を読み取る現在の同一タスクについて DeepSeek のモデル精度を採点することはできず、本番候補としては不合格である。Gemini 3.5 Flash Lite を既定のまま維持し、DeepSeek V4 Flash への切り替えや自動フォールバックは行わない。

OpenRouter 接続自体は実装対象とする。ただし、接続テストで画像入力と構造化出力の両方が合格した、別途精度検証済みのモデルだけを画像タスクに使用できるようにする。DeepSeek V4 Flash はテキスト専用候補として表示し、テンプレート生成・氏名転記・画像採点には選択できない。

## 同一実画像による結果

評価画像:

- ファイル: `codex-clipboard-01f23b28-0ef6-4a27-adb1-064dc8840451.png`
- SHA-256: `ff98af93d6b94156f48d6fcd931610e3ae7d0d731737aeeb44b05df0059d472f`
- サイズ: 752,106 bytes
- 内容: 日本語理科「光」。問題文中に11個の手書き穴埋め解答欄が混在する1ページ
- 期待解答: 光、太陽、光源、月、光源、かげ、直進、上下左右、反射、反射、反射

| 候補 | 画像入力 | 構造化出力 | 実画像結果 | 現行経路への適格性 |
|---|---:|---:|---:|---:|
| Gemini 3.5 Flash Lite | 対応 | 対応 | 6/6実行合格、66/66欄一致 | 合格 |
| OpenRouter `deepseek/deepseek-v4-flash` | 非対応 | テキストで成功 | 画像受付で拒否（推論未開始） | 不合格 |
| OpenRouter `deepseek/deepseek-v4-flash-0731` | 非対応 | テキストで成功 | 画像受付で拒否（推論未開始） | 不合格 |

DeepSeek は画像対応エンドポイントがなく推論自体が開始されなかったため、欄一致率や採点精度は算出していない。これは認識精度 0% を意味するのではなく、現在の画像ワークフローでは比較対象になれないという適格性判定である。

Gemini の比較値は、同一画像を「記入済み答案（生徒解答を模範解答として使わない）」と「模範解答入りテスト」の2モードで各3回、合計6回、アプリの実API経路で実行した既存の最終評価による。全実行で11欄、印字番号、順序、正答、空欄化、安全な一括確認、未公開状態が合格した。

## OpenRouter 実APIプローブ

プライバシーとルーティング条件:

- `provider.require_parameters = true`
- `provider.data_collection = "deny"`
- `provider.zdr = true`
- 高推論設定
- strict JSON Schema structured output
- APIキーはプロセス環境だけで使用し、証拠ファイルには保存していない

### 安定版 ID

- 要求モデル: `deepseek/deepseek-v4-flash`
- canonical slug: `deepseek/deepseek-v4-flash-20260423`
- メタデータ: input `text`、output `text`、1,048,576 context
- テキスト構造化プローブ: HTTP 200、Schema 合格
- 画像プローブ: HTTP 404、`No endpoints found that support image input`
- 証拠: `output/accuracy/openrouter-deepseek-v4-flash-capability-2026-08-05.json`

### 最新の固定スナップショット

- 要求モデル: `deepseek/deepseek-v4-flash-0731`
- canonical slug: `deepseek/deepseek-v4-flash-20260731`
- メタデータ: input `text`、output `text`、1,048,576 context
- テキスト構造化プローブ: HTTP 200、Schema 合格
- 画像プローブ: HTTP 404、`No endpoints found that support image input`
- 証拠: `output/accuracy/openrouter-deepseek-v4-flash-0731-capability-2026-08-05.json`

モデルの別スナップショットへの暗黙切り替えを避けるため、0731 を固定した場合も検証した。結果は安定版 ID と同じだった。

実装した `OpenRouterClient` の opt-in live contract test でも 0731 を固定し、同じキーをプロセス内だけで使用して再確認した。テキストの strict structured output、利用量情報、認証、モデル到達性は合格し、画像は `openrouter_image_not_supported` として安全に不合格へ分類された。テストは1/1成功し、キーや raw response を出力していない。

## 判定基準と切り替え判断

Ooki Grader の本番候補は、最低でも次をすべて満たす必要がある。

1. 日本語の実答案画像を直接受け取れる。
2. strict JSON Schema に従った応答を返せる。
3. 問題抽出・手書き転記・採点の実回帰セットで Gemini と同等以上である。
4. 接続テストと精度評価を合格し、要求モデルと実モデルを証拠に固定できる。
5. 学生データを扱うルーティングで、パラメータ要件・データ収集拒否・ZDR 要件を満たせる。

DeepSeek V4 Flash は2のみ合格し、最初の必須条件を満たさない。この時点で同一タスクの精度比較対象から除外する。テキスト化済みの問題だけを渡す比較は、OCR・レイアウト理解・手書き認識を Gemini 側に依存する別タスクになるため、エンドツーエンドの代替可否を示す証拠としては使わない。

## 運用判断

- 既定: Gemini 3.5 Flash Lite を維持
- DeepSeek V4 Flash: 画像タスクでは使用不可
- 自動切り替え: 無効
- OpenRouter: オプション接続として実装するが、画像タスクの有効化には capability probe と精度評価の合格を必須とする
- 再評価: OpenRouter に画像対応の候補モデルを設定したとき、同一画像・同一プロンプト・同一Schema・同一回数で実施する

## 公式仕様の確認先

- OpenRouter Models API: https://openrouter.ai/api/v1/models
- DeepSeek V4 Flash 0731: https://openrouter.ai/deepseek/deepseek-v4-flash-0731/api
- Structured Outputs: https://openrouter.ai/docs/guides/features/structured-outputs
- Image inputs: https://openrouter.ai/docs/guides/overview/multimodal/image-understanding
- Zero Data Retention: https://openrouter.ai/docs/guides/features/zdr
- Provider routing: https://openrouter.ai/docs/guides/routing/provider-selection

## 証拠と再実行

- Gemini 最終証拠: `output/accuracy/fill-blank-live-evidence-v1.8.3-v9-final-3x-2026-08-05.json`
- Gemini レポート: `output/accuracy/fill-in-template-generation-report-2026-08-05.md`
- DeepSeek 安定版証拠: `output/accuracy/openrouter-deepseek-v4-flash-capability-2026-08-05.json`
- DeepSeek 0731 証拠: `output/accuracy/openrouter-deepseek-v4-flash-0731-capability-2026-08-05.json`
- OpenRouter 評価器: `tools/probe-openrouter-deepseek.py`
