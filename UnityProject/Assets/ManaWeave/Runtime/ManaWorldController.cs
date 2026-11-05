using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using ManaWeave.Core;
using ManaWeave.Magic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ManaWeave.Runtime
{
    [Serializable]
    public sealed class SpellRecipe
    {
        public string Name, Description, Source;
        public SpellOperation Operation;
        public int Radius;
        public float Power;
        public MaterialId Material;

        public SpellRecipe Copy()
        {
            return new SpellRecipe { Name=Name, Description=Description, Source=Source, Operation=Operation, Radius=Radius, Power=Power, Material=Material };
        }
    }

    public sealed class JournalLine
    {
        public long Tick;
        public string Text;
        public JournalLine(long tick, string text) { Tick=tick; Text=text; }
    }

    [Serializable]
    internal sealed class SurveyBookmark
    {
        public int schema = 2;
        public int seed;
        public long tick;
        public float x, y, z, qx, qy, qz, qw;
        public int mode;
        public bool stabilization, propagationResistance, crystal, alloy, observed;
        public int successfulCasts;
        public SpellRecipe spell;
        public int selectedSlot = -1;
    }

    /// <summary>Unity presentation adapter. Only WorldSimulator changes simulated phenomena.</summary>
    public sealed class ManaWorldController : MonoBehaviour
    {
        public WorldState World { get; private set; }
        public WorldSimulator Simulator { get; private set; }
        public VoxelWorldRenderer Terrain { get; private set; }
        public SurveyCamera Surveyor { get; private set; }
        public Camera ViewCamera { get; private set; }
        public bool WorkbenchOpen { get; private set; }
        public bool HelpOpen = true;
        public bool Paused;
        public float SimulationSpeed = 1;
        public int SelectedSlot { get; private set; }
        public SpellRecipe Equipped { get; private set; }
        public VoxelHit Aim { get; private set; }
        public int TargetX { get; private set; }
        public int TargetY { get; private set; }
        public int TargetZ { get; private set; }
        public bool TargetInWorld { get; private set; }
        public bool ProbePinned { get; private set; }
        public int ProbeX { get; private set; }
        public int ProbeY { get; private set; }
        public int ProbeZ { get; private set; }
        public int ObservationX { get { return ProbePinned?ProbeX:TargetX; } }
        public int ObservationY { get { return ProbePinned?ProbeY:TargetY; } }
        public int ObservationZ { get { return ProbePinned?ProbeZ:TargetZ; } }
        public bool ObservationInWorld { get { return World!=null&&World.InBounds(ObservationX,ObservationY,ObservationZ); } }
        public string LastMessage { get; private set; }
        public bool LastMessageError { get; private set; }
        public float LastMessageTime { get; private set; }
        public MagicParseResult Parsed { get; private set; }
        public bool UseDirectWeights = true;
        public bool DirectWeightsAvailable { get; private set; }
        public bool ParseBusy { get { return _parseTask != null; } }
        public double ParseMilliseconds { get; private set; }
        public string ModelStatus
        {
            get
            {
                if (_parser == null) return _modelError;
                if (UseDirectWeights)
                    return DirectWeightsAvailable ? "詠唱を" + MagicParser.MaxInputLength + "文字まで入力できます。改行も使えます。" :
                        "詠唱解析のデータがありません。短文解析または数値指定を使ってください。";
                return _parser.LearnedModelAvailable ? "短文を" + MagicParser.MaxLearnedInputLength + "文字まで解析します。長い詠唱は「詠唱解析」へ。" :
                    "短文解析のデータがありません。数値指定の術式は使えます。";
            }
        }
        public int SuccessfulCasts { get; private set; }
        public bool ObservedStabilization { get; private set; }
        public bool ObservedPropagationResistance { get; private set; }
        public bool ChargedCrystal { get; private set; }
        public bool CreatedAlloy { get; private set; }
        public bool ObservedMaterial { get; private set; }
        public bool WorldSaved { get; private set; }
        public float SmoothedFps { get; private set; }
        public IReadOnlyList<JournalLine> Journal { get { return _journal; } }
        public string SavePath { get { return Path.Combine(Application.persistentDataPath,"ManaWeave","world.mwsv"); } }

        public static readonly SpellRecipe[] Presets = {
            new SpellRecipe { Name="加熱", Description="熱を加える", Source="基本術式", Operation=SpellOperation.Heat, Radius=1, Power=120000, Material=MaterialId.Air },
            new SpellRecipe { Name="冷却", Description="熱を取り除く", Source="基本術式", Operation=SpellOperation.Cool, Radius=1, Power=120000, Material=MaterialId.Air },
            new SpellRecipe { Name="掘削", Description="物質を取り除く", Source="基本術式", Operation=SpellOperation.Excavate, Radius=1, Power=0, Material=MaterialId.Air },
            new SpellRecipe { Name="建築", Description="素材を配置する", Source="基本術式", Operation=SpellOperation.Build, Radius=1, Power=0, Material=MaterialId.Stone },
            new SpellRecipe { Name="魔力充填", Description="物質へ魔力を移す", Source="基本術式", Operation=SpellOperation.Charge, Radius=1, Power=80, Material=MaterialId.Crystal },
            new SpellRecipe { Name="錬成", Description="鉄を魔導合金へ", Source="基本術式", Operation=SpellOperation.Transmute, Radius=1, Power=0, Material=MaterialId.Alloy }
        };

        private MagicParser _parser;
        private string _modelError = "解析データを読み込んでいます。";
        private Task<MagicParseResult> _parseTask;
        private System.Diagnostics.Stopwatch _parseWatch;
        private readonly List<JournalLine> _journal = new List<JournalLine>();
        private readonly HashSet<MaterialId> _scanned = new HashSet<MaterialId>();
        private float _accumulator;
        private float _castAllowedAfter;
        private LineRenderer _selection, _radius, _probe;
        private Material _selectionMaterial, _skyMaterial;
        private bool _initialized;
        private ManaHud _hud;
        private GUIStyle _startupStyle;

        private void Awake()
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 1;
            try
            {
                SetAtmosphere();
                World = WorldGenerator.Generate(48,24,48,731);
                Simulator = new WorldSimulator(World);
                var terrainObject = new GameObject("ボクセル地形");
                terrainObject.transform.SetParent(transform,false);
                Terrain = terrainObject.AddComponent<VoxelWorldRenderer>();
                Terrain.Attach(World);
                SetupCamera();
                SetupSelection();
                var model = Resources.Load<TextAsset>("ManaWeave/magic_model");
                try
                {
                    var directBytes=Resources.Load<TextAsset>("ManaWeave/wsp_model");
                    Func<string,MagicParseResult> direct=null;
                    if(directBytes!=null)
                    {
                        var directModel=new WeightSpaceMagic(directBytes.bytes);
                        direct=directModel.Parse;
                        DirectWeightsAvailable=true;
                    }
                    else UseDirectWeights=false;
                    _parser = new MagicParser(model == null ? null : model.bytes,direct);
                }
                catch (Exception error)
                {
                    _modelError="解析器を読み込めませんでした。基本術式（1～6）を使用してください。";
                    Debug.LogWarning(_modelError);
                    Debug.LogException(error);
                }
                _hud=gameObject.AddComponent<ManaHud>();
                _hud.Attach(this);
                EquipPreset(0);
                SpawnSurveyor();
                Surveyor.Capture(false);
                _initialized = true;
                Announce("「世界へ入る」で移動を開始します。Tabで魔法工房を開けます。");
                AddJournal("研究用の島を生成しました。生成番号：" + World.Seed + "。");
                AddJournal("世界法則：命令のない魔力は熱の変化を抑えます。加熱・冷却は指定範囲へ直接作用し、範囲外への伝播には強い抵抗が働きます。");
            }
            catch (Exception error)
            {
                LastMessage="世界の初期化に失敗しました。UnityのConsole（コンソール）で詳細を確認してください。";
                LastMessageError=true;
                Debug.LogException(error);
            }
        }

        private void SetAtmosphere()
        {
            RenderSettings.ambientMode=AmbientMode.Flat;
            RenderSettings.ambientLight=new Color(0.40f,0.48f,0.62f);
            RenderSettings.fog=true;
            RenderSettings.fogMode=FogMode.Linear;
            RenderSettings.fogColor=new Color(0.19f,0.29f,0.35f);
            RenderSettings.fogStartDistance=48;
            RenderSettings.fogEndDistance=145;
            Shader sky = Resources.Load<Shader>("ManaWeave/Sky");
            if (sky != null) { _skyMaterial=new Material(sky); RenderSettings.skybox=_skyMaterial; }
            var sunObject=new GameObject("研究島の太陽");
            sunObject.transform.SetParent(transform,false);
            sunObject.transform.rotation=Quaternion.Euler(43,-34,0);
            var light=sunObject.AddComponent<Light>();
            light.type=LightType.Directional;
            light.intensity=1.12f;
            light.color=new Color(1f,0.90f,0.75f);
            light.shadows=LightShadows.None;
            RenderSettings.sun=light;
        }

        private void SetupCamera()
        {
            ViewCamera=Camera.main;
            if (ViewCamera == null)
            {
                var obj=new GameObject("調査者のカメラ");
                obj.tag="MainCamera";
                ViewCamera=obj.AddComponent<Camera>();
            }
            ViewCamera.transform.SetParent(transform,false);
            ViewCamera.clearFlags=CameraClearFlags.Skybox;
            ViewCamera.backgroundColor=new Color(0.12f,0.19f,0.27f);
            ViewCamera.nearClipPlane=0.08f;
            ViewCamera.farClipPlane=190;
            ViewCamera.fieldOfView=64;
            if (ViewCamera.GetComponent<AudioListener>() == null) ViewCamera.gameObject.AddComponent<AudioListener>();
            Surveyor=ViewCamera.GetComponent<SurveyCamera>();
            if (Surveyor == null) Surveyor=ViewCamera.gameObject.AddComponent<SurveyCamera>();
            Surveyor.Attach(this);
        }

        private void SetupSelection()
        {
            Shader shader=Resources.Load<Shader>("ManaWeave/Aura");
            _selectionMaterial=new Material(shader);
            _selection=MakeLine("対象セルの枠",0.025f);
            _radius=MakeLine("術式の効果範囲",0.018f);
            _probe=MakeLine("固定した実験観測点",0.028f);
            _probe.enabled=false;
        }

        private LineRenderer MakeLine(string label,float width)
        {
            var obj=new GameObject(label);
            obj.transform.SetParent(transform,false);
            var line=obj.AddComponent<LineRenderer>();
            line.sharedMaterial=_selectionMaterial;
            line.useWorldSpace=true;
            line.startWidth=width;
            line.endWidth=width;
            line.numCornerVertices=2;
            line.shadowCastingMode=ShadowCastingMode.Off;
            line.receiveShadows=false;
            return line;
        }

        private void Update()
        {
            if (!_initialized) return;
            float frame=Mathf.Max(Time.unscaledDeltaTime,0.001f);
            SmoothedFps=Mathf.Lerp(SmoothedFps,1/frame,0.05f);
            HandleKeys();
            CompleteParse();
            if (!Paused)
            {
                _accumulator+=Mathf.Min(Time.unscaledDeltaTime,0.25f)*SimulationSpeed;
                int steps=0;
                while (_accumulator>=0.1f && steps<4)
                {
                    Vector3 p=Surveyor.transform.position;
                    World.CasterX=Mathf.Clamp(Mathf.FloorToInt(p.x),0,World.Width-1);
                    World.CasterY=Mathf.Clamp(Mathf.FloorToInt(p.y),0,World.Height-1);
                    World.CasterZ=Mathf.Clamp(Mathf.FloorToInt(p.z),0,World.Depth-1);
                    Simulator.Step(0.1f);
                    _accumulator-=0.1f;
                    steps++;
                }
                // Keep the simulation responsive after focus loss; simulation time advances only on actual steps.
                if (_accumulator>0.5f) _accumulator=0.5f;
            }
            if (!ObservedStabilization && Simulator.Stats.StabilizedJoules>1)
            {
                ObservedStabilization=true;
                AddJournal("発見：命令のない魔力が、熱の変化を基準状態へ戻す抵抗として働きました。");
            }
            if (!ObservedPropagationResistance && Simulator.Stats.SuppressedTransportJoules>1)
            {
                ObservedPropagationResistance=true;
                AddJournal("発見：命令のない空間魔力が、隣のセルへ伝わる熱を抑えました。");
            }
            Terrain.Synchronize(Time.unscaledDeltaTime);
        }

        private void LateUpdate()
        {
            if (!_initialized) return;
            UpdateAim();
            if (Surveyor.Captured && !WorkbenchOpen && Time.unscaledTime >= _castAllowedAfter && Input.GetMouseButtonDown(0)) CastEquipped();
            if (Surveyor.Captured && !WorkbenchOpen && Input.GetMouseButtonDown(1)) SetWorkbench(true);
        }

        private void HandleKeys()
        {
            if (Input.GetKeyDown(KeyCode.Tab)) SetWorkbench(!WorkbenchOpen);
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (WorkbenchOpen) SetWorkbench(false);
                else Surveyor.Capture(!Surveyor.Captured);
            }
            if (WorkbenchOpen) return;
            if (Input.GetKeyDown(KeyCode.F1)) HelpOpen=!HelpOpen;
            if (Input.GetKeyDown(KeyCode.F2)) CycleObservation();
            if (Input.GetKeyDown(KeyCode.F5)) SaveWorld();
            if (Input.GetKeyDown(KeyCode.F9)) LoadWorld();
            if (Input.GetKeyDown(KeyCode.F6)) RunExperiment(0);
            if (Input.GetKeyDown(KeyCode.F7)) RunExperiment(1);
            if (Input.GetKeyDown(KeyCode.F8)) RunExperiment(2);
            if (Input.GetKeyDown(KeyCode.F10)) RunExperiment(3);
            if (Input.GetKeyDown(KeyCode.P)) Paused=!Paused;
            if (Input.GetKeyDown(KeyCode.E)) ObserveTarget();
            for (int i=0;i<6;i++)
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1+i))) EquipPreset(i);
            if (Surveyor.Captured)
            {
                float wheel=Input.mouseScrollDelta.y;
                if (wheel!=0 && Equipped!=null) Equipped.Radius=Mathf.Clamp(Equipped.Radius+(wheel>0?1:-1),1,5);
            }
        }

        private void UpdateAim()
        {
            Ray ray=ViewCamera.ViewportPointToRay(new Vector3(0.5f,0.5f,0));
            Aim=VoxelRay.Trace(World,ray,90);
            if (Aim.HasHit)
            {
                if (Equipped!=null && Equipped.Operation==SpellOperation.Build)
                {
                    TargetX=Aim.AdjacentX;TargetY=Aim.AdjacentY;TargetZ=Aim.AdjacentZ;
                }
                else { TargetX=Aim.X;TargetY=Aim.Y;TargetZ=Aim.Z; }
            }
            else
            {
                Vector3 p=ray.origin+ray.direction*8;
                TargetX=Mathf.FloorToInt(p.x);TargetY=Mathf.FloorToInt(p.y);TargetZ=Mathf.FloorToInt(p.z);
            }
            TargetInWorld=World.InBounds(TargetX,TargetY,TargetZ);
            _probe.enabled=ProbePinned;
            if(ProbePinned)
            {
                Vector3 probeCenter=new Vector3(ProbeX+0.5f,ProbeY+0.5f,ProbeZ+0.5f);
                _probe.startColor=new Color(0.70f,0.50f,1f,0.85f);
                _probe.endColor=_probe.startColor;
                _probe.positionCount=33;
                for(int i=0;i<=32;i++)
                {
                    float a=i/32f*Mathf.PI*2;
                    _probe.SetPosition(i,probeCenter+new Vector3(Mathf.Cos(a)*0.7f,Mathf.Sin(a)*0.7f,0));
                }
            }
            _selection.enabled=TargetInWorld;
            _radius.enabled=TargetInWorld && Equipped!=null;
            if (!TargetInWorld) return;
            Vector3 o=new Vector3(TargetX,TargetY,TargetZ);
            float inset=-0.015f,edge=1.03f;
            Vector3[] corners={
                o+new Vector3(inset,inset,inset),o+new Vector3(inset+edge,inset,inset),
                o+new Vector3(inset+edge,inset+edge,inset),o+new Vector3(inset,inset+edge,inset),
                o+new Vector3(inset,inset,inset+edge),o+new Vector3(inset+edge,inset,inset+edge),
                o+new Vector3(inset+edge,inset+edge,inset+edge),o+new Vector3(inset,inset+edge,inset+edge)
            };
            int[] trace={0,1,2,3,0,4,5,1,5,6,2,6,7,3,7,4};
            _selection.positionCount=trace.Length;
            for(int i=0;i<trace.Length;i++)_selection.SetPosition(i,corners[trace[i]]);
            Color color=SpellColor(Equipped==null?SpellOperation.Heat:Equipped.Operation);
            _selection.startColor=color;_selection.endColor=color;
            _radius.positionCount=65;
            _radius.startColor=new Color(color.r,color.g,color.b,0.5f);
            _radius.endColor=_radius.startColor;
            Vector3 center=o+new Vector3(0.5f,1.04f,0.5f);
            float radius=(Equipped==null?1:Equipped.Radius)+0.5f;
            for(int i=0;i<=64;i++)
            {
                float angle=i/64f*Mathf.PI*2;
                _radius.SetPosition(i,center+new Vector3(Mathf.Cos(angle)*radius,0,Mathf.Sin(angle)*radius));
            }
        }

        public void SetWorkbench(bool open)
        {
            WorkbenchOpen=open;
            if (!open) _castAllowedAfter=Time.unscaledTime+0.12f;
            if (Surveyor!=null) Surveyor.Capture(!open);
        }

        public void EnterWorld()
        {
            WorkbenchOpen=false;
            _castAllowedAfter=Time.unscaledTime+0.12f;
            HelpOpen=false;
            Surveyor.Capture(true);
        }

        public void EquipPreset(int slot)
        {
            if(slot<0||slot>=Presets.Length)return;
            SelectedSlot=slot;
            Equipped=Presets[slot].Copy();
        }

        public void EquipParsed()
        {
            if(Parsed==null||!Parsed.Success)return;
            Equipped=new SpellRecipe {
                Name=JapaneseNames.Operation(Parsed.Operation),
                Source=LocalizedSource(Parsed.Source,"解析した術式"), Operation=Parsed.Operation, Radius=Parsed.Radius, Power=Parsed.Power, Material=Parsed.Material
            };
            Equipped.Description=DescribeSpell(Equipped);
            SelectedSlot=-1;
            AddJournal("術式を装備しました［"+Equipped.Source+"］："+Equipped.Description);
            Announce("術式を装備しました。対象を狙い、左クリックで発動できます。");
            SetWorkbench(false);
        }

        public void Analyze(string text)
        {
            if(_parseTask!=null)return;
            Parsed=null;
            if(_parser==null) { Announce(ModelStatus,true); return; }
            if(string.IsNullOrWhiteSpace(text)) { Announce("詠唱文を入力するか、数値指定の術式を入力してください。",true); return; }
            if(text.Length>MagicParser.MaxInputLength) { Announce("入力は"+MagicParser.MaxInputLength+"文字以内にしてください。",true); return; }
            if(UseDirectWeights&&!DirectWeightsAvailable) { Announce("詠唱解析のデータがありません。",true); return; }
            string request=text.Trim();
            MagicParser parser=_parser;
            bool direct=UseDirectWeights;
            _parseWatch=System.Diagnostics.Stopwatch.StartNew();
            _parseTask=Task.Run(()=>direct?parser.Parse(request):parser.ParseLearned(request));
        }

        public void SetParserMode(bool direct)
        {
            if(ParseBusy)return;
            if(direct&&!DirectWeightsAvailable) { Announce("詠唱解析のデータがありません。",true); return; }
            UseDirectWeights=direct;
            Parsed=null;
        }

        private void CompleteParse()
        {
            if(_parseTask==null||!_parseTask.IsCompleted)return;
            _parseWatch.Stop();
            ParseMilliseconds=_parseWatch.Elapsed.TotalMilliseconds;
            if(_parseTask.IsFaulted)
            {
                if(_parseTask.Exception!=null)Debug.LogException(_parseTask.Exception.GetBaseException());
                Announce("解析中にエラーが発生しました。入力を確認し、もう一度解析してください。",true);
            }
            else if(!_parseTask.IsCanceled)
            {
                Parsed=_parseTask.Result;
                Announce(Parsed!=null&&Parsed.Success?"解析できました。効果と範囲を確認し、術式を装備してください。":"この入力は術式にできませんでした。魔法工房の説明を確認してください。",Parsed==null||!Parsed.Success);
            }
            _parseTask=null;
        }

        public void CastEquipped()
        {
            if(Equipped==null||!TargetInWorld) { Announce("術式を装備し、研究島の範囲内を狙ってください。",true); return; }
            // Target IDs are resolved at cast time. Neither language inference nor a stale preview owns a world reference.
            var command=new SpellCommand {
                Operation=Equipped.Operation,X=TargetX,Y=TargetY,Z=TargetZ,
                Radius=Equipped.Radius,Power=Equipped.Power,Material=Equipped.Material
            };
            float crystalBefore=SumStoredInRadius(MaterialId.Crystal,command);
            int alloyBefore=CountInRadius(MaterialId.Alloy,command);
            SpellResult result=Simulator.Cast(command);
            Announce(result.Message+(result.Success?"  ［体内魔力 −"+result.ManaCost.ToString("0.0",CultureInfo.InvariantCulture)+" MU］":""),!result.Success);
            if(!result.Success)return;
            SuccessfulCasts++;
            if(command.Operation==SpellOperation.Charge&&SumStoredInRadius(MaterialId.Crystal,command)>crystalBefore+0.001f)ChargedCrystal=true;
            if(command.Operation==SpellOperation.Transmute&&CountInRadius(MaterialId.Alloy,command)>alloyBefore)CreatedAlloy=true;
            AddJournal(JapaneseNames.Operation(command.Operation)+"（座標 "+TargetX+", "+TargetY+", "+TargetZ+"）："+result.Message);
        }

        private float SumStoredInRadius(MaterialId material,SpellCommand command)
        {
            float sum=0;
            for(int z=command.Z-command.Radius;z<=command.Z+command.Radius;z++)
            for(int y=command.Y-command.Radius;y<=command.Y+command.Radius;y++)
            for(int x=command.X-command.Radius;x<=command.X+command.Radius;x++)
            {
                if(!World.InBounds(x,y,z))continue;
                Cell c=World.Cells[World.Index(x,y,z)];
                if(c.Material==material)sum+=c.StoredMana;
            }
            return sum;
        }

        private int CountInRadius(MaterialId material,SpellCommand command)
        {
            int count=0;
            for(int z=command.Z-command.Radius;z<=command.Z+command.Radius;z++)
            for(int y=command.Y-command.Radius;y<=command.Y+command.Radius;y++)
            for(int x=command.X-command.Radius;x<=command.X+command.Radius;x++)
                if(World.InBounds(x,y,z)&&World.Cells[World.Index(x,y,z)].Material==material)count++;
            return count;
        }

        public void ObserveTarget()
        {
            if(!ObservationInWorld)return;
            int index=World.Index(ObservationX,ObservationY,ObservationZ);
            Cell c=World.Cells[index];
            ObservedMaterial=true;
            string text=JapaneseNames.Material(c.Material)+" / "+World.Temperature(index).ToString("0.0",CultureInfo.InvariantCulture)+" °C / 空間魔力 "+c.AmbientMana.ToString("0.00",CultureInfo.InvariantCulture)+" MU / 保有魔力 "+c.StoredMana.ToString("0.00",CultureInfo.InvariantCulture)+" MU";
            Announce(text);
            if(_scanned.Add(c.Material))AddJournal("物質を観測："+text);
        }

        public void ReleaseProbe() { ProbePinned=false; }

        public void CycleObservation()
        {
            SetObservation((ObservationMode)(((int)Terrain.Mode+1)%3));
        }

        public void SetObservation(ObservationMode mode) { Terrain.SetMode(mode); }

        public void RunExperiment(int id)
        {
            Simulator.RunExperiment(id);
            ProbeX=Simulator.LastExperimentCenterX;ProbeY=Simulator.LastExperimentCenterY;ProbeZ=Simulator.LastExperimentCenterZ;
            ProbePinned=true;
            Vector3 point=new Vector3(Simulator.LastExperimentCenterX+0.5f,Simulator.LastExperimentCenterY+0.5f,Simulator.LastExperimentCenterZ+0.5f);
            Surveyor.Focus(point,new Vector3(7,7,-10));
            Terrain.SetMode(id==3?ObservationMode.Mana:ObservationMode.Temperature);
            string[] titles={"小規模な熱変化への抵抗","大規模な熱変化への抵抗","錬成の準備","空間魔力の直接抵抗"};
            string next=id==2?"。時間を停止しました。6で錬成を装備して鉄へ発動し、Pで再開してください。":(WorkbenchOpen?"。Tabで世界に戻れます。":"。固定された観測点の数値を確認してください。");
            Announce("実験を準備しました："+titles[Mathf.Clamp(id,0,3)]+next);
            AddJournal("実験の準備："+Simulator.Stats.LastEvent);
            Paused=id==2;
        }

        public void NewWorld()
        {
            int next=World.Seed+1;
            ReplaceWorld(WorldGenerator.Generate(48,24,48,next));
            _journal.Clear();_scanned.Clear();
            SuccessfulCasts=0;
            ObservedStabilization=ObservedPropagationResistance=ChargedCrystal=CreatedAlloy=ObservedMaterial=WorldSaved=false;
            SpawnSurveyor();
            AddJournal("新しい島を生成しました。生成番号："+next+"。");
            Announce("新しい島を生成しました。前の保存データはそのまま残っています。");
        }

        private void ReplaceWorld(WorldState world)
        {
            World=world;
            ProbePinned=false;
            Simulator=new WorldSimulator(world);
            Terrain.Attach(world);
            Terrain.SetMode(Terrain.Mode);
            _accumulator=0;
            Paused=false;
        }

        private void SpawnSurveyor()
        {
            int x=World.Width/2,z=World.Depth/2;
            int y=SurfaceY(x,z);
            Surveyor.Focus(new Vector3(x,y+0.5f,z),new Vector3(18,13,-21));
        }

        public int SurfaceY(int x,int z)
        {
            x=Mathf.Clamp(x,0,World.Width-1);z=Mathf.Clamp(z,0,World.Depth-1);
            for(int y=World.Height-1;y>=0;y--)
            {
                MaterialId material=World.Cells[World.Index(x,y,z)].Material;
                if(material!=MaterialId.Air&&material!=MaterialId.Steam)return y+1;
            }
            return 1;
        }

        public void SurveyMap(float u,float v)
        {
            int x=Mathf.Clamp(Mathf.FloorToInt(u*World.Width),0,World.Width-1);
            int z=Mathf.Clamp(Mathf.FloorToInt(v*World.Depth),0,World.Depth-1);
            Surveyor.Focus(new Vector3(x+0.5f,SurfaceY(x,z),z+0.5f),new Vector3(0,10,-10));
        }

        public void SaveWorld()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
                WorldPersistence.Save(World,SavePath);
                Vector3 p=Surveyor.transform.position;Quaternion q=Surveyor.transform.rotation;
                var bookmark=new SurveyBookmark {
                    seed=World.Seed,tick=World.Tick,x=p.x,y=p.y,z=p.z,qx=q.x,qy=q.y,qz=q.z,qw=q.w,mode=(int)Terrain.Mode,
                    stabilization=ObservedStabilization,propagationResistance=ObservedPropagationResistance,crystal=ChargedCrystal,alloy=CreatedAlloy,
                    observed=ObservedMaterial,successfulCasts=SuccessfulCasts,spell=Equipped==null?null:Equipped.Copy(),selectedSlot=SelectedSlot
                };
                string viewPath=Path.ChangeExtension(SavePath,"view.json");
                File.WriteAllText(viewPath+".tmp",JsonUtility.ToJson(bookmark,true));
                if(File.Exists(viewPath))File.Delete(viewPath);
                File.Move(viewPath+".tmp",viewPath);
                WorldSaved=true;
                Announce("世界を保存しました。F9でこの状態に戻せます。");
                AddJournal("世界を保存しました。計算回数："+World.Tick+"。");
            }
            catch(Exception error)
            {
                Debug.LogException(error);
                Announce(PersistenceFailure(error,false),true);
            }
        }

        public void LoadWorld()
        {
            try
            {
                // Load validates the entire snapshot before replacing the active world.
                WorldState loaded=WorldPersistence.Load(SavePath);
                ReplaceWorld(loaded);
                ObservedStabilization=ObservedPropagationResistance=ChargedCrystal=CreatedAlloy=ObservedMaterial=false;
                SuccessfulCasts=0;
                _journal.Clear();_scanned.Clear();
                EquipPreset(0);
                SpawnSurveyor();
                string viewPath=Path.ChangeExtension(SavePath,"view.json");
                try
                {
                    if(File.Exists(viewPath))
                    {
                        var v=JsonUtility.FromJson<SurveyBookmark>(File.ReadAllText(viewPath));
                        if(v!=null&&(v.schema==1||v.schema==2)&&v.seed==World.Seed&&v.tick==World.Tick&&Finite(v.x)&&Finite(v.y)&&Finite(v.z)&&Finite(v.qx)&&Finite(v.qy)&&Finite(v.qz)&&Finite(v.qw))
                        {
                            var rotation=new Quaternion(v.qx,v.qy,v.qz,v.qw);
                            if(rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w>0.01f)
                                Surveyor.SetPose(new Vector3(Mathf.Clamp(v.x,-20,World.Width+20),Mathf.Clamp(v.y,0.8f,World.Height+30),Mathf.Clamp(v.z,-20,World.Depth+20)),rotation.normalized);
                            Terrain.SetMode((ObservationMode)Mathf.Clamp(v.mode,0,2));
                            ObservedStabilization=v.stabilization;
                            // The old resonance objective is not evidence of the new resistance law.
                            // Schema 1 camera/spell bookmarks remain readable, but only schema 2 stores this observation.
                            ObservedPropagationResistance=v.schema==2&&v.propagationResistance;
                            ChargedCrystal=v.crystal;CreatedAlloy=v.alloy;ObservedMaterial=v.observed;
                            SuccessfulCasts=Math.Max(0,v.successfulCasts);
                            RestoreSpell(v.spell,v.selectedSlot);
                        }
                    }
                }
                catch(Exception) { /* A missing/broken camera bookmark does not invalidate a valid world. */ }
                WorldSaved=true;
                string migration=World.MigrationNotice;
                Announce("世界を読み込みました。保存時点から時間を再開します。"+(string.IsNullOrEmpty(migration)?"":"\n"+migration));
                AddJournal("保存した世界を読み込みました。計算回数："+World.Tick+"。");
                if(!string.IsNullOrEmpty(migration))AddJournal(migration);
            }
            catch(Exception error)
            {
                Debug.LogException(error);
                Announce(PersistenceFailure(error,true),true);
            }
        }

        private static bool Finite(float value) { return !float.IsNaN(value)&&!float.IsInfinity(value); }
        private void RestoreSpell(SpellRecipe saved,int slot)
        {
            if(saved==null||!Enum.IsDefined(typeof(SpellOperation),saved.Operation)||!Enum.IsDefined(typeof(MaterialId),saved.Material)||saved.Radius<1||saved.Radius>5||!Finite(saved.Power)||saved.Power<0)return;
            Equipped=saved.Copy();
            SelectedSlot=slot>=0&&slot<Presets.Length&&Presets[slot].Operation==saved.Operation?slot:-1;
            // Older view.json files stored English presentation strings. Rebuild
            // labels from the unchanged identifiers and numerical spell fields.
            Equipped.Name=JapaneseNames.Operation(saved.Operation);
            Equipped.Description=DescribeSpell(Equipped);
            Equipped.Source=LocalizedSource(saved.Source,SelectedSlot>=0?"基本術式":"保存した術式");
        }
        private static string LocalizedSource(string value,string fallback)
        {
            if(string.IsNullOrWhiteSpace(value))return fallback;
            if(value.IndexOf("WSP",StringComparison.OrdinalIgnoreCase)>=0||value.IndexOf("direct",StringComparison.OrdinalIgnoreCase)>=0||value.Contains("詠唱"))return "詠唱解析";
            if(value.IndexOf("Transformer",StringComparison.OrdinalIgnoreCase)>=0||value.IndexOf("RuneLM",StringComparison.OrdinalIgnoreCase)>=0||value.Contains("短文"))return "短文解析";
            if(value.IndexOf("DSL",StringComparison.OrdinalIgnoreCase)>=0||value.Contains("数値"))return "数値指定";
            if(value.IndexOf("Preset",StringComparison.OrdinalIgnoreCase)>=0||value.Contains("基本"))return "基本術式";
            return fallback;
        }
        private static string DescribeSpell(SpellRecipe spell)
        {
            string text=JapaneseNames.Operation(spell.Operation)+" / 半径 "+spell.Radius+" m";
            if(spell.Operation==SpellOperation.Heat)
                return text+" / 指定した総熱量 "+spell.Power.ToString("0.###",CultureInfo.InvariantCulture)+" J";
            if(spell.Operation==SpellOperation.Cool)
                return text+" / 最大除去熱量 "+spell.Power.ToString("0.###",CultureInfo.InvariantCulture)+" J";
            if(spell.Operation==SpellOperation.Charge)
                return text+" / 最大充填量 "+spell.Power.ToString("0.###",CultureInfo.InvariantCulture)+" MU";
            if(spell.Operation==SpellOperation.Build)
                return text+" / 素材："+JapaneseNames.Material(spell.Material);
            if(spell.Operation==SpellOperation.Transmute)
                return text+" / 条件を満たす鉄を魔導合金へ";
            return text+" / 対象範囲の物質を取り除く";
        }
        private static string PersistenceFailure(Exception error,bool loading)
        {
            if(error is FileNotFoundException||error is DirectoryNotFoundException)
                return loading?"保存データが見つかりません。先にF5で世界を保存してください。":"保存先が見つかりません。保存フォルダーを確認してください。";
            if(error is UnauthorizedAccessException)
                return "保存先へアクセスできません。フォルダーの読み書き権限を確認してください。";
            if(error is InvalidDataException||error is EndOfStreamException)
                return loading?"保存データを読み込めません。形式が未対応か、データが破損しています。":"世界の状態に不正な値があるため、保存できませんでした。";
            if(error is IOException)
                return loading?"保存ファイルを開けません。ファイルとアクセス権限を確認してください。":"保存処理を完了できませんでした。空き容量と書き込み権限を確認してください。";
            return loading?"世界の読み込みを完了できませんでした。UnityのConsole（コンソール）で詳細を確認してください。":"世界の保存を完了できませんでした。UnityのConsole（コンソール）で詳細を確認してください。";
        }
        private void Announce(string message,bool error=false) { LastMessage=message;LastMessageError=error;LastMessageTime=Time.unscaledTime; }
        private void AddJournal(string text)
        {
            _journal.Insert(0,new JournalLine(World==null?0:World.Tick,text));
            if(_journal.Count>50)_journal.RemoveAt(_journal.Count-1);
        }

        public static Color SpellColor(SpellOperation operation)
        {
            switch(operation)
            {
                case SpellOperation.Heat:return new Color(1f,0.63f,0.31f);
                case SpellOperation.Cool:return new Color(0.37f,0.81f,1f);
                case SpellOperation.Excavate:return new Color(0.82f,0.84f,0.90f);
                case SpellOperation.Build:return new Color(0.35f,0.88f,0.66f);
                case SpellOperation.Charge:return new Color(0.72f,0.50f,1f);
                default:return new Color(1f,0.50f,0.72f);
            }
        }

        private void OnGUI()
        {
            if(_initialized)return;
            if(_startupStyle==null)
            {
                Font font=Resources.Load<Font>("ManaWeave/Fonts/UIJapanese-Regular");
                if(font==null)font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                _startupStyle=new GUIStyle(GUI.skin.label) { font=font,fontSize=16,fontStyle=FontStyle.Normal,wordWrap=true,padding=new RectOffset(14,14,12,12) };
                _startupStyle.normal.textColor=Color.white;
            }
            Rect rect=new Rect(20,20,Mathf.Max(300,Screen.width-40),120);
            GUI.Box(rect,string.Empty);
            GUI.Label(rect,LastMessage??"ManaWeaveを準備しています…",_startupStyle);
        }

        private void OnDestroy()
        {
            if(_selectionMaterial!=null)Destroy(_selectionMaterial);
            if(_skyMaterial!=null)Destroy(_skyMaterial);
            Cursor.lockState=CursorLockMode.None;
            Cursor.visible=true;
        }
    }
}
