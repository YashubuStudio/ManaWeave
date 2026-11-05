// Numeric weights constructed directly from the bounded magic-language spec.
// This is a neural conditional token transducer, not a general-purpose LLM.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ManaWeave.Core;

namespace ManaWeave.Magic
{
    public sealed class WeightSpaceMagic
    {
        public const int FormatVersion = 2;
        public const string SourceLabel = "詠唱解析";
        private const int ActionCountFeature = 15, TargetCountFeature = 16;
        private readonly string[] lexicon, outputs;
        private readonly int featureCount, inputCount, hidden1, hidden2;
        private readonly float[] embedding, w1, b1, w2, b2, w3, b3;
        public int ParameterCount { get; private set; }
        public string ModelStatus { get { return "直接生成の詠唱モデル / " + ParameterCount.ToString("N0") + " パラメータ"; } }

        public WeightSpaceMagic(byte[] weights)
        {
            if (weights == null || weights.Length < 32 || weights.Length > 16000000)
                throw new InvalidDataException("詠唱モデルのデータが存在しないか、サイズが正しくありません。");
            using (var stream = new MemoryStream(weights, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "MWSP" || reader.ReadInt32() != FormatVersion)
                    throw new InvalidDataException("詠唱モデルの形式が一致しません。コードと重みを同じ修正版に更新してください。");
                int vocab = Bound(reader.ReadInt32(), 1, 4096);
                featureCount = Bound(reader.ReadInt32(), 23, 23);
                inputCount = Bound(reader.ReadInt32(), 28, 28);
                hidden1 = Bound(reader.ReadInt32(), 1, 1024);
                hidden2 = Bound(reader.ReadInt32(), 1, 4096);
                int outputCount = Bound(reader.ReadInt32(), 1, 128);
                lexicon = Strings(reader, vocab); outputs = Strings(reader, outputCount);
                embedding = Floats(reader, vocab * featureCount);
                w1 = Floats(reader, hidden1 * inputCount); b1 = Floats(reader, hidden1);
                w2 = Floats(reader, hidden2 * hidden1); b2 = Floats(reader, hidden2);
                w3 = Floats(reader, outputCount * hidden2); b3 = Floats(reader, outputCount);
                ParameterCount = embedding.Length + w1.Length + b1.Length + w2.Length + b2.Length + w3.Length + b3.Length;
                if (stream.Position != stream.Length) throw new InvalidDataException("詠唱モデルの末尾に想定外のデータがあります。");
            }
        }

        public MagicParseResult Parse(string text)
        {
            float[] features = Encode(text);
            string[] generated = Generate(features);
            if (generated.Length != 5 || generated[0] == "reject")
                return Reject(ExplainRejection(text, features));
            SpellOperation operation;
            if (!Enum.TryParse(generated[0], true, out operation) || !Enum.IsDefined(typeof(SpellOperation), operation))
                return Reject("詠唱モデルが未対応の効果を出力しました。");
            int radius;
            float power;
            if (!generated[1].StartsWith("r", StringComparison.Ordinal) || !int.TryParse(generated[1].Substring(1), out radius) ||
                !generated[2].StartsWith("p", StringComparison.Ordinal) || !float.TryParse(generated[2].Substring(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out power))
                return Reject("詠唱モデルの範囲・出力の形式が正しくありません。");
            MaterialId material;
            if (!Enum.TryParse(generated[3], true, out material) || !Enum.IsDefined(typeof(MaterialId), material) || generated[4] != "eos")
                return Reject("詠唱モデルの素材・終了記号の形式が正しくありません。");
            if (radius < 1 || radius > 5 || float.IsNaN(power) || float.IsInfinity(power) || power < 0 ||
                ((operation == SpellOperation.Heat || operation == SpellOperation.Cool) && (power <= 0 || power > 2000000)) ||
                (operation == SpellOperation.Charge && (power <= 0 || power > 500)) ||
                (operation == SpellOperation.Build && material != MaterialId.Stone && material != MaterialId.Wood &&
                    material != MaterialId.Iron && material != MaterialId.Crystal))
                return Reject("生成された術式が、この世界で扱える範囲を超えています。");
            string canonical = generated[0] + " r=" + radius;
            if (operation == SpellOperation.Heat || operation == SpellOperation.Cool || operation == SpellOperation.Charge)
                canonical += " power=" + power.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (operation == SpellOperation.Build) canonical += " material=" + generated[3];
            return new MagicParseResult
            {
                Success = true, Source = SourceLabel, Error = "",
                Operation = operation, Radius = radius, Power = power, Material = material,
                Canonical = canonical, Confidence = 1f
            };
        }

        public string[] Generate(string text)
        {
            return Generate(Encode(text));
        }

        private string[] Generate(float[] features)
        {
            var result = new List<string>(5);
            for (int stage = 0; stage < 5; stage++)
            {
                float[] logits = Forward(features, stage);
                int best = 0;
                for (int i = 1; i < logits.Length; i++) if (logits[i] > logits[best]) best = i;
                string token = outputs[best]; result.Add(token);
                if (token == "reject" || token == "eos") break;
            }
            return result.ToArray();
        }

        // Public diagnostic endpoint permits Python/C# numerical parity checks.
        public float[] PredictLogits(string text, int stage)
        {
            if (stage < 0 || stage > 4) throw new ArgumentOutOfRangeException("stage");
            return Forward(Encode(text), stage);
        }

        private float[] Encode(string text)
        {
            float[] features = new float[featureCount];
            // Check before normalization too. A long pasted suffix is never
            // silently dropped, even if it contains a late restriction.
            if (text == null || text.Length > MagicParser.MaxInputLength)
            { features[13] = 1; return features; }
            try { text = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Trim(); }
            catch (ArgumentException) { features[13] = 1; return features; }
            if (text.Length == 0 || text.Length > MagicParser.MaxInputLength) { features[13] = 1; return features; }
            int offset = 0;
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset])) { offset++; continue; }
                bool found = false;
                for (int token = 0; token < lexicon.Length; token++)
                {
                    string word = lexicon[token];
                    if (offset + word.Length > text.Length || string.CompareOrdinal(text, offset, word, 0, word.Length) != 0) continue;
                    int end = offset + word.Length;
                    if (AsciiLetter(word[word.Length-1]) && end < text.Length && AsciiLetter(text[end])) continue;
                    for (int d = 0; d < featureCount; d++)
                    {
                        float value = embedding[token * featureCount + d];
                        if (d == ActionCountFeature || d == TargetCountFeature) features[d] += value;
                        else features[d] = Math.Max(features[d], value);
                    }
                    offset = end; found = true; break;
                }
                if (!found) { features[13] = 1; offset++; }
            }
            return features;
        }

        // Called only AFTER numeric inference rejects. These explanations never
        // create or rewrite a successful operation or its parameters.
        private static string ExplainRejection(string text, float[] f)
        {
            if (string.IsNullOrWhiteSpace(text)) return "詠唱を入力してください。例文ボタンからも選べます。";
            if (text.Length > MagicParser.MaxInputLength) return "詠唱は " + MagicParser.MaxInputLength + " 文字以内にしてください。末尾を省略せず解析を中止しました。";
            if (f[12] > 0) return "否定・条件・対象の選別・繰り返し・形状指定など、この術式で表せない指定が含まれています。効果を一つずつ指定してください。";
            if (f[13] > 0) return "未対応の語や文体が含まれています。例文を基に、呼びかけ・範囲・一つの発動指示を組み合わせてください。";
            if (f[ActionCountFeature] > 1) return "発動指示が複数あります。同じ効果の繰り返しも含め、1回の解析では指示を一つにしてください。";
            if (f[TargetCountFeature] > 1) return "対象の指定が複数あります。対象は現在の照準先を一度だけ指定してください。";
            if (f[0] + f[1] + f[2] + f[3] + f[4] + f[5] == 0)
                return "発動指示が見つかりません。呼びかけに続けて『焼け』『冷やせ』『穿て』などの効果を指定してください。";
            if (f[6] + f[7] + f[14] > 1) return "範囲の指定が矛盾しています。小規模・標準・広範囲のうち一つにしてください。";
            float materials = f[8] + f[9] + f[10] + f[11];
            if (f[3] > 0 && materials == 0) return "創り出す素材を指定してください。石・木材・鉄・魔導結晶に対応しています。";
            if (materials > 1) return "素材が複数あります。一つの素材を指定してください。";
            if (materials > 0 && f[3] == 0) return "素材で対象を絞り込む指定には対応していません。素材名は建築のときに指定してください。";
            if ((f[17] > 0 && f[0] == 0) || (f[18] > 0 && f[1] == 0) ||
                (f[19] > 0 && f[2] == 0 && f[3] == 0) || (f[20] > 0 && f[3] == 0) ||
                (f[21] > 0 && f[4] == 0) || (f[22] > 0 && f[5] == 0))
                return "呼びかけの属性と発動指示が一致しません。炎は加熱、霜は冷却、大地は掘削・建築など、同じ効果に揃えてください。";
            return "この詠唱は一つの術式に確定できません。例文か数値指定を使ってください。";
        }

        private float[] Forward(float[] features, int stage)
        {
            float[] input = new float[inputCount];
            Array.Copy(features, input, features.Length); input[featureCount + stage] = 1;
            float[] h1 = Affine(input, w1, b1, true);
            float[] h2 = Affine(h1, w2, b2, true);
            return Affine(h2, w3, b3, false);
        }

        private static float[] Affine(float[] input, float[] matrix, float[] bias, bool relu)
        {
            float[] result = new float[bias.Length];
            for (int row = 0; row < result.Length; row++)
            {
                float value = bias[row]; int start = row * input.Length;
                for (int col = 0; col < input.Length; col++) value += matrix[start + col] * input[col];
                result[row] = relu ? Math.Max(0, value) : value;
            }
            return result;
        }

        private static int Bound(int value, int min, int max)
        { if (value < min || value > max) throw new InvalidDataException("詠唱モデルの行列サイズが正しくありません。"); return value; }
        private static bool AsciiLetter(char c) { return c >= 'a' && c <= 'z'; }
        private static string[] Strings(BinaryReader reader, int count)
        {
            var result = new string[count];
            for (int i=0;i<count;i++)
            {
                int length = Bound(reader.ReadInt32(),1,1024);
                byte[] bytes=reader.ReadBytes(length);
                if(bytes.Length!=length) throw new EndOfStreamException();
                result[i]=new UTF8Encoding(false,true).GetString(bytes);
            }
            return result;
        }
        private static float[] Floats(BinaryReader reader,int count)
        {
            float[] result=new float[count];
            for(int i=0;i<count;i++)
            { result[i]=reader.ReadSingle(); if(float.IsNaN(result[i])||float.IsInfinity(result[i])) throw new InvalidDataException("詠唱モデルに有限値でない重みがあります。"); }
            return result;
        }
        private static MagicParseResult Reject(string message)
        { return new MagicParseResult { Success=false, Error=message, Source=SourceLabel, Canonical="" }; }
    }
}
