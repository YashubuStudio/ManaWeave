// ManaWeave original causal Transformer inference. No Unity or ML runtime dependency.
// Float32 weights trained by LLM/model.py. Each request gets its own KV cache.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ManaWeave.Magic
{
    public sealed class RuneLanguageModel
    {
        public readonly int Dimension, Heads, Layers, ContextLength, ParameterCount;
        public readonly string[] Vocabulary;
        private readonly Dictionary<string, int> tokenIds;
        private readonly float[] embedding, position, finalScale, finalBias;
        private readonly Layer[] layers;
        public int VocabularySize { get { return Vocabulary.Length; } }

        private sealed class Layer
        {
            public float[] Scale1, Bias1, Qkv, Projection, Scale2, Bias2, Up, Down;
        }

        public RuneLanguageModel(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 36) throw new InvalidDataException("Missing RuneLM weights.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "MWLM0001")
                    throw new InvalidDataException("Not a ManaWeave RuneLM file.");
                int version = reader.ReadInt32();
                if (version != 1) throw new InvalidDataException("Unsupported RuneLM version.");
                Dimension = reader.ReadInt32(); Heads = reader.ReadInt32(); Layers = reader.ReadInt32();
                ContextLength = reader.ReadInt32(); int count = reader.ReadInt32(); ParameterCount = reader.ReadInt32();
                if (Dimension < 8 || Dimension > 2048 || Heads < 1 || Dimension % Heads != 0 ||
                    Layers < 1 || Layers > 32 || ContextLength < 16 || ContextLength > 2048 ||
                    count < 16 || count > 65536)
                    throw new InvalidDataException("RuneLM configuration is out of bounds.");
                long expected = (long)count * Dimension + (long)ContextLength * Dimension +
                    Layers * (12L * Dimension * Dimension + 4L * Dimension) + 2L * Dimension;
                if (ParameterCount != expected || expected > 200000000L || bytes.LongLength < 36L + expected * 4L)
                    throw new InvalidDataException("RuneLM parameter count is invalid.");
                Vocabulary = new string[count]; tokenIds = new Dictionary<string, int>(count, StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    int n = reader.ReadInt32();
                    if (n < 1 || n > 64 || stream.Length - stream.Position < n)
                        throw new InvalidDataException("Invalid vocabulary token.");
                    string token = new UTF8Encoding(false, true).GetString(reader.ReadBytes(n));
                    if (tokenIds.ContainsKey(token)) throw new InvalidDataException("Duplicate vocabulary token.");
                    Vocabulary[i] = token; tokenIds.Add(token, i);
                }
                if (stream.Length - stream.Position != expected * 4L)
                    throw new InvalidDataException("RuneLM tensor byte count mismatch.");
                embedding = Read(reader, count * Dimension);
                position = Read(reader, ContextLength * Dimension);
                layers = new Layer[Layers];
                for (int l = 0; l < Layers; l++)
                {
                    layers[l] = new Layer {
                        Scale1 = Read(reader, Dimension), Bias1 = Read(reader, Dimension),
                        Qkv = Read(reader, 3 * Dimension * Dimension), Projection = Read(reader, Dimension * Dimension),
                        Scale2 = Read(reader, Dimension), Bias2 = Read(reader, Dimension),
                        Up = Read(reader, 4 * Dimension * Dimension), Down = Read(reader, 4 * Dimension * Dimension)
                    };
                }
                finalScale = Read(reader, Dimension); finalBias = Read(reader, Dimension);
                string[] required = { "<bos>", "<sep>", "<eos>", "@heat", "@cool", "@excavate", "@build",
                    "@charge", "@transmute", "@reject", "@r1", "@r2", "@r3", "@p0", "@p20", "@p50", "@p80",
                    "@p50000", "@p120000", "@p300000", "@stone", "@wood", "@iron", "@crystal" };
                foreach (string token in required)
                    if (!tokenIds.ContainsKey(token)) throw new InvalidDataException("Missing required token: " + token);
            }
        }

        private static float[] Read(BinaryReader reader, int count)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                float value = reader.ReadSingle();
                if (Single.IsNaN(value) || Single.IsInfinity(value))
                    throw new InvalidDataException("Non-finite model weight.");
                values[i] = value;
            }
            return values;
        }

        public int TokenId(string token)
        {
            int id;
            if (!tokenIds.TryGetValue(token, out id)) throw new ArgumentException("Unknown model token: " + token);
            return id;
        }

        public int[] EncodePrompt(string normalizedText)
        {
            if (normalizedText == null) throw new ArgumentNullException("normalizedText");
            if (normalizedText.Length + 7 > ContextLength) throw new ArgumentException("Chant is too long for this model.");
            var result = new int[normalizedText.Length + 2];
            result[0] = TokenId("<bos>");
            for (int i = 0; i < normalizedText.Length; i++)
            {
                int id;
                if (!tokenIds.TryGetValue(normalizedText[i].ToString(), out id))
                    throw new ArgumentException("This prototype has no vocabulary for: " + normalizedText[i]);
                result[i + 1] = id;
            }
            result[result.Length - 1] = TokenId("<sep>");
            return result;
        }

        // Public diagnostic surface for Python/C# numeric parity tests.
        public float[] PredictNext(int[] tokens)
        {
            if (tokens == null || tokens.Length == 0 || tokens.Length > ContextLength)
                throw new ArgumentException("Invalid token sequence.");
            var session = new Session(this);
            float[] logits = null;
            for (int i = 0; i < tokens.Length; i++) logits = session.Push(tokens[i], i == tokens.Length - 1);
            return logits;
        }

        public string[] Generate(int[] prompt, out float confidence)
        {
            if (prompt == null || prompt.Length < 1 || prompt.Length + 5 > ContextLength)
                throw new ArgumentException("Invalid generation context.");
            var session = new Session(this); float[] logits = null;
            for (int i = 0; i < prompt.Length; i++) logits = session.Push(prompt[i], i == prompt.Length - 1);
            var history = new List<string>(5); confidence = 1f;
            for (int step = 0; step < 5; step++)
            {
                string[] valid = Allowed(history);
                double max = Double.NegativeInfinity;
                for (int j = 0; j < valid.Length; j++) max = Math.Max(max, logits[TokenId(valid[j])]);
                double sum = 0; double bestValue = Double.NegativeInfinity; int best = 0;
                for (int j = 0; j < valid.Length; j++)
                {
                    double value = logits[TokenId(valid[j])];
                    sum += Math.Exp(value - max);
                    if (value > bestValue) { bestValue = value; best = j; }
                }
                float probability = (float)(Math.Exp(bestValue - max) / sum);
                if (valid.Length > 1) confidence = Math.Min(confidence, probability);
                string chosen = valid[best]; history.Add(chosen);
                if (chosen == "<eos>") break;
                logits = session.Push(TokenId(chosen), true);
            }
            return history.ToArray();
        }

        private static string[] Allowed(List<string> history)
        {
            if (history.Count == 0)
                return new[] { "@heat", "@cool", "@excavate", "@build", "@charge", "@transmute", "@reject" };
            string op = history[0];
            if (op == "@reject") return new[] { "<eos>" };
            if (history.Count == 1) return new[] { "@r1", "@r2", "@r3" };
            if (history.Count == 2)
            {
                if (op == "@heat" || op == "@cool") return new[] { "@p50000", "@p120000", "@p300000" };
                if (op == "@charge") return new[] { "@p20", "@p50", "@p80" };
                return new[] { "@p0" };
            }
            if (history.Count == 3)
                return op == "@build" ? new[] { "@stone", "@wood", "@iron", "@crystal" } : new[] { "@stone" };
            return new[] { "<eos>" };
        }

        private sealed class Session
        {
            private readonly RuneLanguageModel model;
            private readonly float[][] keyCache, valueCache;
            private readonly float[] x, normalized, qkv, attended, projected, hidden, scores;
            private int positionIndex;

            public Session(RuneLanguageModel model)
            {
                this.model = model; int d = model.Dimension;
                keyCache = new float[model.Layers][]; valueCache = new float[model.Layers][];
                for (int l = 0; l < model.Layers; l++)
                {
                    keyCache[l] = new float[model.ContextLength * d];
                    valueCache[l] = new float[model.ContextLength * d];
                }
                x = new float[d]; normalized = new float[d]; qkv = new float[3 * d];
                attended = new float[d]; projected = new float[d]; hidden = new float[4 * d];
                scores = new float[model.ContextLength];
            }

            public float[] Push(int token, bool produceLogits)
            {
                int d = model.Dimension, p = positionIndex;
                if (token < 0 || token >= model.Vocabulary.Length || p >= model.ContextLength)
                    throw new ArgumentException("Token or context out of bounds.");
                for (int i = 0; i < d; i++) x[i] = model.embedding[token * d + i] + model.position[p * d + i];
                for (int l = 0; l < model.Layers; l++)
                {
                    Layer layer = model.layers[l];
                    Norm(x, normalized, layer.Scale1, layer.Bias1);
                    Linear(normalized, layer.Qkv, qkv, d, 3 * d);
                    for (int i = 0; i < d; i++)
                    {
                        keyCache[l][p * d + i] = qkv[d + i];
                        valueCache[l][p * d + i] = qkv[2 * d + i];
                    }
                    int hd = d / model.Heads; double scale = 1.0 / Math.Sqrt(hd);
                    Array.Clear(attended, 0, attended.Length);
                    for (int h = 0; h < model.Heads; h++)
                    {
                        int offset = h * hd; float max = Single.NegativeInfinity;
                        for (int t = 0; t <= p; t++)
                        {
                            double dot = 0;
                            for (int i = 0; i < hd; i++) dot += qkv[offset + i] * keyCache[l][t * d + offset + i];
                            scores[t] = (float)(dot * scale); max = Math.Max(max, scores[t]);
                        }
                        double sum = 0;
                        for (int t = 0; t <= p; t++) { scores[t] = (float)Math.Exp(scores[t] - max); sum += scores[t]; }
                        for (int t = 0; t <= p; t++)
                        {
                            float weight = (float)(scores[t] / sum);
                            for (int i = 0; i < hd; i++) attended[offset + i] += weight * valueCache[l][t * d + offset + i];
                        }
                    }
                    Linear(attended, layer.Projection, projected, d, d);
                    for (int i = 0; i < d; i++) x[i] += projected[i];
                    Norm(x, normalized, layer.Scale2, layer.Bias2);
                    Linear(normalized, layer.Up, hidden, d, 4 * d);
                    for (int i = 0; i < hidden.Length; i++) hidden[i] = Math.Max(0f, hidden[i]);
                    Linear(hidden, layer.Down, projected, 4 * d, d);
                    for (int i = 0; i < d; i++) x[i] += projected[i];
                }
                positionIndex++;
                if (!produceLogits) return null;
                Norm(x, normalized, model.finalScale, model.finalBias);
                var logits = new float[model.Vocabulary.Length];
                Linear(normalized, model.embedding, logits, d, logits.Length);
                return logits;
            }

            private static void Linear(float[] input, float[] weights, float[] output, int inputs, int outputs)
            {
                for (int row = 0; row < outputs; row++)
                {
                    int at = row * inputs; double sum = 0;
                    for (int col = 0; col < inputs; col++) sum += input[col] * weights[at + col];
                    output[row] = (float)sum;
                }
            }

            private static void Norm(float[] input, float[] output, float[] scale, float[] bias)
            {
                double mean = 0;
                for (int i = 0; i < input.Length; i++) mean += input[i];
                mean /= input.Length;
                double variance = 0;
                for (int i = 0; i < input.Length; i++) { double z = input[i] - mean; variance += z * z; }
                double inv = 1.0 / Math.Sqrt(variance / input.Length + 1e-5);
                for (int i = 0; i < input.Length; i++) output[i] = (float)((input[i] - mean) * inv * scale[i] + bias[i]);
            }
        }
    }
}
