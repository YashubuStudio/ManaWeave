using System;
using System.Globalization;
using ManaWeave.Core;
using ManaWeave.Magic;
using UnityEngine;

namespace ManaWeave.Runtime
{
    /// <summary>Research HUD with bundled fonts; all readouts come from the live law engine.</summary>
    public sealed class ManaHud : MonoBehaviour
    {
        private ManaWorldController _world;
        private Font _font,_boldFont;
        private GUIStyle _label,_small,_title,_heading,_button,_input,_guide,_centerSmall,_rightSmall;
        private GUIContent _measureContent;
        private Vector2 _workScroll,_chantScroll;
        private string _chant="紅蓮の炎よ、我が前に集いて、小さく対象を焼け。";
        private string _submittedChant;
        private int _chantCursor=-1;
        private SpellRecipe _powerSliderRecipe;
        private float _powerSliderMinimum,_powerSliderMaximum;
        private bool _submittedDirect,_showRawSpell;
        private bool _stylesReady,_fontsLoaded;
        private bool _lastWorkbench;
        private const string RegularFontResource="ManaWeave/Fonts/UIJapanese-Regular";
        private const string BoldFontResource="ManaWeave/Fonts/UIJapanese-Bold";
        private static readonly string[] ObservationLabels={"物質","魔力","温度"};
        private static readonly string[] ExampleLabels={"加熱の例","冷却の例","掘削の例","建築の例","充填の例","錬成の例"};
        private static readonly string[] ChantExamples={
            "紅蓮の炎よ、我が前に集いて、小さく対象を焼け。",
            "凍てつく霜よ、我が前に集いて、広範囲に対象を冷やせ。",
            "大地よ、我が意に応じて、対象を穿て。",
            "大地よ、我が意に応じて、石を創り出せ。",
            "巡る魔力よ、我が意に応じて、対象に満ちよ。",
            "錬金の理よ、我が意に応じて、対象を錬成せよ。"
        };
        private static readonly string[] ShortExamples={"目の前を小さく熱して","広範囲に冷やして","対象を掘削して","石で建てて","魔力を充填して","錬成して"};
        private static readonly MaterialId[] BuildMaterials={MaterialId.Stone,MaterialId.Wood,MaterialId.Water,MaterialId.Iron,MaterialId.Crystal};
        private const string FixtureGuide="命令のない魔力は、大きい熱変化にも小さい熱変化にも抵抗します。実験では観測点を固定します。錬成準備では時間が止まります。6番を装備して鉄に発動し、Pで時間を再開してください。魔法は自分の魔力を消費します。";
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
            if(!_fontsLoaded)
            {
                // Load source-font assets once. A non-null OS Font object does not
                // prove that its family exists; font-face failures can occur later
                // in GUI.Label on every IMGUI event. Do not probe OS font names.
                _fontsLoaded=true;
                _font=Resources.Load<Font>(RegularFontResource);
                _boldFont=Resources.Load<Font>(BoldFontResource);
                bool missingRegular=_font==null,missingBold=_boldFont==null;
                if(missingRegular)_font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if(_font==null)_font=GUI.skin.font;
                if(_boldFont==null)_boldFont=_font;
                if(missingRegular||missingBold)
                    Debug.LogWarning("ManaWeave：同梱の日本語フォントが見つかりません。"+
                        (missingRegular?"代替フォントを使います。日本語を表示できない場合があります。":"見出しにも通常のフォントを使います。")+
                        "Assets/Resources/ManaWeave/Fonts を復元し、Playをやり直してください。");
            }
            // If even the built-in fallback is absent, skip labels instead of
            // repeatedly attempting to resolve a missing font while repainting.
            if(_font==null)return;
            _label=new GUIStyle(GUI.skin.label) {font=_font,fontSize=14,fontStyle=FontStyle.Normal,wordWrap=true,richText=false};
            _label.normal.textColor=Ink;
            _small=new GUIStyle(_label){font=_font,fontSize=11,fontStyle=FontStyle.Normal};_small.normal.textColor=Muted;
            // Each weight is its own bundled asset. Normal prevents an OS family
            // or an unavailable Bold face being requested for the selected asset.
            _title=new GUIStyle(_label){font=_boldFont,fontSize=28,fontStyle=FontStyle.Normal};
            _heading=new GUIStyle(_label){font=_boldFont,fontSize=12,fontStyle=FontStyle.Normal};
            _button=new GUIStyle(_label){font=_font,fontStyle=FontStyle.Normal,alignment=TextAnchor.MiddleCenter,fontSize=12,wordWrap=false};
            _input=new GUIStyle(GUI.skin.textArea){font=_font,fontSize=17,fontStyle=FontStyle.Normal,wordWrap=true,richText=false,padding=new RectOffset(12,12,10,10)};
            _input.normal.textColor=Ink;_input.focused.textColor=Ink;
            _guide=new GUIStyle(_label){font=_font,fontSize=12,fontStyle=FontStyle.Normal};
            _centerSmall=new GUIStyle(_small){font=_font,fontStyle=FontStyle.Normal,alignment=TextAnchor.MiddleCenter};
            _rightSmall=new GUIStyle(_small){font=_font,fontStyle=FontStyle.Normal,alignment=TextAnchor.UpperRight};
            _measureContent=new GUIContent();
            _stylesReady=true;
        }

        private void OnGUI()
        {
            if(_world==null||_world.World==null||_world.Terrain==null)return;
            PrepareStyles();
            if(!_stylesReady)return;
            if(_lastWorkbench!=_world.WorkbenchOpen)
            {
                GUIUtility.keyboardControl=0;
                if(!_world.WorkbenchOpen)Input.imeCompositionMode=IMECompositionMode.Auto;
                _lastWorkbench=_world.WorkbenchOpen;
            }
            float scale=Mathf.Clamp(Screen.width/1440f,0.5f,1.5f);
            Matrix4x4 previousMatrix=GUI.matrix;
            Color previousColor=GUI.color;
            bool previousEnabled=GUI.enabled;
            try
            {
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
            }
            finally
            {
                // OnGUI shares this state with other IMGUI/Editor drawing. Do
                // not leave it modified if a control or a world action throws.
                GUI.matrix=previousMatrix;
                GUI.color=previousColor;
                GUI.enabled=previousEnabled;
            }
        }

        private void DrawHeader(float width)
        {
            RectPanel(new Rect(24,22,398,67),Teal);
            Text(new Rect(40,30,260,35),"MANAWEAVE",_title,Ink);
            Text(new Rect(42,64,360,18),"魔法・錬金術・科学の研究サンドボックス",_small,Muted);
            float x=width-612;
            if(Button(new Rect(x,28,98,30),_world.Paused?"再開":"一時停止",_world.Paused,Teal))_world.Paused=!_world.Paused;
            if(Button(new Rect(x+105,28,54,30),_world.SimulationSpeed.ToString("0",CultureInfo.InvariantCulture)+"倍",false,Teal))
                _world.SimulationSpeed=_world.SimulationSpeed==1?2:_world.SimulationSpeed==2?4:1;
            if(Button(new Rect(x+166,28,120,30),"魔法工房",_world.WorkbenchOpen,Violet))_world.SetWorkbench(!_world.WorkbenchOpen);
            Text(new Rect(x,64,300,19),"生成番号 "+_world.World.Seed+"  計算回数 "+_world.World.Tick+"  "+_world.SmoothedFps.ToString("0")+" FPS",_small,Muted);
            float mx=width-306;
            string[] modes=ObservationLabels;
            for(int i=0;i<3;i++)
                if(Button(new Rect(mx+i*91,28,85,30),modes[i],(int)_world.Terrain.Mode==i,Teal))_world.SetObservation((ObservationMode)i);
            string legend=_world.Terrain.Mode==ObservationMode.Material?"物質の表示 ／ F2で切替":_world.Terrain.Mode==ObservationMode.Mana?"青緑：空間魔力　紫：保有魔力":"青：低温　赤：高温（1000 °C以上）";
            Text(new Rect(mx,65,290,20),legend,_small,Muted);
        }

        private void DrawRightPanels(float width,float height)
        {
            float x=width-322;
            RectPanel(new Rect(x,104,298,156),Teal);
            var stats=_world.Simulator.Stats;
            Text(new Rect(x+16,116,270,19),"抵抗対象セル："+stats.ActivePhenomena,_heading,Teal);
            KeyValue(x+16,140,265,"自然発生した魔力",Units(stats.GeneratedMana,"MU"),Ink);
            KeyValue(x+16,162,265,"事象化した魔力",Units(stats.ManifestedMana,"MU"),Ink);
            KeyValue(x+16,184,265,"熱変化への抵抗量",Units(stats.StabilizedJoules,"J"),new Color(0.38f,0.76f,1f));
            KeyValue(x+16,206,265,"熱伝播の抑制量",Units(stats.SuppressedTransportJoules,"J"),new Color(1f,0.67f,0.40f));
            KeyValue(x+16,230,265,"空間魔力の直接消費",Units(stats.DirectManifestedMana,"MU"),Teal);

            RectPanel(new Rect(x,272,298,172),Violet);
            Text(new Rect(x+16,284,260,19),_world.ProbePinned?"観測点を固定中":"照準先の観測 ／ Eで記録",_heading,Violet);
            if(_world.ObservationInWorld)
            {
                int index=_world.World.Index(_world.ObservationX,_world.ObservationY,_world.ObservationZ);
                Cell cell=_world.World.Cells[index];
                var material=MaterialCatalog.Get(cell.Material);
                Text(new Rect(x+16,310,258,24),JapaneseNames.Material(cell.Material),_label,Ink);
                Text(new Rect(x+16,337,250,18),"位置 "+_world.ObservationX+" : "+_world.ObservationY+" : "+_world.ObservationZ+"     "+_world.World.Temperature(index).ToString("0.0",CultureInfo.InvariantCulture)+" °C",_small,Muted);
                KeyValue(x+16,362,265,"空間魔力",cell.AmbientMana.ToString("0.00",CultureInfo.InvariantCulture)+" MU",Teal);
                float capacity=material.ManaCapacity*cell.Mass;
                KeyValue(x+16,385,265,"保有魔力",cell.StoredMana.ToString("0.00",CultureInfo.InvariantCulture)+" / "+capacity.ToString("0.0",CultureInfo.InvariantCulture)+" MU",Violet);
                Bar(new Rect(x+16,414,266,5),cell.StoredMana/Mathf.Max(capacity,0.001f),Violet);
                float directed=_world.Simulator.DirectedThermalRemaining(index);
                Text(new Rect(x+16,424,266,16),directed>0?
                    "熱の指示が有効：残り "+directed.ToString("0.0",CultureInfo.InvariantCulture)+" 秒":
                    "熱の指示なし：通常の抵抗",_small,directed>0?Teal:Muted);
                if(_world.ProbePinned&&Button(new Rect(x+176,309,106,24),"照準に戻す",false,Violet))_world.ReleaseProbe();
            }
            else Text(new Rect(x+16,315,260,80),"48 × 24 × 48 mの研究領域内を狙ってください。\n地形に当たらない照準は、8 m先の空間を指します。",_label,Muted);

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
                Text(new Rect(x+186,mapY+15,100,20),"調査地図",_heading,Teal);
                Text(new Rect(x+186,mapY+47,96,97),"北 ↑\n\n地点をクリックして\nカメラを移動。",_small,Muted);
                Text(new Rect(x+186,mapY+143,96,34),"F5 保存\nF9 読込",_small,Ink);
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
            Text(new Rect(42,118,315,20),"操作ガイド",_heading,Teal);
            Text(new Rect(42,149,305,147),
                "WASD：移動　マウス：視点\nSpace：上昇　Ctrl：下降\nShift：高速移動\n1～6：魔法を選択\n左クリック：照準に魔法を発動\nホイール：効果半径を変更\nTab / 右クリック：魔法工房\nEsc：マウスの解放 / 再捕捉",_guide,Ink);
            string label=_world.Surveyor.Captured?"操作ガイドを閉じる [F1]":"世界へ入る";
            if(Button(new Rect(42,306,314,29),label,true,Teal))
            {
                if(_world.Surveyor.Captured)_world.HelpOpen=false;
                else _world.EnterWorld();
            }
        }

        private void DrawObjectives(float y)
        {
            RectPanel(new Rect(24,y,350,218),Violet);
            Text(new Rect(42,y+14,310,20),"野外研究の目標",_heading,Violet);
            Check(42,y+46,"物質を観測する [E]",_world.ObservedMaterial);
            Check(42,y+68,"熱変化への抵抗を観測する",_world.ObservedStabilization);
            Check(42,y+90,"熱の伝播抵抗を観測する",_world.ObservedPropagationResistance);
            Check(42,y+112,"魔法で結晶に魔力を充填する",_world.ChargedCrystal);
            Check(42,y+134,"錬金術で魔導合金を作る",_world.CreatedAlloy);
            Check(42,y+156,"変化した世界を保存する [F5]",_world.WorldSaved);
            Text(new Rect(42,y+178,310,34),"抵抗実験：F6 小規模 ／ F7 大規模\n　　　　　F8 錬成準備 ／ F10 空間魔力",_small,Muted);
        }

        private void DrawCrosshair(float width,float height)
        {
            float x=width/2,y=height/2;
            Color color=_world.TargetInWorld?new Color(0.85f,0.97f,1f,0.85f):new Color(0.7f,0.7f,0.7f,0.5f);
            Fill(new Rect(x-10,y,6,1),color);Fill(new Rect(x+4,y,6,1),color);
            Fill(new Rect(x,y-10,1,6),color);Fill(new Rect(x,y+4,1,6),color);
            if(_world.Surveyor.Captured&&_world.Equipped!=null)
            {
                Text(new Rect(x-180,y+21,360,24),_world.Equipped.Name+" ／ 半径 "+_world.Equipped.Radius+" m ／ 左クリックで発動",_centerSmall,ManaWorldController.SpellColor(_world.Equipped.Operation));
            }
        }

        private void DrawHotbar(float width,float height)
        {
            float y=height-132;
            RectPanel(new Rect(24,y,width-48,104),Teal);
            Text(new Rect(42,y+12,190,19),"自分の魔力",_heading,Violet);
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
                Text(new Rect(rx,y+12,rw,19),_world.SelectedSlot<0?"解析した術式":"効果",_heading,_world.SelectedSlot<0?Violet:Teal);
                string effect="半径 "+_world.Equipped.Radius+" m";
                if(_world.Equipped.Operation==SpellOperation.Heat||_world.Equipped.Operation==SpellOperation.Cool)effect+="\n"+Units(_world.Equipped.Power,"J");
                else if(_world.Equipped.Operation==SpellOperation.Charge)effect+="\n"+Units(_world.Equipped.Power,"MU");
                else effect+="\n"+JapaneseNames.Operation(_world.Equipped.Operation);
                Text(new Rect(rx,y+37,rw,49),effect,_small,Ink);
            }
            Text(new Rect(26,height-20,width-52,17),"Tab 魔法工房　 F2 表示切替　 P 一時停止　 F5 保存　 F9 読込　 F1 ガイド　 E 観測",_small,Muted);
        }

        private void DrawToast(float width,float height)
        {
            if(string.IsNullOrEmpty(_world.LastMessage)||Time.unscaledTime-_world.LastMessageTime>12)return;
            float w=Mathf.Min(width-420,740);
            float x=(width-w)/2;
            float textHeight=ParagraphHeight(_world.LastMessage,_small,w-28,30);
            float top=height-144-textHeight-18;
            RectPanel(new Rect(x,top,w,textHeight+18),_world.LastMessageError?Error:Teal);
            Text(new Rect(x+14,top+9,w-28,textHeight),_world.LastMessage,_small,_world.LastMessageError?Error:Ink);
        }

        private void DrawWorkbench(float height)
        {
            float panelHeight=Mathf.Max(190,height-302);
            RectPanel(new Rect(24,104,564,panelHeight),Violet);
            Text(new Rect(42,118,390,24),"魔法工房",_heading,Violet);
            if(Button(new Rect(476,115,94,27),"閉じる [Tab]",false,Teal))_world.SetWorkbench(false);
            float resultHeight=ParsedCardHeight();
            float fixtureHeight=ParagraphHeight(FixtureGuide,_small,504,50);
            string adjustmentGuide=AdjustmentGuide();
            float adjustmentHeight=ParagraphHeight(adjustmentGuide,_small,504,32);
            Rect view=new Rect(38,152,538,panelHeight-60);
            _workScroll=GUI.BeginScrollView(view,_workScroll,new Rect(0,0,516,WorkbenchContentHeight(resultHeight,fixtureHeight,adjustmentHeight)));
            bool previousEnabled=GUI.enabled;
            try { DrawWorkbenchContents(resultHeight,fixtureHeight,adjustmentGuide,adjustmentHeight); }
            finally
            {
                GUI.enabled=previousEnabled;
                GUI.EndScrollView();
            }
        }

        private void DrawWorkbenchContents(float resultHeight,float fixtureHeight,string adjustmentGuide,float adjustmentHeight)
        {
            bool controlsEnabled=GUI.enabled;
            bool composing=!string.IsNullOrEmpty(Input.compositionString);
            // Geometry was measured before this GUI event. Apply a details-toggle
            // change on the next event so the current rectangles keep valid sizes.
            bool showRawDetails=_showRawSpell;
            Text(new Rect(2,0,508,20),"1. 入力 → 2. 解析 → 3. 装備して戻る → 左クリックで発動",_small,Muted);
            GUI.enabled=controlsEnabled&&!_world.ParseBusy&&_world.DirectWeightsAvailable;
            if(Button(new Rect(2,26,248,28),"詠唱解析",_world.UseDirectWeights,Violet))_world.SetParserMode(true);
            GUI.enabled=controlsEnabled&&!_world.ParseBusy;
            if(Button(new Rect(258,26,248,28),"短文解析",!_world.UseDirectWeights,Teal))_world.SetParserMode(false);
            GUI.enabled=controlsEnabled;
            Text(new Rect(2,60,504,34),_world.ModelStatus,_small,Muted);
            Text(new Rect(2,96,270,20),"1. 詠唱を入力",_heading,Violet);
            int inputLimit=_world.UseDirectWeights?MagicParser.MaxInputLength:MagicParser.MaxLearnedInputLength;
            Text(new Rect(286,97,220,19),composing?"Enterで変換を確定":_chant.Length+" / "+inputLimit+"文字",_rightSmall,_chant.Length>inputLimit?Error:Muted);

            // Give long, multiline chants their own scrollable editing area.
            // Keep more than the parser limit: truncating exactly at 512 could
            // drop a pasted condition while leaving a valid, unintended spell.
            float inputHeight=ParagraphHeight(_chant+"\n",_input,482,80);
            _chantScroll=GUI.BeginScrollView(new Rect(2,120,504,84),_chantScroll,new Rect(0,0,482,inputHeight));
            try
            {
                GUI.SetNextControlName("mana-chant");
                string previousInput=_chant;
                _chant=GUI.TextArea(new Rect(0,0,482,inputHeight),_chant,MagicParser.MaxInputLength*4,_input);
                if(GUI.GetNameOfFocusedControl()=="mana-chant")
                {
                    Input.imeCompositionMode=IMECompositionMode.On;
                    // Unity's TextEditor positions the IME candidates using the
                    // actual caret and GUI clip transforms; do not override it.
                    FollowChantCursor(previousInput,inputHeight);
                }
                else _chantCursor=-1;
            }
            finally { GUI.EndScrollView(); }
            string[] samples=_world.UseDirectWeights?ChantExamples:ShortExamples;
            for(int i=0;i<6;i++)
                if(Button(new Rect(2+i*85,212,79,25),ExampleLabels[i],false,Muted))SetChant(samples[i]);
            GUI.enabled=controlsEnabled&&!_world.ParseBusy&&!composing&&string.IsNullOrEmpty(Input.compositionString);
            if(Button(new Rect(2,247,180,32),_world.ParseBusy?"解析中…":"詠唱を解析",true,Violet))AnalyzeChant();
            GUI.enabled=controlsEnabled;
            if(Button(new Rect(192,247,314,32),"数値指定の例を入力",false,Teal))SetChant("heat r=1 power=120000");
            if(Event.current.type==EventType.KeyDown&&Event.current.control&&Event.current.keyCode==KeyCode.Return&&
               !_world.ParseBusy&&controlsEnabled&&!composing&&string.IsNullOrEmpty(Input.compositionString))
            {
                AnalyzeChant();Event.current.Use();
            }

            var parsed=_world.Parsed;
            bool current=IsCurrentResult();
            bool ready=current&&parsed!=null&&parsed.Success&&!composing;
            Color resultColor=ready?Teal:(current&&parsed!=null&&!parsed.Success?Error:Muted);
            Fill(new Rect(2,289,504,resultHeight),new Color(0.04f,0.075f,0.11f,1));
            string heading=_world.ParseBusy?"2. 解析しています…":
                parsed==null?"2. 解析結果を確認":!current?"2. 入力が変更されています":
                parsed.Success?"2. 解析結果：発動内容を確認":"2. この詠唱は使えません";
            Text(new Rect(14,297,478,20),heading,_heading,resultColor);
            if(ready)
            {
                Text(new Rect(14,320,478,46),SpellSummary(parsed),_label,Ink);
                if(Button(new Rect(14,372,168,20),showRawDetails?"術式の詳細を閉じる":"術式の詳細を表示",false,Muted))_showRawSpell=!showRawDetails;
                Text(new Rect(190,373,302,18),"対象は発動時の照準です",_small,Muted);
                if(showRawDetails&&resultHeight>120)
                    Text(new Rect(14,400,478,resultHeight-120),parsed.Canonical,_small,Muted);
            }
            else Text(new Rect(14,320,478,resultHeight-39),ParseMessage(),_label,current&&parsed!=null?Error:Muted);
            GUI.enabled=controlsEnabled&&ready&&!_world.ParseBusy;
            if(Button(new Rect(2,299+resultHeight,504,34),"この術式を装備して戻る",true,Teal))_world.EquipParsed();
            GUI.enabled=controlsEnabled;

            float y=349+resultHeight;
            Section(y,"装備中の術式を調整");
            if(_world.Equipped!=null)
            {
                Text(new Rect(2,y+40,504,22),_world.Equipped.Name+" ／ "+_world.Equipped.Source,_small,Muted);
                Text(new Rect(2,y+70,160,24),"半径："+_world.Equipped.Radius+" m",_label,Ink);
                _world.Equipped.Radius=Mathf.RoundToInt(GUI.HorizontalSlider(new Rect(180,y+76,320,18),_world.Equipped.Radius,1,5));
                if(_world.Equipped.Operation==SpellOperation.Heat||_world.Equipped.Operation==SpellOperation.Cool)
                {
                    Text(new Rect(2,y+104,170,24),"指定熱量："+Units(_world.Equipped.Power,"J"),_small,Ink);
                    DrawPowerSlider(new Rect(180,y+110,320,18),10000,800000,1000);
                }
                else if(_world.Equipped.Operation==SpellOperation.Charge)
                {
                    Text(new Rect(2,y+104,170,24),"指定魔力："+Units(_world.Equipped.Power,"MU"),_small,Ink);
                    DrawPowerSlider(new Rect(180,y+110,320,18),1,200,1);
                }
                else if(_world.Equipped.Operation==SpellOperation.Build)
                {
                    for(int i=0;i<BuildMaterials.Length;i++)
                        if(Button(new Rect(2+i*102,y+104,96,27),JapaneseNames.Material(BuildMaterials[i]),_world.Equipped.Material==BuildMaterials[i],Teal))_world.Equipped.Material=BuildMaterials[i];
                }
                else Text(new Rect(2,y+103,504,38),_world.Equipped.Operation==SpellOperation.Transmute?
                    "鉄の温度250 °C以上・保有魔力6 MU以上が必要です（1ブロックあたり）。下の錬成準備で条件を整えられます。":
                    "発動時に魔力と対象の条件を確認します。",_small,Muted);
                Text(new Rect(2,y+147,504,adjustmentHeight),adjustmentGuide,_small,Muted);
            }
            else Text(new Rect(2,y+42,504,70),"まだ術式を装備していません。1～6で選ぶか、詠唱を解析して装備してください。",_label,Muted);

            y+=158+adjustmentHeight;
            Section(y,"世界法則の実験");
            if(Button(new Rect(2,y+34,248,33),"01 小規模な熱変化への抵抗",false,Teal))_world.RunExperiment(0);
            if(Button(new Rect(258,y+34,248,33),"02 大規模な熱変化への抵抗",false,new Color(1f,0.65f,0.37f)))_world.RunExperiment(1);
            if(Button(new Rect(2,y+75,248,33),"03 錬成の条件を準備",false,Violet))_world.RunExperiment(2);
            if(Button(new Rect(258,y+75,248,33),"04 空間魔力の直接抵抗",false,Teal))_world.RunExperiment(3);
            Text(new Rect(2,y+116,504,fixtureHeight),FixtureGuide,_small,Muted);
            y+=116+fixtureHeight+18;
            Section(y,"保存とワールド");
            if(Button(new Rect(2,y+34,160,31),"保存 [F5]",false,Teal))_world.SaveWorld();
            if(Button(new Rect(174,y+34,160,31),"読込 [F9]",false,Teal))_world.LoadWorld();
            if(Button(new Rect(346,y+34,160,31),"新しい島を生成",false,Violet))_world.NewWorld();
            Text(new Rect(2,y+77,504,36),"新しい島を生成すると、現在の未保存の変更を失います。読込は最後に保存した状態へ戻ります。",_small,Muted);
            y+=130;
            Section(y,"研究日誌");
            y+=38;
            int shown=Math.Min(5,_world.Journal.Count);
            for(int i=0;i<shown;i++)
            {
                string entry=JournalText(_world.Journal[i]);
                float rowHeight=ParagraphHeight(entry,_small,504,40);
                Text(new Rect(2,y,504,rowHeight),entry,_small,i==0?Ink:Muted);
                y+=rowHeight+8;
            }
            if(shown==0)Text(new Rect(2,y,504,48),"Eで対象を観測するか、魔法や実験を使うと、ここへ結果が記録されます。",_small,Muted);
        }

        private void SetChant(string value)
        {
            _chant=value;
            _chantScroll=Vector2.zero;
            _chantCursor=-1;
            _showRawSpell=false;
        }

        private void AnalyzeChant()
        {
            if(_world.ParseBusy||!string.IsNullOrEmpty(Input.compositionString))return;
            _submittedChant=_chant;
            _submittedDirect=_world.UseDirectWeights;
            _showRawSpell=false;
            _world.Analyze(_chant);
        }

        private void FollowChantCursor(string previousInput,float inputHeight)
        {
            var editor=GUIUtility.GetStateObject(typeof(TextEditor),GUIUtility.keyboardControl) as TextEditor;
            if(editor==null)return;
            int cursor=Mathf.Clamp(editor.cursorIndex,0,_chant.Length);
            bool moved=cursor!=_chantCursor||!string.Equals(previousInput,_chant,StringComparison.Ordinal);
            _chantCursor=cursor;
            // Mouse-wheel and scrollbar reading must not be pulled back to an
            // unchanged caret. Follow only focus, edit, or caret changes.
            if(!moved)return;
            _measureContent.text=_chant;
            Vector2 caret=_input.GetCursorPixelPosition(new Rect(0,0,482,inputHeight),_measureContent,cursor);
            float lineHeight=Mathf.Max(_input.lineHeight,_input.fontSize+2);
            if(caret.y<_chantScroll.y+4)_chantScroll.y=caret.y-4;
            else if(caret.y+lineHeight>_chantScroll.y+80)_chantScroll.y=caret.y+lineHeight-80;
            float contentHeight=ParagraphHeight(_chant+"\n",_input,482,80);
            _chantScroll.y=Mathf.Clamp(_chantScroll.y,0,Mathf.Max(0,contentHeight-84));
        }

        private bool IsCurrentResult()
        {
            return _world.Parsed!=null&&string.IsNullOrEmpty(Input.compositionString)&&
                   string.Equals(_submittedChant,_chant,StringComparison.Ordinal)&&
                   _submittedDirect==_world.UseDirectWeights;
        }

        private string ParseMessage()
        {
            if(_world.ParseBusy)return "詠唱を読み解いています。解析が終わるまでお待ちください。";
            if(!string.IsNullOrEmpty(Input.compositionString))return "変換中の文字があります。Enterで確定し、『詠唱を解析』を押してください。";
            if(_world.Parsed==null)
                return "上の欄に入力し、『詠唱を解析』を押してください。対応する言葉から1種類の効果を読み取ります。まず例文を試してください。";
            if(!IsCurrentResult())return "詠唱または解析方法を変更しました。『詠唱を解析』を押して、新しい結果を確認してください。";
            return _world.Parsed.Error??"解析できませんでした。例文の言葉を使い、1種類の効果を指定してください。";
        }

        private float ParsedCardHeight()
        {
            if(IsCurrentResult()&&_world.Parsed.Success)
                return _showRawSpell?120+ParagraphHeight(_world.Parsed.Canonical,_small,478,32):110;
            return Mathf.Max(110,ParagraphHeight(ParseMessage(),_label,478,60)+42);
        }

        private float WorkbenchContentHeight(float resultHeight,float fixtureHeight,float adjustmentHeight)
        {
            float y=349+resultHeight+158+adjustmentHeight+116+fixtureHeight+18+130+38;
            int shown=Math.Min(5,_world.Journal.Count);
            if(shown==0)y+=48;
            for(int i=0;i<shown;i++)y+=ParagraphHeight(JournalText(_world.Journal[i]),_small,504,40)+8;
            return y+18;
        }

        private string AdjustmentGuide()
        {
            if(_world.Equipped!=null&&(_world.Equipped.Operation==SpellOperation.Heat||_world.Equipped.Operation==SpellOperation.Cool))
                return "熱量は指定範囲全体の合計です。範囲外への伝播は空間魔力が抑えます。発動後"+
                    _world.Simulator.Config.DirectedThermalSeconds.ToString("0.##",CultureInfo.InvariantCulture)+
                    "秒は範囲内の抵抗が弱まり、その後は通常の抵抗へ戻ります。";
            return "解析した術式は、装備ボタンを押すとここへ反映されます。";
        }

        private void DrawPowerSlider(Rect rect,float minimum,float maximum,float increment)
        {
            SpellRecipe recipe=_world.Equipped;
            if(_powerSliderRecipe!=recipe)
            {
                _powerSliderRecipe=recipe;
                _powerSliderMinimum=minimum;
                _powerSliderMaximum=maximum;
            }
            float original=recipe.Power;
            // Include analyzed/saved values and keep the range stable while dragging.
            // Merely drawing the control must not clamp or round a spell's power.
            _powerSliderMinimum=Mathf.Min(_powerSliderMinimum,original);
            _powerSliderMaximum=Mathf.Max(_powerSliderMaximum,original);
            float value=GUI.HorizontalSlider(rect,original,_powerSliderMinimum,_powerSliderMaximum);
            if(value!=original)recipe.Power=Mathf.Max(1,Mathf.Round(value/increment)*increment);
        }

        private static string JournalText(JournalLine entry)
        {
            return "経過 "+entry.Tick+" ／ "+entry.Text;
        }

        private static string SpellSummary(MagicParseResult parsed)
        {
            string summary="効果："+JapaneseNames.Operation(parsed.Operation)+"　半径："+parsed.Radius+" m\n";
            switch(parsed.Operation)
            {
                case SpellOperation.Heat:return summary+"指定した総熱量："+Units(parsed.Power,"J");
                case SpellOperation.Cool:return summary+"最大除去熱量："+Units(parsed.Power,"J");
                case SpellOperation.Charge:return summary+"最大充填量："+Units(parsed.Power,"MU");
                case SpellOperation.Build:return summary+"生成する素材："+JapaneseNames.Material(parsed.Material);
                case SpellOperation.Excavate:return summary+"範囲内の物質を取り除きます。";
                case SpellOperation.Transmute:return summary+"条件を満たす鉄を魔導合金へ変えます。";
                default:return summary;
            }
        }

        private float ParagraphHeight(string value,GUIStyle style,float width,float minimum)
        {
            _measureContent.text=value??string.Empty;
            return Mathf.Max(minimum,style.CalcHeight(_measureContent,width));
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
            Text(new Rect(x+width*0.51f,y,width*0.49f,21),value,_rightSmall,color);
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
            Color old=GUI.color;
            try { GUI.color=color;GUI.DrawTexture(rect,Texture2D.whiteTexture); }
            finally { GUI.color=old; }
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
            Color previous=style.normal.textColor;
            try { style.normal.textColor=color;GUI.Label(rect,text??string.Empty,style); }
            finally { style.normal.textColor=previous; }
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
