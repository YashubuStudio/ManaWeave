using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ManaWeave.Core;
using ManaWeave.Magic;

public static class MagicHarness
{
    private static int passed;
    private static readonly List<string> failures = new List<string>();
    public static int Main(string[] args)
    {
        string modelPath = args.Length > 0 ? Path.GetFullPath(args[0]) :
            Path.GetFullPath("UnityProject/Assets/Resources/ManaWeave/magic_model.bytes");
        string root = FindRoot(Path.GetDirectoryName(modelPath));
        byte[] weights = File.ReadAllBytes(modelPath);
        byte[] directWeights = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(modelPath), "wsp_model.bytes"));
        var direct = new WeightSpaceMagic(directWeights);
        var parser = new MagicParser(weights, direct.Parse);
        var transformer = new RuneLanguageModel(weights);
        Check("learned weights are real and below 0.1B", transformer.ParameterCount > 10000 && transformer.ParameterCount < 100000000);
        Check("direct weights are numeric network", direct.ParameterCount > 1000);
        string[] examples = { "目の前を小さく熱して", "広範囲に冷やして", "対象を掘削して", "石で建てて", "魔力を充填して", "錬成して" };
        SpellOperation[] expected = { SpellOperation.Heat, SpellOperation.Cool, SpellOperation.Excavate, SpellOperation.Build, SpellOperation.Charge, SpellOperation.Transmute };
        for (int i = 0; i < examples.Length; i++)
        {
            MagicParseResult a = parser.Parse(examples[i]);
            Check("direct intent: " + examples[i], a.Success && a.Operation == expected[i] && a.Source.Contains("WSP"));
            MagicParseResult b = parser.ParseLearned(examples[i]);
            Check("learned intent: " + examples[i], b.Success && b.Operation == expected[i] && b.Source.Contains("Transformer"));
            if (a.Success)
            {
                MagicParseResult round = parser.Parse(a.Canonical);
                Check("canonical DSL round trip " + i, round.Success && round.Operation == a.Operation && round.Radius == a.Radius && round.Power == a.Power && round.Material == a.Material);
            }
        }
        string[] rejected = { "熱して冷やして", "熱して、ただし容器は壊すな", "加熱しない", "heat r=99", "heat power=NaN", "heat power=Infinity", "heat r=2 r=3", "cool power=-5", "build material=banana", "build power=500", "charge power=999999", "cool unknown=1", "heat r=1\ncool r=1", "世界の時間を止めて", "please do not heat the target", "小さく大きく熱して", "heat power=50; excavate", "水だけを熱して" };
        foreach (string text in rejected) Check("rejection: " + text.Replace('\n',' '), !parser.Parse(text).Success);
        Check("explicit DSL total energy", parser.Parse("heat r=3 power=123456").Power == 123456f);
        Check("missing model still accepts DSL", new MagicParser(null).Parse("cool r=1 power=1000").Success);
        Check("missing model rejects natural input", !new MagicParser(null).Parse("冷やして").Success);
        bool malformedRejected = false;
        try { new WeightSpaceMagic(new byte[40]); } catch (InvalidDataException) { malformedRejected = true; }
        Check("malformed direct model rejected", malformedRejected);
        TestTransformerParity(root, transformer, weights);
        TestDirectParity(root, direct);
        TestIntegratedCasting(parser);
        var sw = Stopwatch.StartNew();
        for (int i=0;i<100;i++) parser.Parse(examples[i % examples.Length]);
        sw.Stop();
        Console.WriteLine("Direct decoder mean time (100 commands, this host): " + (sw.Elapsed.TotalMilliseconds/100).ToString("F3") + " ms");
        Console.WriteLine("MAGIC TESTS: " + passed + " passed, " + failures.Count + " failed.");
        foreach (string failure in failures) Console.Error.WriteLine("FAIL: " + failure);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void TestTransformerParity(string root, RuneLanguageModel model, byte[] weights)
    {
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"LLM/parity_fixture.json"))))
        {
            JsonElement obj=document.RootElement;
            string hash;
            using (SHA256 sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(weights)).Replace("-","").ToLowerInvariant();
            Check("parity fixture matches learned checkpoint", obj.GetProperty("model_sha256").GetString()==hash);
            double tolerance=obj.GetProperty("absolute_tolerance").GetDouble();
            double maxError=0;
            foreach (JsonElement sample in obj.GetProperty("samples").EnumerateArray())
            {
                var ids=new List<int>();
                foreach (JsonElement item in sample.GetProperty("tokens").EnumerateArray()) ids.Add(item.GetInt32());
                float[] predicted=model.PredictNext(ids.ToArray());
                int j=0;
                foreach(JsonElement expected in sample.GetProperty("logits").EnumerateArray())
                { maxError=Math.Max(maxError,Math.Abs(predicted[j++]-expected.GetSingle())); }
                Check("Transformer parity "+sample.GetProperty("text").GetString(),maxError <= tolerance && j==predicted.Length);
            }
            Console.WriteLine("Transformer max absolute logit error: "+maxError.ToString("G9"));
        }
    }
    private static void TestDirectParity(string root,WeightSpaceMagic model)
    {
        using(JsonDocument document=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"LLM/weightspace/parity_fixture.json"))))
        {
            foreach(JsonElement sample in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                string text=sample.GetProperty("text").GetString();
                var wanted=new List<string>();
                foreach(JsonElement token in sample.GetProperty("tokens").EnumerateArray()) wanted.Add(token.GetString());
                Check("WSP generated token parity "+text,String.Join("|",model.Generate(text))==String.Join("|",wanted));
                int stage=0;double error=0;
                foreach(JsonElement expected in sample.GetProperty("logits").EnumerateArray())
                {
                    float[] logits=model.PredictLogits(text,stage++);int j=0;
                    foreach(JsonElement value in expected.EnumerateArray()) error=Math.Max(error,Math.Abs(logits[j++]-value.GetSingle()));
                }
                Check("WSP numerical parity "+text,error==0);
            }
        }
    }
    private static void TestIntegratedCasting(MagicParser parser)
    {
        WorldState w=new WorldState(12,12,12,731);
        w.SetMaterial(6,6,6,MaterialId.Stone);
        var sim=new WorldSimulator(w);
        var ir=parser.Parse("目の前を小さく熱して");
        double before=w.TotalEnergy();float mana=w.CasterMana;
        SpellResult result=sim.Cast(new SpellCommand{Operation=ir.Operation,Radius=ir.Radius,Power=ir.Power,Material=ir.Material,X=6,Y=6,Z=6});
        Check("neural IR executes through world laws",result.Success && w.TotalEnergy()>before && w.CasterMana<mana);
        w.CasterMana=0;before=w.TotalEnergy();
        SpellResult failed=sim.Cast(new SpellCommand{Operation=SpellOperation.Heat,Radius=1,Power=50000,X=6,Y=6,Z=6});
        Check("world law rejects unfunded neural spell atomically",!failed.Success && w.TotalEnergy()==before);
    }
    private static string FindRoot(string start)
    {
        DirectoryInfo dir=new DirectoryInfo(start);
        while(dir!=null)
        { if(Directory.Exists(Path.Combine(dir.FullName,"LLM")))return dir.FullName;dir=dir.Parent; }
        throw new DirectoryNotFoundException("Cannot find ManaWeave root.");
    }
    private static void Check(string name,bool condition)
    { if(condition){passed++;Console.WriteLine("PASS: "+name);}else failures.Add(name); }
}
