using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ManaWeave.Editor
{
    /// <summary>Checks the actual bundled source fonts, independently of OS font installation.</summary>
    public static class HudFontTools
    {
        internal const string RegularAsset = "Assets/Resources/ManaWeave/Fonts/UIJapanese-Regular.otf";
        internal const string BoldAsset = "Assets/Resources/ManaWeave/Fonts/UIJapanese-Bold.otf";
        private const string Sample = "MANAWEAVE 0123456789 魔法工房 詠唱を解析 この術式を装備して戻る 錬金術 科学 魔力 紅蓮の炎よ ✓ → ↑ ≥ − °C";

        [MenuItem("Tools/ManaWeave/HUDフォントを検証")]
        public static void VerifyFontAssets()
        {
            Verify("ManaWeave/Fonts/UIJapanese-Regular", RegularAsset);
            Verify("ManaWeave/Fonts/UIJapanese-Bold", BoldAsset);
            Debug.Log("ManaWeave：HUDフォントの検証に成功しました。同梱の通常・太字フォント、Dynamic、Include Font Data、代表文字を確認しました。" +
                      "実際の表示はPlayモードのGameビューで確認してください。");
        }

        private static void Verify(string resourcePath, string expectedAssetPath)
        {
            Font font = Resources.Load<Font>(resourcePath);
            if (font == null)
                throw new FileNotFoundException("HUDフォントが見つかりません。Fontsフォルダーと.metaファイルを復元してください：" + expectedAssetPath);
            string path = AssetDatabase.GetAssetPath(font);
            if (!string.Equals(path, expectedAssetPath, StringComparison.Ordinal))
                throw new InvalidOperationException("HUDフォントとして想定外のアセットが読み込まれました：" + path);
            var importer = AssetImporter.GetAtPath(path) as TrueTypeFontImporter;
            if (importer == null || !importer.includeFontData ||
                importer.fontTextureCase != FontTextureCase.Dynamic || !font.dynamic)
                throw new InvalidOperationException("HUDフォントにはDynamicとInclude Font Dataの有効化が必要です。次のファイルを再インポートしてください：" + path);
            foreach (char character in Sample)
            {
                if (!char.IsWhiteSpace(character) && !font.HasCharacter(character))
                    throw new InvalidOperationException("HUDフォントに文字 U+" + ((int)character).ToString("X4") + " がありません：" + path);
            }
        }

        internal static bool IsBundledFont(string path)
        {
            return string.Equals(path, RegularAsset, StringComparison.Ordinal) ||
                   string.Equals(path, BoldAsset, StringComparison.Ordinal);
        }
    }

    /// <summary>Keep only ManaWeave's two source-font assets self-contained on import.</summary>
    public sealed class ManaHudFontImporter : AssetPostprocessor
    {
        private void OnPreprocessAsset()
        {
            if (!HudFontTools.IsBundledFont(assetPath)) return;
            var importer = assetImporter as TrueTypeFontImporter;
            if (importer == null) return;
            importer.fontTextureCase = FontTextureCase.Dynamic;
            importer.includeFontData = true;
            importer.fontSize = 18;
            importer.fontReferences = new Font[0];
        }
    }
}
