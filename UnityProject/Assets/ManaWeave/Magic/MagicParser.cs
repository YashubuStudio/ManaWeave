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
        public const int MaxInputLength = 512;
        public const int MaxLearnedInputLength = 80;
        public const string LearnedSourceLabel = "短文解析";
        public const string DslSourceLabel = "数値指定";
        private readonly RuneLanguageModel model;
        private readonly Func<string, MagicParseResult> directInterpreter;
        public string LearnedModelStatus { get; private set; }
        public string ModelStatus { get; private set; }
        public bool LearnedModelAvailable { get { return model != null; } }
        public const float MinimumConfidence = .72f;

        public MagicParser(byte[] weights, Func<string, MagicParseResult> directInterpreter = null)
        {
            this.directInterpreter = directInterpreter;
            try
            {
                model = new RuneLanguageModel(weights);
                LearnedModelStatus = "学習型短文モデル: " + model.ParameterCount.ToString("N0", CultureInfo.InvariantCulture) + " 学習済みパラメータ（独自重み）";
            }
            catch (Exception e)
            {
                if (!(e is ArgumentException || e is System.IO.IOException || e is System.IO.InvalidDataException || e is OverflowException)) throw;
                LearnedModelStatus = "学習型短文モデルを読み込めません: " + e.Message;
            }
            ModelStatus = directInterpreter == null ? LearnedModelStatus : "詠唱解析を使用中 / " + LearnedModelStatus;
        }

        public MagicParseResult Parse(string text)
        {
            string normalized;
            MagicParseResult early = Prepare(text, out normalized);
            if (early != null) return early;
            MagicParseResult dsl = TryDsl(normalized);
            if (dsl != null) return dsl;
            string invalid = ValidateNatural(normalized, directInterpreter == null);
            if (invalid != null) return Fail(invalid, "入力確認");
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
            string invalid = ValidateNatural(normalized, true);
            return invalid == null ? ParseModel(normalized) : Fail(invalid, "入力確認");
        }

        public static string Normalize(string text)
        {
            return Regex.Replace((text ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(), @"\s+", " ").TrimEnd('。', '.', '!', '！');
        }

        private static MagicParseResult Prepare(string text, out string normalized)
        {
            normalized = "";
            if (String.IsNullOrWhiteSpace(text)) return Fail("詠唱を入力してください。例文ボタンからも選べます。", "入力確認");
            if (text.Length > MaxInputLength) return Fail("入力は " + MaxInputLength + " 文字以内にしてください。末尾を省略せず解析を中止しました。", "入力確認");
            try { normalized = Normalize(text); }
            catch (ArgumentException) { return Fail("入力に扱えない文字が含まれています。詠唱を入力し直してください。", "入力確認"); }
            if (normalized.Length > MaxInputLength) return Fail("文字の表記を揃えた後の入力が " + MaxInputLength + " 文字を超えています。短くしてください。", "入力確認");
            if (normalized.Length == 0) return Fail("詠唱が空です。発動指示を入力してください。", "入力確認");
            return null;
        }

        private static string ValidateNatural(string text, bool forLearnedModel)
        {
            if (forLearnedModel && text.Length > MaxLearnedInputLength)
                return "短文解析は " + MaxLearnedInputLength + " 文字までです。長い詠唱には『詠唱解析』を選んでください。";
            if (Regex.IsMatch(text, @"[0-9=;?]")) return "数値指定・質問・複合命令には対応していません。数値を指定する場合は数値指定の例を使ってください。";
            string[] denied = { "しない", "するな", "ないで", "せず", "禁止", "やめ", "以外", "ただし", "だけ", "なら", "もし",
                "そして", "かつ", "してから", "その後", "同時", "すべて", "全て", "全部", "壊さず", "除い", "以降", "まで", "しつつ",
                "ない", "なかれ", "場合", "したら", "すれば", "であれば", "繰り返", "何度", "二回", "二度", "毎秒",
                "無限", "永久", "永遠", "必ず", "絶対", "のみ", "命中", "接触", "追尾" };
            foreach (string word in denied)
                if (text.IndexOf(word, StringComparison.Ordinal) >= 0)
                    return "否定・条件・例外・対象の選別・複数の効果は一つの術式で表せません。効果を一つずつ指定してください。";
            if (Regex.IsMatch(text, @"\b(not|don't|never|without|except|unless|if|and|or|then|while|all|until|but|twice|repeat|only|always|infinite)\b"))
                return "否定・条件・例外・対象の選別・複数の効果は一つの術式で表せません。効果を一つずつ指定してください。";
            // Direct chants are interpreted by the numeric WSP network. Its
            // full-coverage tokenizer and rejection gates also count repeated
            // actions, require one construction material and check invocations.
            if (!forLearnedModel) return null;
            string[] actionPatterns = {
                @"熱して|加熱|温め|熱を加|(?<!remove )\b(heat|warm)\b|raise the temperature",
                @"冷や|冷却|熱を取り除|温度を下げ|\b(cool|chill)\b|lower the temperature|remove heat",
                @"掘って|掘削|くり抜|削って|\b(excavate|dig)\b|hollow out|carve out",
                @"建て|置いて|作って|構築|\b(build|place|construct|create)\b",
                @"充填|蓄え|魔力を注|補給|\b(charge|store|infuse|recharge)\b",
                @"錬成|錬金|合金|\b(transmute|alchemize|alchemy|alloy)\b"
            };
            int kinds = 0, requests = 0;
            foreach (string pattern in actionPatterns)
            {
                int matches = Regex.Matches(text, pattern).Count;
                if (matches > 0) kinds++;
                requests += matches;
            }
            if (kinds > 1) return "効果が複数あります。一つずつ解析して装備してください。";
            if (requests > 1) return "発動指示が複数あります。同じ効果も一つずつ解析してください。";
            bool small = Regex.IsMatch(text, @"小さ|小規模|狭い|\b(gently|small|low)\b");
            bool large = Regex.IsMatch(text, @"広範囲|大規模|大き|\b(strongly|large|high)\b");
            if (small && large) return "範囲の指定が矛盾しています。小規模・標準・広範囲のうち一つを選んでください。";
            return null;
        }

        private MagicParseResult ParseModel(string normalized)
        {
            const string source = LearnedSourceLabel;
            if (model == null) return Fail("学習型短文モデルを読み込めません。数値指定は利用できます。", source);
            try
            {
                float confidence;
                string[] tokens = model.Generate(model.EncodePrompt(normalized), out confidence);
                if (tokens.Length < 1 || tokens[0] == "@reject")
                    return Fail("短文モデルがこの指示を解釈できませんでした。例文を使うか、詠唱解析へ切り替えてください。", source, confidence);
                if (tokens.Length != 5 || tokens[4] != "<eos>" || confidence < MinimumConfidence)
                    return Fail("解釈を確定できません。言い換えるか、数値指定を使ってください。", source, confidence);
                SpellOperation op;
                if (!Enum.TryParse<SpellOperation>(tokens[0].Substring(1), true, out op))
                    return Fail("短文モデルが未対応の効果を出力しました。", source, confidence);
                int radius; float power; MaterialId material;
                if (!Int32.TryParse(tokens[1].Substring(2), out radius) ||
                    !Single.TryParse(tokens[2].Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out power) ||
                    !Enum.TryParse<MaterialId>(tokens[3].Substring(1), true, out material))
                    return Fail("生成された術式の形式が正しくありません。", source, confidence);
                if (op != SpellOperation.Build && Regex.IsMatch(normalized, @"水|氷|鉄|結晶|木材|空気|敵|\b(water|ice|wood|stone|iron|crystal|enemy)\b"))
                    return Fail("素材や相手を選別する指定には対応していません。対象は現在の照準先です。", source, confidence);
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
                return "入力の効果と短文モデルの解釈が一致しません。詠唱解析か数値指定を使ってください。";
            bool small = Regex.IsMatch(text, @"小さ|小規模|狭い|\b(gently|small|low)\b");
            bool large = Regex.IsMatch(text, @"広範囲|大規模|大き|\b(strongly|large|high)\b");
            int size = small ? 1 : (large ? 3 : 2);
            int expectedRadius = op == SpellOperation.Heat || op == SpellOperation.Cool ? size : Math.Max(1, size - 1);
            float expectedPower = op == SpellOperation.Heat || op == SpellOperation.Cool ?
                (size == 1 ? 50000f : (size == 3 ? 300000f : 120000f)) :
                (op == SpellOperation.Charge ? (size == 1 ? 20f : (size == 3 ? 80f : 50f)) : 0f);
            if (radius != expectedRadius || power != expectedPower)
                return "入力の範囲・出力と短文モデルの解釈が一致しません。詠唱解析か数値指定を使ってください。";
            if (op == SpellOperation.Build)
            {
                string[] materials = { @"石|\bstone\b", @"木|\bwood\b", @"鉄|\biron\b", @"結晶|\bcrystal\b" };
                MaterialId[] ids = { MaterialId.Stone, MaterialId.Wood, MaterialId.Iron, MaterialId.Crystal };
                int names = 0; MaterialId expected = MaterialId.Stone;
                for (int i = 0; i < materials.Length; i++)
                    if (Regex.IsMatch(text, materials[i])) { names++; expected = ids[i]; }
                if (names != 1 || expected != material)
                    return "入力した素材と短文モデルの解釈が一致しません。一つの素材を明示してください。";
            }
            return null;
        }

        private static MagicParseResult TryDsl(string text)
        {
            bool starts = Regex.IsMatch(text, @"^(heat|cool|excavate|build|charge|transmute)(\s+[a-z]+\s*=|$)");
            if (!starts)
                return text.Contains("=") ? Fail("数値指定の効果名が不明です。加熱 heat・冷却 cool・掘削 excavate・建築 build・充填 charge・錬成 transmute を使います。", DslSourceLabel) : null;
            string[] parts = Regex.Split(text, @"\s+");
            SpellOperation operation;
            if (!Enum.TryParse<SpellOperation>(parts[0], true, out operation)) return Fail("未対応の効果です。", DslSourceLabel);
            int radius = operation == SpellOperation.Heat || operation == SpellOperation.Cool ? 2 : 1;
            float power = operation == SpellOperation.Heat || operation == SpellOperation.Cool ? 120000f : (operation == SpellOperation.Charge ? 50f : 0f);
            MaterialId material = MaterialId.Stone;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < parts.Length; i++)
            {
                var pair = parts[i].Split('=');
                if (pair.Length != 2 || !seen.Add(pair[0])) return Fail("項目=値の形で指定してください。同じ項目の重複や、=の前後の空白は使えません。", DslSourceLabel);
                if (pair[0] == "r")
                {
                    if (!Int32.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out radius) || radius < 1 || radius > 5)
                        return Fail("半径 r は1～5の整数で指定してください。", DslSourceLabel);
                }
                else if (pair[0] == "power")
                {
                    if (operation != SpellOperation.Heat && operation != SpellOperation.Cool && operation != SpellOperation.Charge)
                        return Fail("この効果では出力 power を指定しません。消費魔力は世界法則で決まります。", DslSourceLabel);
                    if (!Single.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out power) ||
                        Single.IsNaN(power) || Single.IsInfinity(power) || power <= 0 ||
                        power > (operation == SpellOperation.Charge ? 500f : 2000000f))
                        return Fail("出力 power が範囲外です。熱量は0より大きく2,000,000 J以下、充填は0より大きく500 MU以下です。", DslSourceLabel);
                }
                else if (pair[0] == "material")
                {
                    if (operation != SpellOperation.Build) return Fail("素材 material を指定できるのは建築 build だけです。", DslSourceLabel);
                    if (!Enum.TryParse<MaterialId>(pair[1], true, out material) || !Enum.IsDefined(typeof(MaterialId), material) ||
                        material == MaterialId.Air || material == MaterialId.Steam || Regex.IsMatch(pair[1], @"^[0-9]"))
                        return Fail("建築に使えない素材、または不明な素材です。", DslSourceLabel);
                }
                else return Fail("数値指定に存在しない項目です: " + pair[0], DslSourceLabel);
            }
            return Valid(operation, radius, power, material, DslSourceLabel, 1f);
        }

        private static MagicParseResult Valid(SpellOperation op, int radius, float power, MaterialId material, string source, float confidence)
        {
            if (radius < 1 || radius > 5 || Single.IsNaN(power) || Single.IsInfinity(power))
                return Fail("生成された範囲・出力が有効な値ではありません。", source, confidence);
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
