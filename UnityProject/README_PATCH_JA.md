# ManaWeave 日本語HUDフォント修正

Unity 6.3 LTS向け / 2026-11-05-bundled-hud-fonts

旧版の `Noto Sans CJK JP` 読み込みエラーと、それに伴うHUD描画失敗へ対応する差分です。
OSのフォント名を検索する方式から、同梱のNoto Sans JP Regular/Boldを直接読み込む方式に変更しました。

## 適用手順

1. UnityのPlayを停止し、プロジェクトを保存してUnity Editorを閉じます。
2. 既存プロジェクトのコピーを取ります。独自編集したManaHud.csやProjectTools.csがある場合は必ず残してください。
3. このREADMEと同じ階層の `Assets` を、使用中のUnityプロジェクトの `Assets` と結合し、同名ファイルを置き換えます。
   `.meta` も含めてコピーしてください。既存のAssetsを丸ごと削除して置き換えないでください。
4. `<UnityProject>/Assets/Resources/ManaWeave/Fonts/UIJapanese-Regular.otf` と `UIJapanese-Bold.otf` があることを確認します。
   `Assets/Assets/...` にならないようにしてください。
5. Unity Editorを起動し、インポートとコンパイルの完了を待ちます。
6. `Tools > ManaWeave > Verify HUD Fonts` を実行します。Consoleに `ManaWeave HUD fonts verified` が出れば設定・代表字形の確認成功です。
7. Consoleの古いログをClearしてPlayします。ホットバー、ガイド、Tabのワークベンチ、日本語の入力欄を確認してください。

Package Managerからインストールするパッケージではありません。OSへのフォントインストールも不要です。
Unity Hubへ既存プロジェクトを追加し直す必要はありません。

## 主な変更

- 日本語Regular/BoldフォントとDynamic / Include Font Dataを設定した .meta を同梱。
- HUDのOSフォント名検索を廃止。GUIStyleをキャッシュし、GUI状態を例外時も復元。
- `Verify HUD Fonts` と専用2フォントのインポート設定維持処理を追加。
- 既存の `Verify Core and Model` / ManaWeaveのBuildメニューにもフォント検証を接続。
- フォントライセンスをAssets/StreamingAssetsへ同梱。

Core、Magic、モデル重み、シーン、保存形式は変更しません。

## 確認範囲

フォントのバイナリ、既存ソース文字の収録、SHA-256、メタ設定、ZIP構成とCRCを検査しています。
修正後のRuntimeは既存の簡略Unity APIスタブに対するC#コンパイルで警告0・エラー0でした。
Unity Editorでの実インポート・描画・PlayerビルドとFPS改善量は、この作業環境では未確認です。
「Gizmos」と表示される症状の解消も、ユーザー環境での表示確認が必要です。

詳細は `Documentation/FONT_FIX_2026-11-05.md`、検査記録は `Documentation/FONT_FIX_VERIFICATION.json` を参照してください。
この差分の適用では、以前の配布物の外側にあるREADMEや設計資料は自動更新されません。
全体の更新済み資料も必要な場合は、同時配布の `ManaWeave_Unity63_Prototype.zip` を参照してください。
