using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ManaWeave.Core;
using ManaWeave.Magic;

public static class LearnedAcceptance
{
    private sealed class Case
    {
        public string Text; public string[] Expected;
    }
    public static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
        var weights = File.ReadAllBytes(Path.Combine(root,"UnityProject/Assets/Resources/ManaWeave/magic_model.bytes"));
        var wspBytes = File.ReadAllBytes(Path.Combine(root,"UnityProject/Assets/Resources/ManaWeave/wsp_model.bytes"));
        var wsp = new WeightSpaceMagic(wspBytes);
        var parser = new MagicParser(weights, wsp.Parse);
        var report = new Dictionary<string,object>();
        report["model_sha256"] = Hash(weights);
        report["wsp_sha256"] = Hash(wspBytes);
        report["protocol"] = "Frozen-model actual C# parsing. Development templates were observed previously. Manual challenge written after final weight freeze; no subsequent weight/threshold/guard tuning. Small curated diagnostics, not production NLP accuracy.";
        var allPredictions = new StringBuilder();
        int unsafeTotal = 0;
        foreach(string split in new[]{"heldout","manual_challenge"})
        {
            string path=Path.Combine(root,"LLM",split=="heldout" ? "data/heldout.jsonl" : "manual_challenge.jsonl");
            List<Case> cases=Read(path);
            var group=new Dictionary<string,object>();
            group["dataset_sha256"]=Hash(File.ReadAllBytes(path));
            foreach(string mode in new[]{"learned_guarded","wsp_guarded"})
            {
                var errors=new List<object>();
                var reasons=new Dictionary<string,int>();
                var sources=new Dictionary<string,int>();
                int positive=0,negative=0,accepted=0,correctAccepted=0,rejectedNegative=0,wrongAccepted=0;
                Stopwatch timer=Stopwatch.StartNew();
                for(int i=0;i<cases.Count;i++)
                {
                    Case c=cases[i];
                    MagicParseResult got=mode=="learned_guarded" ? parser.ParseLearned(c.Text) : parser.Parse(c.Text);
                    bool reject=c.Expected[0]=="@reject";
                    if(reject)negative++;else positive++;
                    string source=got.Source ?? "null";
                    if(!sources.ContainsKey(source))sources[source]=0;sources[source]++;
                    bool exact = !reject && got.Success && Match(got,c.Expected);
                    if(got.Success)
                    {
                        accepted++;
                        if(exact)correctAccepted++;
                        else {wrongAccepted++;errors.Add(new {text=c.Text, expected=c.Expected, actual=got.Canonical, source=got.Source, confidence=got.Confidence});}
                    }
                    else
                    {
                        if(reject)rejectedNegative++;
                        string why=got.Error ?? "unspecified";
                        if(!reasons.ContainsKey(why))reasons[why]=0;reasons[why]++;
                    }
                    allPredictions.AppendLine(JsonSerializer.Serialize(new {split=split,mode=mode,text=c.Text,
                        expected=c.Expected,accepted=got.Success,exact=exact,canonical=got.Canonical,
                        error=got.Error,source=got.Source,confidence=got.Confidence}));
                    if((i+1)%200==0)Console.WriteLine(split+" / "+mode+": "+(i+1)+"/"+cases.Count);
                }
                timer.Stop();
                group[mode]=new {
                    count=cases.Count,positive_count=positive,negative_count=negative,
                    accepted_count=accepted,correct_accepted_count=correctAccepted,
                    positive_coverage=positive==0?0:(double)(accepted-(negative-rejectedNegative))/positive,
                    accepted_exact_accuracy=accepted==0?0:(double)correctAccepted/accepted,
                    rejected_negative_count=rejectedNegative,wrong_accepted_count=wrongAccepted,
                    elapsed_seconds=timer.Elapsed.TotalSeconds,
                    source_counts=sources,rejection_reasons=reasons,wrong_accepts=errors
                };
                unsafeTotal+=wrongAccepted;
                Console.WriteLine(split+" / "+mode+": accepted="+accepted+", correct="+correctAccepted+", wrong="+wrongAccepted+", seconds="+timer.Elapsed.TotalSeconds.ToString("F2"));
            }
            report[split]=group;
        }
        File.WriteAllText(Path.Combine(root,"LLM/acceptance_report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root,"LLM/acceptance_predictions.jsonl"),allPredictions.ToString(),new UTF8Encoding(false));
        Console.WriteLine("Reported wrong acceptances: "+unsafeTotal);
        return unsafeTotal==0?0:1;
    }
    private static bool Match(MagicParseResult got,string[] expected)
    {
        SpellOperation op;MaterialId material;int radius;float power;
        return expected.Length==5 && Enum.TryParse<SpellOperation>(expected[0].Substring(1),true,out op) &&
            Int32.TryParse(expected[1].Substring(2),out radius) && Single.TryParse(expected[2].Substring(2),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out power) &&
            Enum.TryParse<MaterialId>(expected[3].Substring(1),true,out material) &&
            got.Operation==op && got.Radius==radius && got.Power==power && got.Material==material;
    }
    private static List<Case> Read(string path)
    {
        var cases=new List<Case>();
        foreach(string line in File.ReadLines(path))
        {
            using(JsonDocument doc=JsonDocument.Parse(line))
            {
                JsonElement row=doc.RootElement;
                var answers=new List<string>();foreach(JsonElement token in row.GetProperty("answer").EnumerateArray())answers.Add(token.GetString());
                cases.Add(new Case{Text=row.GetProperty("text").GetString(),Expected=answers.ToArray()});
            }
        }
        return cases;
    }
    private static string Hash(byte[] bytes)
    {using(SHA256 sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
}
