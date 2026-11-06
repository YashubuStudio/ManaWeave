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
            VerifyExpressiveMagic(new MagicParser(model.bytes, direct.Parse));
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
            Debug.Log("ManaWeave：世界法則と魔法の検証に成功しました。熱抵抗・温度下限・錬金用加熱・詠唱効率・物質移動を確認しました。" + parser.ModelStatus +
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

        private static void VerifyExpressiveMagic(MagicParser parser)
        {
            var heat=parser.Parse("紅蓮を統べる精霊よ、我が呼び声に応えよ。照準先の鉄を三百五十度まで熱し、十秒間その熱を守れ。");
            var movement=parser.Parse("対象を上へ3メートル移動させよ。");
            if(!heat.Success||heat.Operation!=SpellOperation.Heat||heat.ThermalMode!=ThermalMode.Temperature||
                heat.TargetTemperature!=350||heat.DurationSeconds!=10||heat.Radius!=0||
                !heat.HasMaterialFilter||heat.TargetMaterial!=MaterialId.Iron||
                !movement.Success||movement.Operation!=SpellOperation.Move||movement.Direction!=SpellDirection.Up||movement.Distance!=3)
                throw new InvalidOperationException("温度・保護時間・素材・移動の詠唱解析の検証に失敗しました。");
            var world=new WorldState(9,7,9,731);
            world.SetMaterial(4,2,4,MaterialId.Iron);
            var sim=new WorldSimulator(world);
            var command=new SpellCommand {
                Operation=heat.Operation,X=4,Y=2,Z=4,Radius=heat.Radius,Power=heat.Power,Material=heat.Material,
                ThermalMode=heat.ThermalMode,TargetTemperature=heat.TargetTemperature,DurationSeconds=heat.DurationSeconds,
                HasMaterialFilter=heat.HasMaterialFilter,TargetMaterial=heat.TargetMaterial,Incantation=heat.Incantation
            };
            SpellQuote quote=sim.Quote(command);
            if(!quote.Success||quote.ManaCost>=quote.BaseManaCost||quote.EffectiveChantLength<=0||!sim.Cast(command).Success)
                throw new InvalidOperationException("加熱の費用見積りまたは詠唱効率の検証に失敗しました。");
            for(int i=0;i<50;i++)sim.Step(.1f);
            if(!sim.Cast(new SpellCommand {Operation=SpellOperation.Charge,X=4,Y=2,Z=4,Radius=0,Power=10}).Success)
                throw new InvalidOperationException("錬金用の魔力充填に失敗しました。");
            for(int i=0;i<10;i++)sim.Step(.1f);
            if(!sim.Cast(new SpellCommand {Operation=SpellOperation.Transmute,X=4,Y=2,Z=4,Radius=0,Power=0}).Success)
                throw new InvalidOperationException("通常の加熱・充填から錬成までの操作時間を確保できませんでした。");
            double energy=world.TotalEnergy(),mass=world.TotalMass();
            var move=new SpellCommand {Operation=SpellOperation.Move,X=4,Y=2,Z=4,Radius=0,Power=0,OffsetY=3,Incantation=movement.Incantation};
            if(!sim.Cast(move).Success||world.Cells[world.Index(4,5,4)].Material!=MaterialId.Alloy||
                Math.Abs(world.TotalEnergy()-energy)>.0001||Math.Abs(world.TotalMass()-mass)>.00001)
                throw new InvalidOperationException("物質移動の位置または熱・質量収支の検証に失敗しました。");
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
