// Original ManaWeave project tooling. Unity 6.3 LTS.
using System;
using System.IO;
using ManaWeave.Core;
using ManaWeave.Magic;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ManaWeave.Editor
{
    public static class ProjectTools
    {
        public const string SandboxScene = "Assets/Scenes/Sandbox.unity";

        [MenuItem("Tools/ManaWeave/Open Sandbox")]
        public static void OpenSandbox()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(SandboxScene);
        }

        [MenuItem("Tools/ManaWeave/Verify Core and Model")]
        public static void Verify()
        {
            HudFontTools.VerifyFontAssets();
            var world = WorldGenerator.Generate(24, 16, 24, 731);
            var sim = new WorldSimulator(world);
            for (int i = 0; i < 20; i++) sim.Step(0.1f);
            if (world.Tick < 20 || Math.Abs(world.SimulatedSeconds - 2.0) > 0.001 || sim.Stats.GeneratedMana <= 0)
                throw new InvalidOperationException("Mana generation or simulation clock failed.");
            for (int i = 0; i < world.Cells.Length; i++)
            {
                Cell c = world.Cells[i];
                if (!Finite(c.Energy) || !Finite(c.AmbientMana) || !Finite(c.StoredMana) ||
                    c.Energy < 0 || c.AmbientMana < 0 || c.StoredMana < 0)
                    throw new InvalidOperationException("Invalid cell state at " + i);
            }
            var model = Resources.Load<TextAsset>("ManaWeave/magic_model");
            if (model == null) throw new FileNotFoundException("The original trained model is missing.");
            var parser = new MagicParser(model.bytes);
            var result = parser.Parse("heat r=2 power=120000");
            if (!result.Success || result.Operation != SpellOperation.Heat)
                throw new InvalidOperationException("Magic parser validation failed.");
            var directAsset = Resources.Load<TextAsset>("ManaWeave/wsp_model");
            if (directAsset == null) throw new FileNotFoundException("Direct-weight model missing.");
            var direct = new WeightSpaceMagic(directAsset.bytes);
            var directResult = direct.Parse("目の前を小さく熱して");
            if (!directResult.Success || directResult.Operation != SpellOperation.Heat)
                throw new InvalidOperationException("Direct-weight model verification failed.");
            if (Resources.Load<Shader>("ManaWeave/Voxel") == null)
                throw new FileNotFoundException("ManaWeave vertex-color shader missing.");
            string path = Path.Combine(Application.temporaryCachePath, "manaweave_verification.mw");
            try
            {
                WorldPersistence.Save(world, path);
                WorldState restored = WorldPersistence.Load(path);
                if (restored.Tick != world.Tick || restored.Cells.Length != world.Cells.Length)
                    throw new InvalidOperationException("Save round trip failed.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            Debug.Log("ManaWeave verification passed. " + parser.ModelStatus +
                      ". This menu does not replace Play-mode/rendering checks.");
        }

        [MenuItem("Tools/ManaWeave/Build Windows x64")]
        public static void BuildWindows()
        {
            Build(BuildTarget.StandaloneWindows64, "Builds/Windows/ManaWeave.exe");
        }

        [MenuItem("Tools/ManaWeave/Build Linux x64")]
        public static void BuildLinux()
        {
            Build(BuildTarget.StandaloneLinux64, "Builds/Linux/ManaWeave.x86_64");
        }

        private static void Build(BuildTarget target, string defaultPath)
        {
            Verify();
            string output = Arg("-manaweaveOutput") ?? defaultPath;
            if (!Path.IsPathRooted(output)) output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", output));
            string parent = Path.GetDirectoryName(Path.GetFullPath(output));
            Directory.CreateDirectory(parent);
            PlayerSettings.companyName = "YashubuStudio";
            PlayerSettings.productName = "ManaWeave";
            PlayerSettings.runInBackground = true;
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(SandboxScene, true) };
            var options = new BuildPlayerOptions
            {
                scenes = new[] { SandboxScene },
                locationPathName = output,
                target = target,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("ManaWeave build failed: " + report.summary.result);
            Debug.Log("ManaWeave built: " + Path.GetFullPath(output));
        }

        private static bool Finite(float value)
        { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
