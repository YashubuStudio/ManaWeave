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
        private readonly string[] lexicon, outputs;
        private readonly int featureCount, inputCount, hidden1, hidden2;
        private readonly float[] embedding, w1, b1, w2, b2, w3, b3;
        public int ParameterCount { get; private set; }
        public string ModelStatus { get { return "Direct-weight neural decoder / " + ParameterCount.ToString("N0") + " parameters"; } }

        public WeightSpaceMagic(byte[] weights)
        {
            if (weights == null || weights.Length < 32 || weights.Length > 16000000)
                throw new InvalidDataException("Invalid direct-weight model size.");
            using (var stream = new MemoryStream(weights, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "MWSP" || reader.ReadInt32() != 1)
                    throw new InvalidDataException("Unknown direct-weight model format.");
                int vocab = Bound(reader.ReadInt32(), 1, 4096);
                featureCount = Bound(reader.ReadInt32(), 15, 15);
                inputCount = Bound(reader.ReadInt32(), 20, 20);
                hidden1 = Bound(reader.ReadInt32(), 1, 1024);
                hidden2 = Bound(reader.ReadInt32(), 1, 4096);
                int outputCount = Bound(reader.ReadInt32(), 1, 128);
                lexicon = Strings(reader, vocab); outputs = Strings(reader, outputCount);
                embedding = Floats(reader, vocab * featureCount);
                w1 = Floats(reader, hidden1 * inputCount); b1 = Floats(reader, hidden1);
                w2 = Floats(reader, hidden2 * hidden1); b2 = Floats(reader, hidden2);
                w3 = Floats(reader, outputCount * hidden2); b3 = Floats(reader, outputCount);
                ParameterCount = embedding.Length + w1.Length + b1.Length + w2.Length + b2.Length + w3.Length + b3.Length;
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing model bytes.");
            }
        }

        public MagicParseResult Parse(string text)
        {
            string[] generated = Generate(text);
            if (generated.Length != 5 || generated[0] == "reject")
                return Reject("この表現は対応範囲外です。単一の操作と範囲を指定してください。条件・否定・複数操作にはDSLを個別に使います。");
            SpellOperation operation;
            if (!Enum.TryParse(generated[0], true, out operation)) return Reject("Unknown operation.");
            int radius;
            float power;
            if (!generated[1].StartsWith("r", StringComparison.Ordinal) || !int.TryParse(generated[1].Substring(1), out radius) ||
                !generated[2].StartsWith("p", StringComparison.Ordinal) || !float.TryParse(generated[2].Substring(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out power))
                return Reject("Malformed neural output.");
            MaterialId material;
            if (!Enum.TryParse(generated[3], true, out material) || generated[4] != "eos") return Reject("Malformed material output.");
            string canonical = generated[0] + " r=" + radius;
            if (operation == SpellOperation.Heat || operation == SpellOperation.Cool || operation == SpellOperation.Charge)
                canonical += " power=" + power.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (operation == SpellOperation.Build) canonical += " material=" + generated[3];
            return new MagicParseResult
            {
                Success = true, Source = "WSP / directly constructed weights", Error = "",
                Operation = operation, Radius = radius, Power = power, Material = material,
                Canonical = canonical, Confidence = 1f
            };
        }

        public string[] Generate(string text)
        {
            float[] features = Encode(text);
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
            text = (text ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant().Trim();
            if (text.Length == 0 || text.Length > 192) { features[13] = 1; return features; }
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
                    for (int d = 0; d < featureCount; d++) features[d] = Math.Max(features[d], embedding[token * featureCount + d]);
                    offset = end; found = true; break;
                }
                if (!found) { features[13] = 1; offset++; }
            }
            return features;
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
        { if (value < min || value > max) throw new InvalidDataException("Invalid model dimension."); return value; }
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
            { result[i]=reader.ReadSingle(); if(float.IsNaN(result[i])||float.IsInfinity(result[i])) throw new InvalidDataException("Non-finite weight."); }
            return result;
        }
        private static MagicParseResult Reject(string message)
        { return new MagicParseResult { Success=false, Error=message, Source="WSP / directly constructed weights" }; }
    }
}
