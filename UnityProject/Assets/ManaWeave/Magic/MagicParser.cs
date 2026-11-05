// Human language -> bounded, reviewable magic IR. World targeting and all effects
// remain in WorldSimulator; no model is allowed to edit world state directly.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ManaWeave.Core;

namespace ManaWeave.Magic
{
    public sealed class MagicParseResult
    {
        public bool Success;
        public string Error, Source, Canonical;
        public SpellOperation Operation;
        public int Radius;
        public float Power;
        public MaterialId Material;
        // Grammar-conditioned model probability; NOT a calibrated semantic guarantee.
        public float Confidence;
    }

    public sealed class MagicParser
    {
        private readonly RuneLanguageModel model;
        private readonly Func<string, MagicParseResult> directInterpreter;
        public string LearnedModelStatus { get; private set; }
        public string ModelStatus { get; private set; }
        public const float MinimumConfidence = .72f;

        public MagicParser(byte[] weights, Func<string, MagicParseResult> directInterpreter = null)
        {
            this.directInterpreter = directInterpreter;
            try
            {
                model = new RuneLanguageModel(weights);
                LearnedModelStatus = "RuneLM: " + model.ParameterCount.ToString("N0", CultureInfo.InvariantCulture) + " trained parameters / original weights";
            }
            catch (Exception e)
            {
                if (!(e is ArgumentException || e is System.IO.IOException || e is System.IO.InvalidDataException || e is OverflowException)) throw;
                LearnedModelStatus = "RuneLM unavailable: " + e.Message;
            }
            ModelStatus = directInterpreter == null ? LearnedModelStatus : "Direct weight interpreter active; " + LearnedModelStatus;
        }

        public MagicParseResult Parse(string text)
        {
            string normalized;
            MagicParseResult early = Prepare(text, out normalized);
            if (early != null) return early;
            MagicParseResult dsl = TryDsl(normalized);
            if (dsl != null) return dsl;
            string invalid = ValidateNatural(normalized);
            if (invalid != null) return Fail(invalid, "Input validation");
            if (directInterpreter != null)
            {
                MagicParseResult direct = directInterpreter(normalized);
                if (direct != null) return direct;
            }
            return ParseModel(normalized);
        }

        // Explicitly selects the learned original Transformer, bypassing optional
        // direct-weight routing. The same DSL/input guards still apply.
        public MagicParseResult ParseLearned(string text)
        {
            string normalized;
            MagicParseResult early = Prepare(text, out normalized);
            if (early != null) return early;
            MagicParseResult dsl = TryDsl(normalized);
            if (dsl != null) return dsl;
            string invalid = ValidateNatural(normalized);
            return invalid == null ? ParseModel(normalized) : Fail(invalid, "Input validation");
        }

        public static string Normalize(string text)
        {
            return Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(), @"\s+", " ").TrimEnd('。', '.', '!', '！');
        }

        private static MagicParseResult Prepare(string text, out string normalized)
        {
            normalized = "";
            if (String.IsNullOrWhiteSpace(text)) return Fail("Enter one spell or an explicit DSL command.", "Input validation");
            if (text.Length > 240) return Fail("Command too long. Use one short instruction.", "Input validation");
            if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
                return Fail("One instruction per submission; multiline commands are unsupported.", "Input validation");
            normalized = Normalize(text);
            if (normalized.Length == 0) return Fail("Command is empty.", "Input validation");
            return null;
        }

        private static string ValidateNatural(string text)
        {
            if (text.Length > 80) return "The pilot language model accepts at most 80 characters. Use the explicit DSL.";
            if (Regex.IsMatch(text, @"[0-9=;?]")) return "Numeric quantities, questions, or compound commands require the explicit DSL.";
            string[] denied = { "しない", "するな", "ないで", "せず", "禁止", "やめ", "以外", "ただし", "だけ", "なら", "もし",
                "そして", "かつ", "してから", "その後", "同時", "すべて", "全部", "壊さず", "除い", "以降", "まで", "しつつ" };
            foreach (string word in denied)
                if (text.IndexOf(word, StringComparison.Ordinal) >= 0)
                    return "Negation, conditions, exceptions and compound spells are not represented by this prototype IR.";
            if (Regex.IsMatch(text, @"\b(not|don't|never|without|except|unless|if|and|or|then|while|all|until|but)\b"))
                return "Negation, conditions, exceptions and compound spells are not represented by this prototype IR.";
            string[] actionPatterns = {
                @"熱して|加熱|温め|熱を加|\b(heat|warm)\b|raise the temperature",
                @"冷や|冷却|熱を取り除|温度を下げ|\b(cool|chill)\b|lower the temperature|remove heat",
                @"掘って|掘削|くり抜|削って|\b(excavate|dig)\b|hollow out|carve out",
                @"建て|置いて|作って|構築|\b(build|place|construct|create)\b",
                @"充填|蓄え|魔力を注|補給|\b(charge|store|infuse|recharge)\b",
                @"錬成|錬金|合金|\b(transmute|alchemize|alchemy|alloy)\b"
            };
            int kinds = 0;
            foreach (string pattern in actionPatterns) if (Regex.IsMatch(text, pattern)) kinds++;
            // 'remove heat' contains heat, so ignore the generic heat category there.
            if (text.Contains("remove heat") && kinds > 1) kinds--;
            if (kinds > 1) return "More than one operation detected. Analyze and equip each spell separately.";
            bool small = Regex.IsMatch(text, @"小さ|小規模|狭い|\b(gently|small|low)\b");
            bool large = Regex.IsMatch(text, @"広範囲|大規模|大き|\b(strongly|large|high)\b");
            if (small && large) return "Conflicting size instructions. Specify one area scale.";
            return null;
        }

        private MagicParseResult ParseModel(string normalized)
        {
            const string source = "RuneLM / trained Transformer";
            if (model == null) return Fail("Trained model unavailable. Explicit DSL remains available.", source);
            try
            {
                float confidence;
                string[] tokens = model.Generate(model.EncodePrompt(normalized), out confidence);
                if (tokens.Length < 1 || tokens[0] == "@reject")
                    return Fail("The learned model rejected this command. Use a supported single action or explicit DSL.", source, confidence);
                if (tokens.Length != 5 || tokens[4] != "<eos>" || confidence < MinimumConfidence)
                    return Fail("Interpretation is uncertain. Rephrase or use the explicit DSL.", source, confidence);
                SpellOperation op;
                if (!Enum.TryParse<SpellOperation>(tokens[0].Substring(1), true, out op))
                    return Fail("Unknown model operation.", source, confidence);
                int radius; float power; MaterialId material;
                if (!Int32.TryParse(tokens[1].Substring(2), out radius) ||
                    !Single.TryParse(tokens[2].Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out power) ||
                    !Enum.TryParse<MaterialId>(tokens[3].Substring(1), true, out material))
                    return Fail("Invalid generated IR.", source, confidence);
                if (op != SpellOperation.Build && Regex.IsMatch(normalized, @"水|氷|鉄|結晶|木材|空気|敵|\b(water|ice|wood|stone|iron|crystal|enemy)\b"))
                    return Fail("Named-material target filters are not supported. This prototype always targets the crosshair.", source, confidence);
                string mismatch = ValidateLearnedMeaning(normalized, op, radius, power, material);
                if (mismatch != null) return Fail(mismatch, source, confidence);
                return Valid(op, radius, power, material, source, confidence);
            }
            catch (ArgumentException e) { return Fail(e.Message, source); }
        }

        // Reject disagreement with the small supported vocabulary. This is an
        // independent acceptance gate, not a fallback that rewrites neural output.
        // It prevents a confident model from turning "cool" into "excavate".
        private static string ValidateLearnedMeaning(string text, SpellOperation op, int radius, float power, MaterialId material)
        {
            string[] patterns = {
                @"熱して|加熱|温め|熱を加|(?<!remove )\b(heat|warm)\b|raise the temperature",
                @"冷や|冷却|熱を取り除|温度を下げ|\b(cool|chill)\b|lower the temperature|remove heat",
                @"掘って|掘削|くり抜|削って|\b(excavate|dig)\b|hollow out|carve out",
                @"建て|置いて|作って|構築|\b(build|place|construct|create)\b",
                @"充填|蓄え|魔力を注|補給|\b(charge|store|infuse|recharge)\b",
                @"錬成|錬金|合金|\b(transmute|alchemize|alchemy|alloy)\b"
            };
            SpellOperation[] ops = { SpellOperation.Heat, SpellOperation.Cool, SpellOperation.Excavate,
                SpellOperation.Build, SpellOperation.Charge, SpellOperation.Transmute };
            int count = 0; SpellOperation cue = SpellOperation.Heat;
            for (int i = 0; i < patterns.Length; i++)
                if (Regex.IsMatch(text, patterns[i])) { count++; cue = ops[i]; }
            if (count != 1 || cue != op)
                return "The learned interpretation disagrees with the supported action vocabulary. Use explicit DSL.";
            bool small = Regex.IsMatch(text, @"小さ|小規模|狭い|\b(gently|small|low)\b");
            bool large = Regex.IsMatch(text, @"広範囲|大規模|大き|\b(strongly|large|high)\b");
            int size = small ? 1 : (large ? 3 : 2);
            int expectedRadius = op == SpellOperation.Heat || op == SpellOperation.Cool ? size : Math.Max(1, size - 1);
            float expectedPower = op == SpellOperation.Heat || op == SpellOperation.Cool ?
                (size == 1 ? 50000f : (size == 3 ? 300000f : 120000f)) :
                (op == SpellOperation.Charge ? (size == 1 ? 20f : (size == 3 ? 80f : 50f)) : 0f);
            if (radius != expectedRadius || power != expectedPower)
                return "The learned area or strength disagrees with the supported instruction. Use explicit DSL.";
            if (op == SpellOperation.Build)
            {
                string[] materials = { @"石|\bstone\b", @"木|\bwood\b", @"鉄|\biron\b", @"結晶|\bcrystal\b" };
                MaterialId[] ids = { MaterialId.Stone, MaterialId.Wood, MaterialId.Iron, MaterialId.Crystal };
                int names = 0; MaterialId expected = MaterialId.Stone;
                for (int i = 0; i < materials.Length; i++)
                    if (Regex.IsMatch(text, materials[i])) { names++; expected = ids[i]; }
                if (names != 1 || expected != material)
                    return "The learned build material disagrees with the command. Specify one material explicitly.";
            }
            return null;
        }

        private static MagicParseResult TryDsl(string text)
        {
            bool starts = Regex.IsMatch(text, @"^(heat|cool|excavate|build|charge|transmute)(\s+[a-z]+\s*=|$)");
            if (!starts)
                return text.Contains("=") ? Fail("Unknown DSL operation. Use heat/cool/excavate/build/charge/transmute.", "Explicit DSL") : null;
            string[] parts = Regex.Split(text, @"\s+");
            SpellOperation operation;
            if (!Enum.TryParse<SpellOperation>(parts[0], true, out operation)) return Fail("Unknown operation.", "Explicit DSL");
            int radius = operation == SpellOperation.Heat || operation == SpellOperation.Cool ? 2 : 1;
            float power = operation == SpellOperation.Heat || operation == SpellOperation.Cool ? 120000f : (operation == SpellOperation.Charge ? 50f : 0f);
            MaterialId material = MaterialId.Stone;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < parts.Length; i++)
            {
                var pair = parts[i].Split('=');
                if (pair.Length != 2 || !seen.Add(pair[0])) return Fail("Use unique key=value fields without spaces around '='.", "Explicit DSL");
                if (pair[0] == "r")
                {
                    if (!Int32.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out radius) || radius < 1 || radius > 5)
                        return Fail("Radius must be an integer from 1 to 5.", "Explicit DSL");
                }
                else if (pair[0] == "power")
                {
                    if (operation != SpellOperation.Heat && operation != SpellOperation.Cool && operation != SpellOperation.Charge)
                        return Fail("This operation does not accept power; its cost is set by the world laws.", "Explicit DSL");
                    if (!Single.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out power) ||
                        Single.IsNaN(power) || Single.IsInfinity(power) || power <= 0 ||
                        power > (operation == SpellOperation.Charge ? 500f : 2000000f))
                        return Fail("Power out of bounds: 0 < energy <= 2,000,000 J; charge <= 500 MU.", "Explicit DSL");
                }
                else if (pair[0] == "material")
                {
                    if (operation != SpellOperation.Build) return Fail("Only build accepts a material field.", "Explicit DSL");
                    if (!Enum.TryParse<MaterialId>(pair[1], true, out material) || !Enum.IsDefined(typeof(MaterialId), material) ||
                        material == MaterialId.Air || material == MaterialId.Steam || Regex.IsMatch(pair[1], @"^[0-9]"))
                        return Fail("Unknown or unsupported build material.", "Explicit DSL");
                }
                else return Fail("Unknown DSL field: " + pair[0], "Explicit DSL");
            }
            return Valid(operation, radius, power, material, "Explicit DSL", 1f);
        }

        private static MagicParseResult Valid(SpellOperation op, int radius, float power, MaterialId material, string source, float confidence)
        {
            if (radius < 1 || radius > 5 || Single.IsNaN(power) || Single.IsInfinity(power))
                return Fail("Generated parameters failed bounds validation.", source, confidence);
            string canonical = op.ToString().ToLowerInvariant() + " r=" + radius.ToString(CultureInfo.InvariantCulture);
            if (op == SpellOperation.Heat || op == SpellOperation.Cool || op == SpellOperation.Charge)
                canonical += " power=" + power.ToString("0.###", CultureInfo.InvariantCulture);
            if (op == SpellOperation.Build) canonical += " material=" + material.ToString().ToLowerInvariant();
            return new MagicParseResult { Success = true, Operation = op, Radius = radius, Power = power,
                Material = material, Source = source, Canonical = canonical, Error = "", Confidence = confidence };
        }

        private static MagicParseResult Fail(string error, string source, float confidence = 0f)
        {
            return new MagicParseResult { Success = false, Error = error, Source = source, Canonical = "", Confidence = confidence };
        }
    }
}
