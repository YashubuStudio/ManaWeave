using System;
using System.Collections.Generic;
using System.Text;

namespace ManaWeave.Core
{
    /// <summary>
    /// Authored game efficiency: longer, varied chants reduce manifestation work.
    /// The transfer of mana and material reagents are never discounted by this factor.
    /// </summary>
    public static class ChantEconomy
    {
        public const int MaximumInputLength = 512;
        public const int MaximumEffectiveLength = 120;
        public const float MinimumMultiplier = .4f;

        public static int EffectiveLength(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumInputLength) return 0;
            try { text = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(); }
            catch (ArgumentException) { return 0; }
            if (text.Length > MaximumInputLength) return 0;
            var pairs = new HashSet<int>();
            int previous = -1, characters = 0;
            foreach (char c in text)
            {
                // Numbers, whitespace, punctuation and symbol padding do not buy efficiency.
                if (!char.IsLetter(c) || "零〇一二三四五六七八九十百千万億兆壱弐参肆伍陸漆柒捌玖拾佰阡仟萬".IndexOf(c) >= 0) continue;
                if (previous >= 0) pairs.Add((previous << 16) | c);
                previous = c; characters++;
            }
            // Repeating the same phrase adds at most its boundary pair once, rather than its raw length.
            return characters == 0 ? 0 : Math.Min(MaximumEffectiveLength, pairs.Count + 1);
        }

        public static float Multiplier(string text)
        {
            return Math.Max(MinimumMultiplier, 1f / (1f + EffectiveLength(text) / 80f));
        }
    }
}
