using System;
using System.Globalization;
using ManaWeave.Core;
using UnityEngine;

namespace ManaWeave.Runtime
{
    /// <summary>Asset-free research HUD; all readouts come from the live law engine.</summary>
    public sealed class ManaHud : MonoBehaviour
    {
        private ManaWorldController _world;
        private Font _font;
        private GUIStyle _label,_small,_title,_heading,_button,_input;
        private Vector2 _workScroll;
        private string _chant="目の前を小さく熱して";
        private bool _stylesReady;
        private bool _lastWorkbench;
        private static readonly Color Ink=new Color(0.88f,0.94f,0.98f);
        private static readonly Color Muted=new Color(0.49f,0.63f,0.73f);
        private static readonly Color Teal=new Color(0.30f,0.89f,0.79f);
        private static readonly Color Violet=new Color(0.74f,0.55f,1f);
        private static readonly Color Panel=new Color(0.025f,0.047f,0.077f,0.93f);
        private static readonly Color Line=new Color(0.18f,0.28f,0.35f,0.90f);
        private static readonly Color Error=new Color(1f,0.50f,0.43f);

        public void Attach(ManaWorldController owner) { _world=owner; }

        private void PrepareStyles()
        {
            if(_stylesReady)return;
            try { _font=Font.CreateDynamicFontFromOSFont(new[]{"Noto Sans CJK JP","Noto Sans JP","Yu Gothic","Meiryo","Hiragino Kaku Gothic ProN","Arial"},18); }
            catch(Exception) { _font=null; }
            if(_font==null)_font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _label=new GUIStyle(GUI.skin.label) {font=_font,fontSize=14,wordWrap=true,richText=false};
            _label.normal.textColor=Ink;
            _small=new GUIStyle(_label){fontSize=11};_small.normal.textColor=Muted;
            _title=new GUIStyle(_label){fontSize=28,fontStyle=FontStyle.Bold};
            _heading=new GUIStyle(_label){fontSize=12,fontStyle=FontStyle.Bold};
            _button=new GUIStyle(_label){alignment=TextAnchor.MiddleCenter,fontSize=12,wordWrap=false};
            _input=new GUIStyle(GUI.skin.textArea){font=_font,fontSize=17,wordWrap=true,padding=new RectOffset(12,12,10,10)};
            _input.normal.textColor=Ink;_input.focused.textColor=Ink;
            _stylesReady=true;
        }

        private void OnGUI()
        {
            if(_world==null||_world.World==null||_world.Terrain==null)return;
            PrepareStyles();
            if(_lastWorkbench!=_world.WorkbenchOpen)
            {
                GUIUtility.keyboardControl=0;
                if(!_world.WorkbenchOpen)Input.imeCompositionMode=IMECompositionMode.Auto;
                _lastWorkbench=_world.WorkbenchOpen;
            }
            float scale=Mathf.Clamp(Screen.width/1440f,0.5f,1.5f);
            Matrix4x4 previous=GUI.matrix;
            GUI.matrix=Matrix4x4.TRS(Vector3.zero,Quaternion.identity,new Vector3(scale,scale,1));
            float width=Screen.width/scale,height=Screen.height/scale;
            DrawHeader(width);
            DrawRightPanels(width,height);
            if(_world.WorkbenchOpen)DrawWorkbench(height);
            else
            {
                if(_world.HelpOpen||!_world.Surveyor.Captured)DrawGuide();
                DrawObjectives(_world.HelpOpen||!_world.Surveyor.Captured?364:104);
                DrawCrosshair(width,height);
            }
            DrawHotbar(width,height);
            DrawToast(width,height);
            GUI.matrix=previous;
        }

        private void DrawHeader(float width)
        {
            RectPanel(new Rect(24,22,398,67),Teal);
            Text(new Rect(40,30,260,35),"MANAWEAVE",_title,Ink);
            Text(new Rect(42,64,360,18),"WORLD SYSTEMS LABORATORY  /  ORIGINAL SANDBOX",_small,Muted);
            float x=width-612;
            if(Button(new Rect(x,28,98,30),_world.Paused?"RESUME":"PAUSE",_world.Paused,Teal))_world.Paused=!_world.Paused;
            if(Button(new Rect(x+105,28,54,30),_world.SimulationSpeed.ToString("0",CultureInfo.InvariantCulture)+"x",false,Teal))
                _world.SimulationSpeed=_world.SimulationSpeed==1?2:_world.SimulationSpeed==2?4:1;
            if(Button(new Rect(x+166,28,120,30),"WORKBENCH",_world.WorkbenchOpen,Violet))_world.SetWorkbench(!_world.WorkbenchOpen);
            Text(new Rect(x,64,300,19),"SEED "+_world.World.Seed+"    TICK "+_world.World.Tick+"    "+_world.SmoothedFps.ToString("0")+" FPS",_small,Muted);
            float mx=width-306;
            string[] modes={"WORLD","MANA","HEAT"};
            for(int i=0;i<3;i++)
                if(Button(new Rect(mx+i*91,28,85,30),modes[i],(int)_world.Terrain.Mode==i,Teal))_world.SetObservation((ObservationMode)i);
            string legend=_world.Terrain.Mode==ObservationMode.Material?"MATERIAL VIEW  /  F2 changes instrument":_world.Terrain.Mode==ObservationMode.Mana?"TEAL: AMBIENT     VIOLET: STORED":"BLUE: COLD      RED: HOT (1000 °C+)";
            Text(new Rect(mx,65,290,20),legend,_small,Muted);
        }

        private void DrawRightPanels(float width,float height)
        {
            float x=width-322;
            RectPanel(new Rect(x,104,298,156),Teal);
            var stats=_world.Simulator.Stats;
            Text(new Rect(x+16,116,270,19),"SESSION LEDGER  /  "+stats.ActivePhenomena+" EVENTS",_heading,Teal);
            KeyValue(x+16,140,265,"Ambient mana created",Units(stats.GeneratedMana,"MU"),Ink);
            KeyValue(x+16,162,265,"Mana manifested",Units(stats.ManifestedMana,"MU"),Ink);
            KeyValue(x+16,184,265,"Stabilization",Units(stats.StabilizedJoules,"J"),new Color(0.38f,0.76f,1f));
            KeyValue(x+16,206,265,"Resonance",Units(stats.AmplifiedJoules,"J"),new Color(1f,0.67f,0.40f));
            KeyValue(x+16,230,265,"Direct ambient consumed",Units(stats.DirectManifestedMana,"MU"),Teal);

            RectPanel(new Rect(x,272,298,172),Violet);
            Text(new Rect(x+16,284,260,19),_world.ProbePinned?"PINNED EXPERIMENT PROBE":"TARGET OBSERVATION  /  E TO LOG",_heading,Violet);
            if(_world.ObservationInWorld)
            {
                int index=_world.World.Index(_world.ObservationX,_world.ObservationY,_world.ObservationZ);
                Cell cell=_world.World.Cells[index];
                var material=MaterialCatalog.Get(cell.Material);
                Text(new Rect(x+16,310,258,24),material.Name.ToUpperInvariant(),_label,Ink);
                Text(new Rect(x+16,337,250,18),"CELL "+_world.ObservationX+" : "+_world.ObservationY+" : "+_world.ObservationZ+"     "+_world.World.Temperature(index).ToString("0.0",CultureInfo.InvariantCulture)+" °C",_small,Muted);
                KeyValue(x+16,362,265,"Ambient",cell.AmbientMana.ToString("0.00",CultureInfo.InvariantCulture)+" MU",Teal);
                float capacity=material.ManaCapacity*cell.Mass;
                KeyValue(x+16,385,265,"Stored",cell.StoredMana.ToString("0.00",CultureInfo.InvariantCulture)+" / "+capacity.ToString("0.0",CultureInfo.InvariantCulture),Violet);
                Bar(new Rect(x+16,414,266,5),cell.StoredMana/Mathf.Max(capacity,0.001f),Violet);
                if(_world.ProbePinned&&Button(new Rect(x+176,309,106,24),"FOLLOW AIM",false,Violet))_world.ReleaseProbe();
            }
            else Text(new Rect(x+16,315,260,80),"Aim inside the 48 × 24 × 48 research volume.\nA missed surface targets air 8 m ahead.",_label,Muted);

            if(height>748)
            {
                float mapY=460;
                RectPanel(new Rect(x,mapY,298,190),Teal);
                Rect map=new Rect(x+15,mapY+16,158,158);
                GUI.DrawTexture(map,_world.Terrain.Overview,ScaleMode.StretchToFill,false);
                Vector3 camera=_world.Surveyor.transform.position;
                float px=map.x+Mathf.Clamp01(camera.x/_world.World.Width)*map.width;
                float py=map.y+(1-Mathf.Clamp01(camera.z/_world.World.Depth))*map.height;
                Fill(new Rect(px-3,py-3,6,6),Color.white);
                Text(new Rect(x+186,mapY+15,100,20),"SURVEY MAP",_heading,Teal);
                Text(new Rect(x+186,mapY+47,96,97),"N ↑\n\nClick a point to move the survey camera.",_small,Muted);
                Text(new Rect(x+186,mapY+151,96,27),"F5 SAVE\nF9 LOAD",_small,Ink);
                if(!_world.Surveyor.Captured&&Event.current.type==EventType.MouseDown&&Event.current.button==0&&map.Contains(Event.current.mousePosition))
                {
                    Vector2 p=Event.current.mousePosition;
                    _world.SurveyMap((p.x-map.x)/map.width,1-(p.y-map.y)/map.height);
                    Event.current.Use();
                }
            }
        }

        private void DrawGuide()
        {
            RectPanel(new Rect(24,104,350,246),Teal);
            Text(new Rect(42,118,315,20),"SURVEYOR MODE",_heading,Teal);
            Text(new Rect(42,149,305,147),
                "W A S D       Fly / survey\nMouse           Look around\nSpace / Ctrl   Ascend / descend\nShift              Fly faster\n1 — 6             Equip a spell\nLeft click       Cast at the crosshair\nWheel            Change effect radius\nTab / right click   Spell workbench\nEsc                Release or capture cursor",new GUIStyle(_label){fontSize=12},Ink);
            string label=_world.Surveyor.Captured?"CLOSE GUIDE  [F1]":"ENTER WORLD";
            if(Button(new Rect(42,306,314,29),label,true,Teal))
            {
                if(_world.Surveyor.Captured)_world.HelpOpen=false;
                else _world.EnterWorld();
            }
        }

        private void DrawObjectives(float y)
        {
            RectPanel(new Rect(24,y,350,213),Violet);
            Text(new Rect(42,y+14,310,20),"FIELD RESEARCH",_heading,Violet);
            Check(42,y+46,"Observe a material [E]",_world.ObservedMaterial);
            Check(42,y+70,"Measure natural stabilization",_world.ObservedStabilization);
            Check(42,y+94,"Measure natural resonance",_world.ObservedResonance);
            Check(42,y+118,"Charge a crystal with a spell",_world.ChargedCrystal);
            Check(42,y+142,"Create an alloy through alchemy",_world.CreatedAlloy);
            Check(42,y+166,"Save the changed world [F5]",_world.WorldSaved);
            Text(new Rect(42,y+191,310,14),"FIXTURES: F6 weak · F7 large · F8 alloy · F10 air",_small,Muted);
        }

        private void DrawCrosshair(float width,float height)
        {
            float x=width/2,y=height/2;
            Color color=_world.TargetInWorld?new Color(0.85f,0.97f,1f,0.85f):new Color(0.7f,0.7f,0.7f,0.5f);
            Fill(new Rect(x-10,y,6,1),color);Fill(new Rect(x+4,y,6,1),color);
            Fill(new Rect(x,y-10,1,6),color);Fill(new Rect(x,y+4,1,6),color);
            if(_world.Surveyor.Captured&&_world.Equipped!=null)
            {
                var label=new GUIStyle(_small){alignment=TextAnchor.MiddleCenter};
                Text(new Rect(x-180,y+21,360,24),_world.Equipped.Name+"  ·  R"+_world.Equipped.Radius+"  ·  CLICK TO CAST",label,ManaWorldController.SpellColor(_world.Equipped.Operation));
            }
        }

        private void DrawHotbar(float width,float height)
        {
            float y=height-132;
            RectPanel(new Rect(24,y,width-48,104),Teal);
            Text(new Rect(42,y+12,190,19),"PERSONAL MANA",_heading,Violet);
            string mana=_world.World.CasterMana.ToString("0.0",CultureInfo.InvariantCulture)+" / "+_world.World.CasterManaMax.ToString("0",CultureInfo.InvariantCulture)+" MU";
            Text(new Rect(42,y+36,190,26),mana,_label,Ink);
            Bar(new Rect(42,y+73,170,6),_world.World.CasterMana/Mathf.Max(1,_world.World.CasterManaMax),Violet);
            float start=234;
            float cardWidth=Mathf.Min(120,(width-494)/6);
            for(int i=0;i<6;i++)
            {
                Rect rect=new Rect(start+i*(cardWidth+7),y+12,cardWidth,79);
                var preset=ManaWorldController.Presets[i];
                Color color=ManaWorldController.SpellColor(preset.Operation);
                bool selected=_world.SelectedSlot==i;
                Fill(rect,selected?new Color(color.r*0.19f,color.g*0.19f,color.b*0.19f,0.95f):new Color(0.06f,0.10f,0.15f,0.8f));
                Fill(new Rect(rect.x,rect.y,rect.width,2),selected?color:Line);
                Text(new Rect(rect.x+10,rect.y+8,rect.width-18,20),(i+1).ToString("00"),_heading,color);
                Text(new Rect(rect.x+10,rect.y+33,rect.width-18,20),preset.Name,_heading,selected?Ink:Muted);
                Text(new Rect(rect.x+10,rect.y+56,rect.width-18,19),preset.Description,_small,Muted);
                if(GUI.Button(rect,GUIContent.none,GUIStyle.none))_world.EquipPreset(i);
            }
            float rx=start+6*(cardWidth+7)+12;
            float rw=width-rx-43;
            if(_world.Equipped!=null)
            {
                Text(new Rect(rx,y+12,rw,19),_world.SelectedSlot<0?"PARSED SPELL":"EFFECT",_heading,_world.SelectedSlot<0?Violet:Teal);
                string effect="RADIUS "+_world.Equipped.Radius+" m";
                if(_world.Equipped.Operation==SpellOperation.Heat||_world.Equipped.Operation==SpellOperation.Cool)effect+="\n"+Units(_world.Equipped.Power,"J");
                else if(_world.Equipped.Operation==SpellOperation.Charge)effect+="\n"+Units(_world.Equipped.Power,"MU");
                else effect+="\n"+_world.Equipped.Operation;
                Text(new Rect(rx,y+37,rw,49),effect,_small,Ink);
            }
            Text(new Rect(26,height-20,width-52,17),"TAB WORKBENCH     F2 INSTRUMENT     P PAUSE     F5 SAVE     F9 LOAD     F1 GUIDE        •        All visible changes are resolved by the world law engine.",_small,Muted);
        }

        private void DrawToast(float width,float height)
        {
            if(string.IsNullOrEmpty(_world.LastMessage)||Time.unscaledTime-_world.LastMessageTime>12)return;
            float w=Mathf.Min(width-420,740);
            float x=(width-w)/2;
            RectPanel(new Rect(x,height-192,w,48),_world.LastMessageError?Error:Teal);
            Text(new Rect(x+14,height-183,w-28,33),_world.LastMessage,_small,_world.LastMessageError?Error:Ink);
        }

        private void DrawWorkbench(float height)
        {
            float panelHeight=height-318;
            panelHeight=Mathf.Max(330,panelHeight);
            RectPanel(new Rect(24,104,564,panelHeight),Violet);
            Text(new Rect(42,118,390,24),"SPELL WORKBENCH / 魔法解析",_heading,Violet);
            if(Button(new Rect(476,115,94,27),"CLOSE",false,Teal))_world.SetWorkbench(false);
            Rect view=new Rect(38,152,538,panelHeight-60);
            _workScroll=GUI.BeginScrollView(view,_workScroll,new Rect(0,0,516,1160));
            Text(new Rect(2,0,508,22),"解析 → 命令を確認 → 装備 → 狙って発動",_small,Muted);
            GUI.enabled=!_world.ParseBusy;
            GUI.enabled=GUI.enabled&&_world.DirectWeightsAvailable;
            if(Button(new Rect(2,30,248,30),"DIRECT WEIGHTS",_world.UseDirectWeights,Violet))_world.SetParserMode(true);
            GUI.enabled=!_world.ParseBusy;
            if(Button(new Rect(258,30,248,30),"TRAINED TRANSFORMER",!_world.UseDirectWeights,Teal))_world.SetParserMode(false);
            GUI.enabled=true;
            Text(new Rect(2,69,504,40),_world.ModelStatus,_small,Muted);
            GUI.SetNextControlName("mana-chant");
            _chant=GUI.TextArea(new Rect(2,115,504,66),_chant,240,_input);
            if(GUI.GetNameOfFocusedControl()=="mana-chant")
            {
                Input.imeCompositionMode=IMECompositionMode.On;
                Input.compositionCursorPos=new Vector2(60,240);
            }
            string[] samples={"目の前を小さく熱して","広範囲に冷やして","対象を掘削して","石で建てて","魔力を充填して","錬成して"};
            string[] labels={"加熱","冷却","掘削","建築","充填","錬成"};
            for(int i=0;i<6;i++)
                if(Button(new Rect(2+i*85,189,79,25),labels[i],false,Muted))_chant=samples[i];
            GUI.enabled=!_world.ParseBusy;
            if(Button(new Rect(2,224,180,34),_world.ParseBusy?"ANALYZING…":"ANALYZE",true,Violet))_world.Analyze(_chant);
            GUI.enabled=true;
            if(Button(new Rect(192,224,314,34),"INSERT EXPLICIT DSL",false,Teal))_chant="heat r=1 power=120000";
            if(Event.current.type==EventType.KeyDown&&Event.current.control&&Event.current.keyCode==KeyCode.Return&&!_world.ParseBusy)
            {
                _world.Analyze(_chant);Event.current.Use();
            }
            var parsed=_world.Parsed;
            Fill(new Rect(2,268,504,112),new Color(0.04f,0.075f,0.11f,1));
            if(parsed!=null)
            {
                Text(new Rect(14,278,478,18),(parsed.Source??"ANALYSIS")+"  /  "+_world.ParseMilliseconds.ToString("0",CultureInfo.InvariantCulture)+" ms",_small,parsed.Success?Teal:Error);
                Text(new Rect(14,304,478,64),parsed.Success?parsed.Canonical:parsed.Error,_label,parsed.Success?Ink:Error);
            }
            else Text(new Rect(14,282,478,82),"Analysis creates a proposed command. It does not cast a spell. Unsupported wording returns an error; no substitute spell is selected.",_small,Muted);
            GUI.enabled=parsed!=null&&parsed.Success;
            if(Button(new Rect(2,390,504,34),"EQUIP REVIEWED SPELL → RETURN TO WORLD",true,Teal))_world.EquipParsed();
            GUI.enabled=true;
            Section(440,"EQUIPPED SPELL PARAMETERS");
            if(_world.Equipped!=null)
            {
                Text(new Rect(2,468,290,20),_world.Equipped.Name+"  /  "+_world.Equipped.Source,_small,Muted);
                Text(new Rect(2,498,160,22),"Radius: "+_world.Equipped.Radius+" m",_label,Ink);
                _world.Equipped.Radius=Mathf.RoundToInt(GUI.HorizontalSlider(new Rect(180,504,320,18),_world.Equipped.Radius,1,5));
                if(_world.Equipped.Operation==SpellOperation.Heat||_world.Equipped.Operation==SpellOperation.Cool)
                {
                    Text(new Rect(2,530,170,22),Units(_world.Equipped.Power,"J"),_label,Ink);
                    _world.Equipped.Power=Mathf.Round(GUI.HorizontalSlider(new Rect(180,536,320,18),_world.Equipped.Power,10000,800000)/1000)*1000;
                }
                else if(_world.Equipped.Operation==SpellOperation.Charge)
                {
                    Text(new Rect(2,530,170,22),Units(_world.Equipped.Power,"MU"),_label,Ink);
                    _world.Equipped.Power=Mathf.Round(GUI.HorizontalSlider(new Rect(180,536,320,18),_world.Equipped.Power,1,200));
                }
                else if(_world.Equipped.Operation==SpellOperation.Build)
                {
                    MaterialId[] materials={MaterialId.Stone,MaterialId.Wood,MaterialId.Water,MaterialId.Iron,MaterialId.Crystal};
                    for(int i=0;i<materials.Length;i++)
                        if(Button(new Rect(2+i*102,530,96,27),materials[i].ToString(),_world.Equipped.Material==materials[i],Teal))_world.Equipped.Material=materials[i];
                }
                else Text(new Rect(2,530,504,33),_world.Equipped.Operation==SpellOperation.Transmute?"Iron requires ≥250 °C and ≥6 stored MU per cell. The alchemy fixture prepares it.":"Resource checks and material conditions are enforced at cast time.",_small,Muted);
            }
            Section(582,"REPRODUCIBLE LAB FIXTURES");
            if(Button(new Rect(2,612,248,33),"01  STABILIZATION",false,Teal))_world.RunExperiment(0);
            if(Button(new Rect(258,612,248,33),"02  RESONANCE",false,new Color(1f,0.65f,0.37f)))_world.RunExperiment(1);
            if(Button(new Rect(2,654,248,33),"03  ALCHEMY SETUP",false,Violet))_world.RunExperiment(2);
            if(Button(new Rect(258,654,248,33),"04  DIRECT AMBIENT",false,Teal))_world.RunExperiment(3);
            Text(new Rect(2,696,504,49),"Fixtures prepare known test conditions and pin a probe. Alchemy pauses the simulation: equip 6, cast on iron, then P to resume. Spells still consume personal mana.",_small,Muted);
            Section(758,"SNAPSHOTS & WORLD");
            if(Button(new Rect(2,788,160,31),"SAVE  [F5]",false,Teal))_world.SaveWorld();
            if(Button(new Rect(174,788,160,31),"LOAD  [F9]",false,Teal))_world.LoadWorld();
            if(Button(new Rect(346,788,160,31),"NEW ISLAND",false,Violet))_world.NewWorld();
            Text(new Rect(2,828,504,32),"New island replaces the active unsaved world. Load restores the last snapshot.",_small,Muted);
            Section(878,"RESEARCH JOURNAL");
            int shown=Math.Min(5,_world.Journal.Count);
            for(int i=0;i<shown;i++)
            {
                var entry=_world.Journal[i];
                Text(new Rect(2,910+i*45,504,40),"T"+entry.Tick+"  "+entry.Text,_small,i==0?Ink:Muted);
            }
            if(shown==0)Text(new Rect(2,910,504,42),"Observe a target with E, cast a spell, or run a fixture to record a result.",_small,Muted);
            GUI.EndScrollView();
        }

        private void Section(float y,string title)
        {
            Fill(new Rect(2,y,504,1),Line);
            Text(new Rect(2,y+10,504,22),title,_heading,Teal);
        }

        private void Check(float x,float y,string label,bool complete)
        {
            Fill(new Rect(x,y+3,12,12),complete?Teal:Line);
            if(complete)Text(new Rect(x+1,y-1,15,18),"✓",_small,Panel);
            Text(new Rect(x+24,y-2,285,21),label,_small,complete?Ink:Muted);
        }

        private void KeyValue(float x,float y,float width,string key,string value,Color color)
        {
            Text(new Rect(x,y,width*0.62f,21),key,_small,Muted);
            var right=new GUIStyle(_small){alignment=TextAnchor.UpperRight};
            Text(new Rect(x+width*0.51f,y,width*0.49f,21),value,right,color);
        }

        private static string Units(double number,string suffix)
        {
            double abs=Math.Abs(number);
            if(abs>=1e9)return(number/1e9).ToString("0.00",CultureInfo.InvariantCulture)+" G"+suffix;
            if(abs>=1e6)return(number/1e6).ToString("0.00",CultureInfo.InvariantCulture)+" M"+suffix;
            if(abs>=1e3)return(number/1e3).ToString("0.00",CultureInfo.InvariantCulture)+" k"+suffix;
            return number.ToString("0.0",CultureInfo.InvariantCulture)+" "+suffix;
        }

        private static void Fill(Rect rect,Color color)
        {
            Color old=GUI.color;GUI.color=color;GUI.DrawTexture(rect,Texture2D.whiteTexture);GUI.color=old;
        }

        private static void RectPanel(Rect rect,Color accent)
        {
            Fill(new Rect(rect.x+3,rect.y+4,rect.width,rect.height),new Color(0,0,0,0.16f));
            Fill(rect,Panel);
            Fill(new Rect(rect.x,rect.y,rect.width,1),Line);
            Fill(new Rect(rect.x,rect.y,3,rect.height),new Color(accent.r,accent.g,accent.b,0.9f));
        }

        private static void Bar(Rect rect,float value,Color color)
        {
            Fill(rect,Line);
            Fill(new Rect(rect.x,rect.y,rect.width*Mathf.Clamp01(value),rect.height),color);
        }

        private void Text(Rect rect,string text,GUIStyle style,Color color)
        {
            Color previous=style.normal.textColor;style.normal.textColor=color;
            GUI.Label(rect,text??string.Empty,style);style.normal.textColor=previous;
        }

        private bool Button(Rect rect,string text,bool active,Color color)
        {
            bool hover=rect.Contains(Event.current.mousePosition);
            Color bg=active?new Color(color.r*0.19f,color.g*0.19f,color.b*0.19f,1):new Color(0.065f,0.10f,0.15f,1);
            if(hover&&GUI.enabled)bg+=new Color(0.035f,0.045f,0.05f,0);
            Fill(rect,bg);
            Fill(new Rect(rect.x,rect.y+rect.height-1,rect.width,1),active?color:Line);
            Text(rect,text,_button,GUI.enabled?(active?color:Ink):Muted);
            return GUI.Button(rect,GUIContent.none,GUIStyle.none);
        }
    }
}
