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
        public int schema = 1;
        public int seed;
        public long tick;
        public float x, y, z, qx, qy, qz, qw;
        public int mode;
        public bool stabilization, resonance, crystal, alloy, observed;
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
        public string ModelStatus { get { return _parser != null ? (UseDirectWeights ? _parser.ModelStatus : _parser.LearnedModelStatus) : _modelError; } }
        public int SuccessfulCasts { get; private set; }
        public bool ObservedStabilization { get; private set; }
        public bool ObservedResonance { get; private set; }
        public bool ChargedCrystal { get; private set; }
        public bool CreatedAlloy { get; private set; }
        public bool ObservedMaterial { get; private set; }
        public bool WorldSaved { get; private set; }
        public float SmoothedFps { get; private set; }
        public IReadOnlyList<JournalLine> Journal { get { return _journal; } }
        public string SavePath { get { return Path.Combine(Application.persistentDataPath,"ManaWeave","world.mwsv"); } }

        public static readonly SpellRecipe[] Presets = {
            new SpellRecipe { Name="THERMAL", Description="Add heat", Source="Preset / explicit operation", Operation=SpellOperation.Heat, Radius=1, Power=120000, Material=MaterialId.Air },
            new SpellRecipe { Name="CRYOSTASIS", Description="Remove heat", Source="Preset / explicit operation", Operation=SpellOperation.Cool, Radius=1, Power=120000, Material=MaterialId.Air },
            new SpellRecipe { Name="EXCAVATE", Description="Remove material", Source="Preset / explicit operation", Operation=SpellOperation.Excavate, Radius=1, Power=0, Material=MaterialId.Air },
            new SpellRecipe { Name="CONSTRUCT", Description="Place stone", Source="Preset / explicit operation", Operation=SpellOperation.Build, Radius=1, Power=0, Material=MaterialId.Stone },
            new SpellRecipe { Name="INFUSE", Description="Store mana", Source="Preset / explicit operation", Operation=SpellOperation.Charge, Radius=1, Power=80, Material=MaterialId.Crystal },
            new SpellRecipe { Name="ALCHEMY", Description="Bind charged iron", Source="Preset / explicit operation", Operation=SpellOperation.Transmute, Radius=1, Power=0, Material=MaterialId.Alloy }
        };

        private MagicParser _parser;
        private string _modelError = "Model not loaded";
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

        private void Awake()
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 1;
            try
            {
                SetAtmosphere();
                World = WorldGenerator.Generate(48,24,48,731);
                Simulator = new WorldSimulator(World);
                var terrainObject = new GameObject("Voxel terrain");
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
                catch (Exception error) { _modelError="LLM unavailable: " + error.Message; Debug.LogWarning(_modelError); }
                _hud=gameObject.AddComponent<ManaHud>();
                _hud.Attach(this);
                EquipPreset(0);
                SpawnSurveyor();
                Surveyor.Capture(false);
                _initialized = true;
                Announce("Click ENTER WORLD to fly. TAB opens the spell workbench.");
                AddJournal("New research island: seed " + World.Seed + ".");
            }
            catch (Exception error)
            {
                LastMessage="Initialization failed: " + error.Message;
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
            var sunObject=new GameObject("Research island / sun");
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
                var obj=new GameObject("Surveyor camera");
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
            _selection=MakeLine("Voxel selection",0.025f);
            _radius=MakeLine("Spell footprint",0.018f);
            _probe=MakeLine("Pinned experiment probe",0.028f);
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
                AddJournal("Discovery: ambient mana can suppress a small thermal disturbance.");
            }
            if (!ObservedResonance && Simulator.Stats.AmplifiedJoules>1)
            {
                ObservedResonance=true;
                AddJournal("Discovery: a connected large disturbance can resonate with ambient mana.");
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
                Name=Parsed.Operation.ToString().ToUpperInvariant(), Description=Parsed.Canonical,
                Source=Parsed.Source, Operation=Parsed.Operation, Radius=Parsed.Radius, Power=Parsed.Power, Material=Parsed.Material
            };
            SelectedSlot=-1;
            AddJournal("Equipped parsed spell ["+Parsed.Source+"]: "+Parsed.Canonical);
            Announce("Spell equipped. Aim at the world and click to cast.");
            SetWorkbench(false);
        }

        public void Analyze(string text)
        {
            if(_parseTask!=null)return;
            Parsed=null;
            if(_parser==null) { Announce(ModelStatus,true); return; }
            if(string.IsNullOrWhiteSpace(text)) { Announce("Enter a chant or an explicit spell command.",true); return; }
            if(text.Length>240) { Announce("The input limit is 240 characters.",true); return; }
            if(UseDirectWeights&&!DirectWeightsAvailable) { Announce("The direct-weight model resource is unavailable.",true); return; }
            string request=text.Trim();
            MagicParser parser=_parser;
            bool direct=UseDirectWeights;
            _parseWatch=System.Diagnostics.Stopwatch.StartNew();
            _parseTask=Task.Run(()=>direct?parser.Parse(request):parser.ParseLearned(request));
        }

        public void SetParserMode(bool direct)
        {
            if(ParseBusy)return;
            if(direct&&!DirectWeightsAvailable) { Announce("The direct-weight model resource is unavailable.",true); return; }
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
                string message=_parseTask.Exception==null?"Unknown model error":_parseTask.Exception.GetBaseException().Message;
                Announce("Analysis failed: "+message,true);
            }
            else if(!_parseTask.IsCanceled)
            {
                Parsed=_parseTask.Result;
                Announce(Parsed!=null&&Parsed.Success?"Analysis ready. Review the command, then equip it.":"Input could not be mapped to a supported spell.",Parsed==null||!Parsed.Success);
            }
            _parseTask=null;
        }

        public void CastEquipped()
        {
            if(Equipped==null||!TargetInWorld) { Announce("Aim inside the research island.",true); return; }
            // Target IDs are resolved at cast time. Neither language inference nor a stale preview owns a world reference.
            var command=new SpellCommand {
                Operation=Equipped.Operation,X=TargetX,Y=TargetY,Z=TargetZ,
                Radius=Equipped.Radius,Power=Equipped.Power,Material=Equipped.Material
            };
            float crystalBefore=SumStoredInRadius(MaterialId.Crystal,command);
            int alloyBefore=CountInRadius(MaterialId.Alloy,command);
            SpellResult result=Simulator.Cast(command);
            Announce(result.Message+(result.Success?"  ["+result.ManaCost.ToString("0.0",CultureInfo.InvariantCulture)+" MU]":""),!result.Success);
            if(!result.Success)return;
            SuccessfulCasts++;
            if(command.Operation==SpellOperation.Charge&&SumStoredInRadius(MaterialId.Crystal,command)>crystalBefore+0.001f)ChargedCrystal=true;
            if(command.Operation==SpellOperation.Transmute&&CountInRadius(MaterialId.Alloy,command)>alloyBefore)CreatedAlloy=true;
            AddJournal(command.Operation+" at ("+TargetX+", "+TargetY+", "+TargetZ+") — "+result.Message);
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
            string text=MaterialCatalog.Get(c.Material).Name+" / "+World.Temperature(index).ToString("0.0",CultureInfo.InvariantCulture)+" °C / ambient "+c.AmbientMana.ToString("0.00",CultureInfo.InvariantCulture)+" MU / stored "+c.StoredMana.ToString("0.00",CultureInfo.InvariantCulture)+" MU";
            Announce(text);
            if(_scanned.Add(c.Material))AddJournal("Material observation: "+text);
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
            string[] titles={"Small disturbance / stabilization","Large disturbance / resonance","Alchemy preparation","Direct ambient action in empty space"};
            string next=id==2?". Simulation paused: equip 6, cast on iron, then P to resume.":(WorkbenchOpen?". Press TAB to return to the world.":". Read the pinned probe.");
            Announce("Lab fixture prepared: "+titles[Mathf.Clamp(id,0,3)]+next);
            AddJournal("Lab fixture: "+Simulator.Stats.LastEvent);
            Paused=id==2;
        }

        public void NewWorld()
        {
            int next=World.Seed+1;
            ReplaceWorld(WorldGenerator.Generate(48,24,48,next));
            _journal.Clear();_scanned.Clear();
            SuccessfulCasts=0;
            ObservedStabilization=ObservedResonance=ChargedCrystal=CreatedAlloy=ObservedMaterial=WorldSaved=false;
            SpawnSurveyor();
            AddJournal("New island created: seed "+next+".");
            Announce("New island generated. The existing save has not been overwritten.");
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
                    stabilization=ObservedStabilization,resonance=ObservedResonance,crystal=ChargedCrystal,alloy=CreatedAlloy,
                    observed=ObservedMaterial,successfulCasts=SuccessfulCasts,spell=Equipped==null?null:Equipped.Copy(),selectedSlot=SelectedSlot
                };
                string viewPath=Path.ChangeExtension(SavePath,"view.json");
                File.WriteAllText(viewPath+".tmp",JsonUtility.ToJson(bookmark,true));
                if(File.Exists(viewPath))File.Delete(viewPath);
                File.Move(viewPath+".tmp",viewPath);
                WorldSaved=true;
                Announce("World saved. F9 restores this snapshot.");
                AddJournal("Snapshot saved at tick "+World.Tick+".");
            }
            catch(Exception error) { Announce("Save failed: "+error.Message,true); }
        }

        public void LoadWorld()
        {
            try
            {
                // Load validates the entire snapshot before replacing the active world.
                WorldState loaded=WorldPersistence.Load(SavePath);
                ReplaceWorld(loaded);
                ObservedStabilization=ObservedResonance=ChargedCrystal=CreatedAlloy=ObservedMaterial=false;
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
                        if(v!=null&&v.schema==1&&v.seed==World.Seed&&v.tick==World.Tick&&Finite(v.x)&&Finite(v.y)&&Finite(v.z)&&Finite(v.qx)&&Finite(v.qy)&&Finite(v.qz)&&Finite(v.qw))
                        {
                            var rotation=new Quaternion(v.qx,v.qy,v.qz,v.qw);
                            if(rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w>0.01f)
                                Surveyor.SetPose(new Vector3(Mathf.Clamp(v.x,-20,World.Width+20),Mathf.Clamp(v.y,0.8f,World.Height+30),Mathf.Clamp(v.z,-20,World.Depth+20)),rotation.normalized);
                            Terrain.SetMode((ObservationMode)Mathf.Clamp(v.mode,0,2));
                            ObservedStabilization=v.stabilization;ObservedResonance=v.resonance;ChargedCrystal=v.crystal;CreatedAlloy=v.alloy;ObservedMaterial=v.observed;
                            SuccessfulCasts=Math.Max(0,v.successfulCasts);
                            RestoreSpell(v.spell,v.selectedSlot);
                        }
                    }
                }
                catch(Exception) { /* A missing/broken camera bookmark does not invalidate a valid world. */ }
                WorldSaved=true;
                Announce("Snapshot restored. Simulation resumes from its saved tick.");
                AddJournal("Snapshot restored at tick "+World.Tick+".");
            }
            catch(Exception error) { Announce("Load failed; current world retained: "+error.Message,true); }
        }

        private static bool Finite(float value) { return !float.IsNaN(value)&&!float.IsInfinity(value); }
        private void RestoreSpell(SpellRecipe saved,int slot)
        {
            if(saved==null||!Enum.IsDefined(typeof(SpellOperation),saved.Operation)||!Enum.IsDefined(typeof(MaterialId),saved.Material)||saved.Radius<1||saved.Radius>5||!Finite(saved.Power)||saved.Power<0)return;
            Equipped=saved.Copy();
            Equipped.Name=BoundedText(saved.Name,64,saved.Operation.ToString().ToUpperInvariant());
            Equipped.Description=BoundedText(saved.Description,512,"Restored spell");
            Equipped.Source=BoundedText(saved.Source,128,"Saved spell");
            SelectedSlot=slot>=0&&slot<Presets.Length&&Presets[slot].Operation==saved.Operation?slot:-1;
        }
        private static string BoundedText(string value,int maximum,string fallback)
        {
            if(string.IsNullOrWhiteSpace(value))return fallback;
            return value.Length<=maximum?value:value.Substring(0,maximum);
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
            GUI.Box(new Rect(20,20,Mathf.Max(300,Screen.width-40),120),LastMessage??"Initializing ManaWeave…");
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
