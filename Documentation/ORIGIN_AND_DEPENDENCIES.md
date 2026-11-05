# 作成元と依存関係

本プロジェクトのゲーム用C#、手続き地形、シェーダー、モデル実装、直接生成した重み、学習済みRuneLM重み、合成学習データ、テストは本依頼のために作成したものです。

以下は外部の基盤であり、「オリジナル」という説明に含めていません。

| 基盤 | 使用箇所 | 配布物に含まれるもの |
|---|---|---|
| Unity 6.3 LTS | Editor、描画、入力、Player | プロジェクト設定のみ。本体は同梱しません |
| .NET / C# | 言語・標準ライブラリ・独立検証 | 検証用プロジェクト。本体は同梱しません |
| PyTorch | 学習時のテンソル演算・自動微分 | 利用コードとバージョン指定。本体は同梱しません |
| NumPy | 直接重みの構成と数値処理 | 利用コード。本体は同梱しません |
| OSフォント | IMGUIの日本語表示 | フォントファイルを同梱しません |

外部の学習済みモデル、外部コーパス、画像・音楽・3D素材、アセットストア商品は含めていません。TransformerやDDA等のアルゴリズム自体を新発明と主張するものではありません。

## 参照した一次資料

- Unity 6000.3.26f1 リリース情報： https://unity.com/ja/releases/editor/whats-new/6000.3.26f1
- Unityの起動時コールバック： https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/runtimeinitializeonloadmethodattribute
- Unityのアセンブリ構成： https://docs.unity.com/en-us/engine/6000.3/manual/programming-environment/script-compilation/assembly-definition-files/assembly-definitions-intro
- ユーザー指定のWeight-Space Programming記事： https://rikiyat.github.io/blog/weight-space-programming/

上記の記事を本モデルの検証結果として扱っていません。本モデルの検証値は同梱の実行結果を参照してください。
