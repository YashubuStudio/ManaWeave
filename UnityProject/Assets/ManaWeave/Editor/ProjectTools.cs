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

        [MenuItem("Tools/ManaWeave/魔力世界を開く")]
        public static void OpenSandbox()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(SandboxScene);
        }

        [MenuItem("Tools/ManaWeave/世界法則と魔法を検証")]
        public static void Verify()
        {
            HudFontTools.VerifyFontAssets();
            var world = WorldGenerator.Generate(24, 16, 24, 731);
            var sim = new WorldSimulator(world);
            for (int i = 0; i < 20; i++) sim.Step(0.1f);
            if (world.Tick < 20 || Math.Abs(world.SimulatedSeconds - 2.0) > 0.001 || sim.Stats.GeneratedMana <= 0)
                throw new InvalidOperationException("魔力の自然発生または世界の時刻の検証に失敗しました。");
            for (int i = 0; i < world.Cells.Length; i++)
            {
                Cell c = world.Cells[i];
                if (!Finite(c.Energy) || !Finite(c.AmbientMana) || !Finite(c.StoredMana) ||
                    c.Energy < 0 || c.AmbientMana < 0 || c.StoredMana < 0 ||
                    !Finite(world.Temperature(i)) || world.Temperature(i) < MaterialCatalog.AbsoluteZero)
                    throw new InvalidOperationException("セル " + i + " の状態が不正です。");
            }
            VerifyThermalResistance();
            var model = Resources.Load<TextAsset>("ManaWeave/magic_model");
            if (model == null) throw new FileNotFoundException("短文解析用の学習済みモデルが見つかりません。");
            var parser = new MagicParser(model.bytes);
            var result = parser.Parse("heat r=2 power=120000");
            if (!result.Success || result.Operation != SpellOperation.Heat)
                throw new InvalidOperationException("魔法解析器の検証に失敗しました。");
            var directAsset = Resources.Load<TextAsset>("ManaWeave/wsp_model");
            if (directAsset == null) throw new FileNotFoundException("詠唱解析用の直接生成モデルが見つかりません。");
            var direct = new WeightSpaceMagic(directAsset.bytes);
            var directResult = direct.Parse("目の前を小さく熱して");
            if (!directResult.Success || directResult.Operation != SpellOperation.Heat)
                throw new InvalidOperationException("直接生成モデルの検証に失敗しました。");
            if (Resources.Load<Shader>("ManaWeave/Voxel") == null)
                throw new FileNotFoundException("ManaWeaveの地形描画用シェーダーが見つかりません。");
            string path = Path.Combine(Application.temporaryCachePath, "manaweave_verification.mw");
            try
            {
                WorldPersistence.Save(world, path);
                WorldState restored = WorldPersistence.Load(path);
                if (restored.Tick != world.Tick || restored.Cells.Length != world.Cells.Length)
                    throw new InvalidOperationException("世界データの保存・読み込み検証に失敗しました。");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            Debug.Log("ManaWeave：世界法則と魔法の検証に成功しました。熱の抵抗と温度下限も確認しました。" + parser.ModelStatus +
                      "。実際の操作と描画はPlayモードで確認してください。");
        }

        private static void VerifyThermalResistance()
        {
            // Isolate passive mana from conduction, fuel and commands. Both
            // disturbance polarities must move toward the reference state.
            var world = new WorldState(3, 1, 1, 731);
            world.SetMaterial(0, 0, 0, MaterialId.Stone);
            world.SetMaterial(2, 0, 0, MaterialId.Stone);
            Cell hot = world.Cells[0], cold = world.Cells[2];
            hot.Energy += 300000f;
            cold.Energy -= 300000f;
            world.Cells[0] = hot;
            world.Cells[2] = cold;
            var config = new SimulationConfig {
                ManaGeneration = 0f, ManaDiffusion = 0f, AbsorptionScale = 0f,
                ThermalDiffusion = 0f, CasterAbsorptionRate = 0f,
                EnableCombustion = false, EnableFluidFlow = false, EnablePhaseChanges = false
            };
            var sim = new WorldSimulator(world, config);
            sim.Step(0.1f);
            if (!(world.Cells[0].Energy < hot.Energy) || !(world.Cells[2].Energy > cold.Energy) ||
                sim.Stats.AmplifiedJoules != 0 || sim.Stats.StabilizedJoules <= 0)
                throw new InvalidOperationException("命令のない魔力が、加熱・冷却の両方へ抵抗する法則の検証に失敗しました。");
        }

        [MenuItem("Tools/ManaWeave/Windows版をビルド")]
        public static void BuildWindows()
        {
            Build(BuildTarget.StandaloneWindows64, "Builds/Windows/ManaWeave.exe");
        }

        [MenuItem("Tools/ManaWeave/Linux版をビルド")]
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
                throw new InvalidOperationException("ManaWeaveのビルドが完了しませんでした。Console（コンソール）の結果を確認してください。");
            Debug.Log("ManaWeaveのビルドが完了しました：" + Path.GetFullPath(output));
        }

        private static bool Finite(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
