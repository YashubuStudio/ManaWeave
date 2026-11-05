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
        private const string Sample = "MANAWEAVE 0123456789 魔法 錬金術 科学 魔力 目の前を小さく熱して ✓ → ↑ ≥ °C";

        [MenuItem("Tools/ManaWeave/Verify HUD Fonts")]
        public static void VerifyFontAssets()
        {
            Verify("ManaWeave/Fonts/UIJapanese-Regular", RegularAsset);
            Verify("ManaWeave/Fonts/UIJapanese-Bold", BoldAsset);
            Debug.Log("ManaWeave HUD fonts verified: bundled Regular/Bold, Dynamic, Include Font Data, " +
                      "and sample glyphs. Check Game view in Play Mode to confirm rendering.");
        }

        private static void Verify(string resourcePath, string expectedAssetPath)
        {
            Font font = Resources.Load<Font>(resourcePath);
            if (font == null)
                throw new FileNotFoundException("ManaWeave HUD font missing. Restore the Fonts folder and its .meta files: " + expectedAssetPath);
            string path = AssetDatabase.GetAssetPath(font);
            if (!string.Equals(path, expectedAssetPath, StringComparison.Ordinal))
                throw new InvalidOperationException("ManaWeave HUD font resolved to an unexpected asset: " + path);
            var importer = AssetImporter.GetAtPath(path) as TrueTypeFontImporter;
            if (importer == null || !importer.includeFontData ||
                importer.fontTextureCase != FontTextureCase.Dynamic || !font.dynamic)
                throw new InvalidOperationException("ManaWeave HUD font must use Dynamic and Include Font Data. Reimport: " + path);
            foreach (char character in Sample)
            {
                if (!char.IsWhiteSpace(character) && !font.HasCharacter(character))
                    throw new InvalidOperationException("ManaWeave HUD font is missing U+" + ((int)character).ToString("X4") + ": " + path);
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
