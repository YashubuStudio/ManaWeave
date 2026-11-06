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
        private GUIStyle _label,_small,_title,_heading,_button,_input,_numericInput,_guide,_centerSmall,_rightSmall;
        private GUIContent _measureContent;
        private Vector2 _workScroll,_chantScroll;
        private string _chant="紅蓮を統べる精霊よ、我が呼び声に応えよ。照準先の鉄を三百五十度まで熱し、十秒間その熱を守れ。";
        private string _submittedChant;
        private int _chantCursor=-1;
        private SpellRecipe _numberRecipe;
        private float _lastPower,_lastTemperature,_lastDuration;
        private int _lastDistance;
        private string _powerDraft,_temperatureDraft,_durationDraft,_distanceDraft,_adjustmentError;
        private bool _submittedDirect,_showRawSpell;
        private bool _stylesReady,_fontsLoaded;
        private bool _lastWorkbench;
        private const string RegularFontResource="ManaWeave/Fonts/UIJapanese-Regular";
        private const string BoldFontResource="ManaWeave/Fonts/UIJapanese-Bold";
        private static readonly string[] ObservationLabels={"物質","魔力","温度"};
        private static readonly string[] ExampleLabels={"錬金用の加熱","熱量を指定","冷却の例","移動の例","掘削の例","建築の例","充填の例","錬成の例"};
        private static readonly string[] ChantExamples={
            "紅蓮を統べる精霊よ、我が呼び声に応えよ。照準先の鉄を三百五十度まで熱し、十秒間その熱を守れ。",
            "対象一点に200キロジュールの熱を与えよ。",
            "凍てつく霜よ、我が前に集いて、広範囲に対象を冷やせ。",
            "対象を上へ3メートル移動させよ。",
            "大地よ、我が意に応じて、対象を穿て。",
            "大地よ、我が意に応じて、石を創り出せ。",
            "巡る魔力よ、照準先の鉄へ10MUを充填せよ。",
            "錬金の理よ、照準先の鉄を錬成せよ。"
        };
        private static readonly string[] ShortExamples={"heat r=0 temperature=350 duration=10 target=iron","heat r=0 power=200000","広範囲に冷やして","move r=0 direction=up distance=3","対象を掘削して","石で建てて","charge r=0 power=10 target=iron","transmute r=0"};
        private static readonly MaterialId[] BuildMaterials={MaterialId.Stone,MaterialId.Wood,MaterialId.Water,MaterialId.Iron,MaterialId.Crystal};
        private static readonly SpellDirection[] MoveDirections={SpellDirection.Up,SpellDirection.Down,SpellDirection.Forward,SpellDirection.Backward,SpellDirection.Left,SpellDirection.Right,SpellDirection.North,SpellDirection.South,SpellDirection.East,SpellDirection.West};
        private static readonly string[] MoveDirectionLabels={"上","下","前","後ろ","左","右","北","南","東","西"};
        private const float ResultTop=330;
        private const string FixtureGuide="通常の錬金は鉄を狙い、1番で350℃へ加熱 → 5番で充填 → 6番で錬成できます。各段階で左クリックしてください。下の実験は別途、素材・熱・魔力を供給する検査用です。錬成準備だけは時間が止まり、Pで再開します。";
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
            _numericInput=new GUIStyle(_input){fontSize=14,wordWrap=false,padding=new RectOffset(8,8,4,4)};
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
            KeyValue(x+16,206,265,"熱伝播の抑制量",Units(stats.SuppressedTransportJoules+stats.DirectedSuppressedTransportJoules,"J"),new Color(1f,0.67f,0.40f));
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
                "WASD：移動　マウス：視点\nSpace：上昇　Ctrl：下降\nShift：高速移動\n1～7：魔法を選択（7は物質移動）\n左クリック：照準に魔法を発動\nホイール：効果半径を変更（0は1セル）\nTab / 右クリック：魔法工房\nEsc：マウスの解放 / 再捕捉",_guide,Ink);
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
                Text(new Rect(x-220,y+21,440,24),_world.Equipped.Name+" ／ "+SpellRecipe.RangeLabel(_world.Equipped.Radius)+" ／ 左クリックで発動",_centerSmall,ManaWorldController.SpellColor(_world.Equipped.Operation));
                SpellQuote quote=_world.QuoteEquipped();
                string cost=quote.Success?"必要魔力 "+Units(quote.ManaCost,"MU"):
                    (quote.ManaCost>0?"必要魔力 "+Units(quote.ManaCost,"MU")+" ／ "+quote.Message:quote.Message);
                Text(new Rect(x-260,y+47,520,48),cost,_centerSmall,quote.Success?Muted:Error);
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
            int slots=ManaWorldController.Presets.Length;
            float cardWidth=Mathf.Min(114,(width-550)/slots);
            for(int i=0;i<slots;i++)
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
            float rx=start+slots*(cardWidth+7)+12;
            float rw=width-rx-43;
            if(_world.Equipped!=null)
            {
                Text(new Rect(rx,y+12,rw,19),_world.SelectedSlot<0?"装備中の術式":"効果",_heading,_world.SelectedSlot<0?Violet:Teal);
                string effect=SpellRecipe.RangeLabel(_world.Equipped.Radius);
                if(_world.Equipped.IsThermal)effect+="\n"+(_world.Equipped.ThermalMode==ThermalMode.Temperature?
                    "目標 "+_world.Equipped.TargetTemperature.ToString("0.##",CultureInfo.InvariantCulture)+" °C":Units(_world.Equipped.Power,"J"));
                else if(_world.Equipped.Operation==SpellOperation.Charge)effect+="\n"+Units(_world.Equipped.Power,"MU");
                else if(_world.Equipped.Operation==SpellOperation.Move)effect+="\n"+ShortDirection(_world.Equipped.Direction)+"へ "+_world.Equipped.Distance+" m";
                else effect+="\n"+JapaneseNames.Operation(_world.Equipped.Operation);
                SpellQuote quote=_world.QuoteEquipped();
                if(quote.ManaCost>0)effect+="\n必要 "+Units(quote.ManaCost,"MU");
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
            EnsureNumberDrafts();
            // Freeze displayed data for this GUI event. Buttons can change a
            // recipe, but the measured rectangles remain valid until repaint.
            SpellRecipe shown=_world.Equipped==null?null:_world.Equipped.Copy();
            SpellQuote quote=_world.QuoteEquipped();
            SpellRecipe parsed=SpellRecipe.FromParsed(_world.Parsed);
            SpellQuote parsedQuote=_world.QuoteParsed();
            float resultHeight=ParsedCardHeight(parsed,parsedQuote);
            float fixtureHeight=ParagraphHeight(FixtureGuide,_small,504,50);
            float contentHeight=WorkbenchContentHeight(resultHeight,fixtureHeight,shown,quote);
            Rect view=new Rect(38,152,538,panelHeight-60);
            _workScroll=GUI.BeginScrollView(view,_workScroll,new Rect(0,0,516,contentHeight));
            bool previousEnabled=GUI.enabled;
            try { DrawWorkbenchContents(resultHeight,fixtureHeight,shown,quote,parsed,parsedQuote); }
            finally { GUI.enabled=previousEnabled;GUI.EndScrollView(); }
        }

        private void DrawWorkbenchContents(float resultHeight,float fixtureHeight,SpellRecipe shown,SpellQuote quote,SpellRecipe parsedRecipe,SpellQuote parsedQuote)
        {
            bool controlsEnabled=GUI.enabled;
            bool composing=!string.IsNullOrEmpty(Input.compositionString);
            bool showRawDetails=_showRawSpell;
            Text(new Rect(2,0,508,20),"1. 入力 → 2. 解析 → 3. 装備して戻る → 左クリックで発動",_small,Muted);
            GUI.enabled=controlsEnabled&&!_world.ParseBusy&&_world.DirectWeightsAvailable;
            if(Button(new Rect(2,26,248,28),"詠唱解析",_world.UseDirectWeights,Violet))_world.SetParserMode(true);
            GUI.enabled=controlsEnabled&&!_world.ParseBusy;
            if(Button(new Rect(258,26,248,28),"短文解析（従来）",!_world.UseDirectWeights,Teal))_world.SetParserMode(false);
            GUI.enabled=controlsEnabled;
            Text(new Rect(2,60,504,34),_world.ModelStatus,_small,Muted);
            Text(new Rect(2,96,270,20),"1. 詠唱を入力",_heading,Violet);
            int inputLimit=_world.UseDirectWeights?MagicParser.MaxInputLength:MagicParser.MaxLearnedInputLength;
            Text(new Rect(286,97,220,19),composing?"Enterで変換を確定":_chant.Length+" / "+inputLimit+"文字",_rightSmall,_chant.Length>inputLimit?Error:Muted);

            float inputHeight=ParagraphHeight(_chant+"\n",_input,482,80);
            _chantScroll=GUI.BeginScrollView(new Rect(2,120,504,84),_chantScroll,new Rect(0,0,482,inputHeight));
            try
            {
                GUI.SetNextControlName("mana-chant");
                string previousInput=_chant;
                // Never truncate at the parser boundary: a long pasted suffix
                // may contain a condition that must not be silently discarded.
                _chant=GUI.TextArea(new Rect(0,0,482,inputHeight),_chant,MagicParser.MaxInputLength*4,_input);
                if(GUI.GetNameOfFocusedControl()=="mana-chant")
                {
                    Input.imeCompositionMode=IMECompositionMode.On;
                    FollowChantCursor(previousInput,inputHeight);
                }
                else _chantCursor=-1;
            }
            finally { GUI.EndScrollView(); }
            string[] samples=_world.UseDirectWeights?ChantExamples:ShortExamples;
            for(int i=0;i<ExampleLabels.Length;i++)
                if(Button(new Rect(2+(i%4)*128,212+(i/4)*32,120,26),ExampleLabels[i],false,Muted))SetChant(samples[i]);
            GUI.enabled=controlsEnabled&&!_world.ParseBusy&&!composing&&string.IsNullOrEmpty(Input.compositionString);
            if(Button(new Rect(2,282,180,32),_world.ParseBusy?"解析中…":"詠唱を解析",true,Violet))AnalyzeChant();
            GUI.enabled=controlsEnabled;
            if(Button(new Rect(192,282,314,32),"数値指定の例を入力",false,Teal))SetChant("heat r=0 temperature=350 duration=10 target=iron");
            if(Event.current.type==EventType.KeyDown&&Event.current.control&&Event.current.keyCode==KeyCode.Return&&
               !_world.ParseBusy&&controlsEnabled&&!composing&&string.IsNullOrEmpty(Input.compositionString))
            { AnalyzeChant();Event.current.Use(); }

            var parsed=_world.Parsed;
            bool current=IsCurrentResult();
            bool ready=current&&parsed!=null&&parsed.Success&&!composing&&parsedRecipe!=null;
            Color resultColor=ready?Teal:(current&&parsed!=null&&!parsed.Success?Error:Muted);
            Fill(new Rect(2,ResultTop,504,resultHeight),new Color(0.04f,0.075f,0.11f,1));
            string heading=_world.ParseBusy?"2. 解析しています…":
                parsed==null?"2. 解析結果を確認":!current?"2. 入力が変更されています":
                parsed.Success?"2. 解析結果：発動内容を確認":"2. この詠唱は使えません";
            Text(new Rect(14,ResultTop+8,478,20),heading,_heading,resultColor);
            if(ready)
            {
                string summary=SpellSummary(parsedRecipe);
                float summaryHeight=ParagraphHeight(summary,_label,478,46);
                Text(new Rect(14,ResultTop+32,478,summaryHeight),summary,_label,Ink);
                string cost=QuoteText(parsedQuote,parsedRecipe);
                float costHeight=ParagraphHeight(cost,_small,478,44);
                float detailY=ResultTop+40+summaryHeight+costHeight;
                Text(new Rect(14,ResultTop+38+summaryHeight,478,costHeight),cost,_small,parsedQuote.Success?Teal:Error);
                if(Button(new Rect(14,detailY+8,168,22),showRawDetails?"術式の詳細を閉じる":"術式の詳細を表示",false,Muted))_showRawSpell=!showRawDetails;
                Text(new Rect(190,detailY+10,302,20),"見積りは現在の照準。発動時に再計算。",_small,Muted);
                if(showRawDetails)Text(new Rect(14,detailY+38,478,ParagraphHeight(parsed.Canonical,_small,478,32)),parsed.Canonical,_small,Muted);
            }
            else Text(new Rect(14,ResultTop+32,478,resultHeight-39),ParseMessage(),_label,current&&parsed!=null?Error:Muted);
            GUI.enabled=controlsEnabled&&ready&&!_world.ParseBusy;
            if(Button(new Rect(2,ResultTop+resultHeight+10,504,34),"この術式を装備して戻る",true,Teal))
            {
                _world.EquipParsed();GUI.enabled=controlsEnabled;return;
            }
            GUI.enabled=controlsEnabled;

            float y=AdjustmentPanel(ResultTop+resultHeight+60,true,shown,quote);
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
            int shownEntries=Math.Min(5,_world.Journal.Count);
            for(int i=0;i<shownEntries;i++)
            {
                string entry=JournalText(_world.Journal[i]);
                float rowHeight=ParagraphHeight(entry,_small,504,40);
                Text(new Rect(2,y,504,rowHeight),entry,_small,i==0?Ink:Muted);
                y+=rowHeight+8;
            }
            if(shownEntries==0)Text(new Rect(2,y,504,48),"Eで対象を観測するか、魔法や実験を使うと、ここへ結果が記録されます。",_small,Muted);
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

        private float ParsedCardHeight(SpellRecipe recipe,SpellQuote quote)
        {
            if(IsCurrentResult()&&recipe!=null)
            {
                float height=84+ParagraphHeight(SpellSummary(recipe),_label,478,46)+ParagraphHeight(QuoteText(quote,recipe),_small,478,44);
                if(_showRawSpell)height+=12+ParagraphHeight(_world.Parsed.Canonical,_small,478,32);
                return height;
            }
            return Mathf.Max(110,ParagraphHeight(ParseMessage(),_label,478,60)+42);
        }

        private float WorkbenchContentHeight(float resultHeight,float fixtureHeight,SpellRecipe recipe,SpellQuote quote)
        {
            float y=AdjustmentPanel(ResultTop+resultHeight+60,false,recipe,quote)+116+fixtureHeight+18+130+38;
            int shown=Math.Min(5,_world.Journal.Count);
            if(shown==0)y+=48;
            for(int i=0;i<shown;i++)y+=ParagraphHeight(JournalText(_world.Journal[i]),_small,504,40)+8;
            return y+18;
        }

        // Shared measurement/drawing walk keeps all controls inside the scroll
        // content, even for long Japanese failure messages or an expanded quote.
        private float AdjustmentPanel(float y,bool draw,SpellRecipe shown,SpellQuote quote)
        {
            if(draw)Section(y,"装備中の術式を調整");
            y+=40;
            if(shown==null)
            {
                if(draw)Text(new Rect(2,y,504,70),"1～7で基本術式を選ぶか、詠唱を解析して装備してください。",_label,Muted);
                return y+88;
            }
            if(draw)Text(new Rect(2,y,504,22),shown.Name+" ／ "+shown.Source,_small,Muted);
            y+=28;
            if(draw)
            {
                Text(new Rect(2,y,170,26),SpellRecipe.RangeLabel(shown.Radius),_label,Ink);
                int radius=Mathf.RoundToInt(GUI.HorizontalSlider(new Rect(180,y+8,320,18),shown.Radius,0,5));
                if(radius!=shown.Radius) { _world.Equipped.Radius=radius;_world.ManualSpellChanged(); }
            }
            y+=36;
            if(SupportsTargetFilter(shown.Operation))
            {
                if(draw)
                {
                    Text(new Rect(2,y,504,22),"対象素材："+(shown.HasMaterialFilter?JapaneseNames.Material(shown.TargetMaterial)+"のみ":"指定しない"),_small,Ink);
                    if(Button(new Rect(2,y+26,110,28),"指定なし",!shown.HasMaterialFilter,Teal))SetMaterialFilter(false,MaterialId.Air);
                    if(Button(new Rect(120,y+26,110,28),"鉄のみ",shown.HasMaterialFilter&&shown.TargetMaterial==MaterialId.Iron,Teal))SetMaterialFilter(true,MaterialId.Iron);
                    if(Button(new Rect(238,y+26,130,28),"前の素材",false,Muted))CycleMaterialFilter(-1);
                    if(Button(new Rect(376,y+26,130,28),"次の素材",false,Muted))CycleMaterialFilter(1);
                }
                y+=62;
            }
            if(shown.IsThermal)
            {
                if(draw)
                {
                    if(Button(new Rect(2,y,248,28),"温度で指定",shown.ThermalMode==ThermalMode.Temperature,Teal))SetThermalMode(ThermalMode.Temperature);
                    if(Button(new Rect(258,y,248,28),"熱量で指定",shown.ThermalMode==ThermalMode.Energy,Teal))SetThermalMode(ThermalMode.Energy);
                }
                y+=36;
                if(shown.ThermalMode==ThermalMode.Temperature)
                    NumericRow(y,draw,"目標温度",ref _temperatureDraft,"°C","temperature");
                else NumericRow(y,draw,shown.Operation==SpellOperation.Heat?"合計の熱量":"最大除去熱量",ref _powerDraft,"J","power");
                y+=38;
                NumericRow(y,draw,"抵抗保護の時間",ref _durationDraft,"秒","duration");
                y+=38;
            }
            else if(shown.Operation==SpellOperation.Charge)
            {
                NumericRow(y,draw,"最大充填量",ref _powerDraft,"MU","power");y+=38;
            }
            else if(shown.Operation==SpellOperation.Build)
            {
                if(draw)
                {
                    Text(new Rect(2,y,504,22),"配置する素材："+JapaneseNames.Material(shown.Material),_small,Ink);
                    for(int i=0;i<BuildMaterials.Length;i++)
                        if(Button(new Rect(2+i*102,y+26,96,28),JapaneseNames.Material(BuildMaterials[i]),shown.Material==BuildMaterials[i],Teal)&&
                           _world.Equipped.Material!=BuildMaterials[i])
                        { _world.Equipped.Material=BuildMaterials[i];_world.ManualSpellChanged(); }
                }
                y+=62;
            }
            else if(shown.Operation==SpellOperation.Move)
            {
                if(draw)
                {
                    Text(new Rect(2,y,504,22),"方向："+SpellRecipe.DirectionLabel(shown.Direction),_small,Ink);
                    for(int i=0;i<MoveDirections.Length;i++)
                        if(Button(new Rect(2+(i%5)*102,y+26+(i/5)*34,96,28),MoveDirectionLabels[i],shown.Direction==MoveDirections[i],Teal)&&
                           _world.Equipped.Direction!=MoveDirections[i])
                        { _world.Equipped.Direction=MoveDirections[i];_world.ManualSpellChanged(); }
                }
                y+=98;
                NumericRow(y,draw,"移動距離",ref _distanceDraft,"m","distance");y+=38;
            }
            string guide=AdjustmentGuide(shown);
            float guideHeight=ParagraphHeight(guide,_small,504,38);
            if(draw)Text(new Rect(2,y,504,guideHeight),guide,_small,Muted);
            y+=guideHeight+12;
            string costs=QuoteText(quote,shown);
            float costHeight=ParagraphHeight(costs,_small,478,56);
            if(draw)
            {
                Fill(new Rect(2,y,504,costHeight+20),new Color(.04f,.075f,.11f,1));
                Text(new Rect(14,y+10,478,costHeight),costs,_small,quote.Success?Teal:Error);
            }
            y+=costHeight+30;
            if(draw)
            {
                bool enabled=GUI.enabled;
                GUI.enabled=enabled&&!string.IsNullOrWhiteSpace(shown.Incantation)&&!_world.ParseBusy;
                if(Button(new Rect(2,y,504,29),"装備中の詠唱を入力欄へ戻す",false,Violet))
                {
                    SetChant(shown.Incantation);
                    if(_world.DirectWeightsAvailable)_world.SetParserMode(true);
                    _workScroll=Vector2.zero;
                }
                GUI.enabled=enabled;
            }
            y+=38;
            const string editNotice="数値・範囲・素材などを手動で変えると、以前の詠唱による軽減を解除します。変更した内容で詠唱を再解析すると、その詠唱の効率を適用できます。";
            float noticeHeight=ParagraphHeight(editNotice,_small,504,36);
            if(draw)Text(new Rect(2,y,504,noticeHeight),editNotice,_small,Muted);
            y+=noticeHeight+8;
            if(draw&&!string.IsNullOrEmpty(_adjustmentError))Text(new Rect(2,y,504,38),_adjustmentError,_small,Error);
            return y+56;
        }

        private static bool SupportsTargetFilter(SpellOperation operation)
        {
            return operation==SpellOperation.Heat||operation==SpellOperation.Cool||operation==SpellOperation.Charge||
                operation==SpellOperation.Move||operation==SpellOperation.Excavate;
        }

        private string AdjustmentGuide(SpellRecipe recipe)
        {
            if(recipe.IsThermal)
            {
                float duration=recipe.DurationSeconds>0?recipe.DurationSeconds:_world.Simulator.Config.DirectedThermalSeconds;
                string mode=recipe.ThermalMode==ThermalMode.Temperature?
                    "目標温度へ一度だけ熱を加減します。必要熱量と魔力は対象の現在状態から計算します。":
                    "熱量は指定範囲全体の合計です。対象素材を指定すると、その素材だけへ配分します。";
                return mode+" 発動後"+duration.ToString("0.##",CultureInfo.InvariantCulture)+
                    "シミュレーション秒は自然抵抗と周囲への熱漏れを抑えます。自動補熱・定温維持ではありません。";
            }
            if(recipe.Operation==SpellOperation.Charge)
                return "物質の保有容量の空きへ、自分の魔力を移します。移す魔力そのものは軽減せず、制御費だけに詠唱の効率を適用します。空気と蒸気は充填対象にしません。";
            if(recipe.Operation==SpellOperation.Transmute)
                return "鉄の温度250℃以上・保有魔力6 MU以上が必要です。鉄を狙い、1番で加熱 → 5番で充填 → 6番で錬成。保有魔力6 MU/セルは詠唱によって軽減しません。";
            if(recipe.Operation==SpellOperation.Move)
                return "物質を1～8 m、1 m刻みで移動します。空気と蒸気を除き、移動経路に障害物があると全体を中止します。前後左右は発動時の視点から最も近い水平方向です。移動先の枠で確認できます。";
            if(recipe.Operation==SpellOperation.Build)
                return "照準面の隣の空きへ素材を配置します。必要魔力は実際に配置するセル数と詠唱の効率から計算します。素材は創作モードの外部在庫から供給します。";
            return "指定範囲の物質を取り除きます。対象素材を指定した場合は、その素材だけを掘削します。必要魔力は変更セル数と詠唱の効率から計算します。";
        }

        private void EnsureNumberDrafts()
        {
            SpellRecipe recipe=_world.Equipped;
            if(recipe==null)return;
            bool changed=recipe!=_numberRecipe;
            if(changed)_adjustmentError=null;
            if(changed||recipe.Power!=_lastPower)_powerDraft=recipe.Power.ToString("0.########",CultureInfo.InvariantCulture);
            if(changed||recipe.TargetTemperature!=_lastTemperature)_temperatureDraft=recipe.TargetTemperature.ToString("0.########",CultureInfo.InvariantCulture);
            if(changed||recipe.DurationSeconds!=_lastDuration)_durationDraft=(recipe.DurationSeconds>0?recipe.DurationSeconds:_world.Simulator.Config.DirectedThermalSeconds).ToString("0.########",CultureInfo.InvariantCulture);
            if(changed||recipe.Distance!=_lastDistance)_distanceDraft=recipe.Distance.ToString(CultureInfo.InvariantCulture);
            _lastPower=recipe.Power;_lastTemperature=recipe.TargetTemperature;_lastDuration=recipe.DurationSeconds;_lastDistance=recipe.Distance;
            _numberRecipe=recipe;
        }

        private void NumericRow(float y,bool draw,string label,ref string draft,string unit,string field)
        {
            if(!draw)return;
            Text(new Rect(2,y+3,154,26),label,_small,Ink);
            GUI.SetNextControlName("mana-number-"+field);
            draft=GUI.TextArea(new Rect(160,y,176,29),draft??"",40,_numericInput);
            Text(new Rect(344,y+4,50,24),unit,_small,Muted);
            bool enabled=GUI.enabled;
            GUI.enabled=enabled&&string.IsNullOrEmpty(Input.compositionString);
            if(Button(new Rect(404,y,102,29),"適用",false,Teal))ApplyNumeric(field,draft);
            GUI.enabled=enabled;
        }

        private void ApplyNumeric(string field,string text)
        {
            SpellRecipe recipe=_world.Equipped;
            if(recipe==null)return;
            float value;
            try
            {
                if(!float.TryParse((text??"").Normalize(System.Text.NormalizationForm.FormKC).Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out value)||
                    float.IsNaN(value)||float.IsInfinity(value))
                { _adjustmentError="有限の数値を入力してください。単位は右側に表示しています。";return; }
            }
            catch(ArgumentException) { _adjustmentError="入力できない文字があります。数値を入力し直してください。";return; }
            bool changed=false;
            if(field=="temperature")
            {
                if(value<MaterialCatalog.AbsoluteZero||value>5000) { _adjustmentError="目標温度は−273.15～5000℃の範囲で指定してください。";return; }
                changed=recipe.TargetTemperature!=value;recipe.TargetTemperature=value;
            }
            else if(field=="duration")
            {
                if(value<1||value>30) { _adjustmentError="抵抗保護の時間は1～30シミュレーション秒で指定してください。";return; }
                changed=recipe.DurationSeconds!=value;recipe.DurationSeconds=value;
            }
            else if(field=="distance")
            {
                if(value<1||value>8||value!=(int)value) { _adjustmentError="移動距離は1～8 mの整数で指定してください。";return; }
                changed=recipe.Distance!=(int)value;recipe.Distance=(int)value;
            }
            else
            {
                float maximum=recipe.Operation==SpellOperation.Charge?10000:8000000;
                if(value<=0||value>maximum) { _adjustmentError=recipe.Operation==SpellOperation.Charge?"充填量は0より大きく10,000 MU以下です。":"熱量は0より大きく8,000,000 J以下です。";return; }
                changed=recipe.Power!=value;recipe.Power=value;
            }
            _adjustmentError=null;
            if(changed)_world.ManualSpellChanged();
        }

        private void SetThermalMode(ThermalMode mode)
        {
            SpellRecipe recipe=_world.Equipped;
            if(recipe==null||recipe.ThermalMode==mode)return;
            recipe.ThermalMode=mode;
            if(mode==ThermalMode.Temperature)
            {
                recipe.TargetTemperature=recipe.Operation==SpellOperation.Heat?350:20;
                if(recipe.DurationSeconds<=0)recipe.DurationSeconds=10;
            }
            else if(recipe.Power<=0)recipe.Power=120000;
            _world.ManualSpellChanged();
        }

        private void SetMaterialFilter(bool filtered,MaterialId material)
        {
            SpellRecipe recipe=_world.Equipped;
            if(recipe==null||(recipe.HasMaterialFilter==filtered&&(!filtered||recipe.TargetMaterial==material)))return;
            recipe.HasMaterialFilter=filtered;recipe.TargetMaterial=material;
            _world.ManualSpellChanged();
        }

        private void CycleMaterialFilter(int delta)
        {
            SpellRecipe recipe=_world.Equipped;
            if(recipe==null)return;
            int count=MaterialCatalog.Count+1;
            int current=recipe.HasMaterialFilter?(int)recipe.TargetMaterial+1:0;
            int next=(current+delta+count)%count;
            SetMaterialFilter(next>0,next>0?(MaterialId)(next-1):MaterialId.Air);
        }

        private static string QuoteText(SpellQuote quote,SpellRecipe recipe)
        {
            string text=quote.Success||quote.ManaCost>0?
                "現在の照準で必要："+Units(quote.ManaCost,"MU")+" （詠唱前 "+Units(quote.BaseManaCost,"MU")+"）":
                "必要魔力は、対象の条件を満たすと表示します。";
            int length=quote.ChantMultiplier>0?quote.EffectiveChantLength:ChantEconomy.EffectiveLength(recipe==null?null:recipe.Incantation);
            float multiplier=quote.ChantMultiplier>0?quote.ChantMultiplier:ChantEconomy.Multiplier(recipe==null?null:recipe.Incantation);
            text+="\n詠唱有効長 "+length+" ／ 制御・事象化費 ×"+multiplier.ToString("0.00",CultureInfo.InvariantCulture);
            if(recipe!=null&&recipe.IsThermal&&quote.ThermalJoules>0)text+="\n今回の熱操作量："+Units(quote.ThermalJoules,"J");
            if(quote.AffectedCells>0)text+=" ／ 対象 "+quote.AffectedCells+"セル";
            if(!quote.Success)text+="\n現在は発動できません："+(quote.Message??"対象を確認してください。");
            if(recipe!=null&&recipe.Operation==SpellOperation.Charge)text+="\n充填する魔力の元本は軽減しません。";
            return text;
        }

        private static string JournalText(JournalLine entry)
        { return "経過 "+entry.Tick+" ／ "+entry.Text; }

        private string SpellSummary(SpellRecipe recipe)
        {
            string text="効果："+JapaneseNames.Operation(recipe.Operation)+"　範囲："+SpellRecipe.RangeLabel(recipe.Radius);
            if(recipe.HasMaterialFilter)text+="\n対象素材："+JapaneseNames.Material(recipe.TargetMaterial)+"のみ";
            if(recipe.IsThermal)
            {
                text+=recipe.ThermalMode==ThermalMode.Temperature?"\n目標温度："+recipe.TargetTemperature.ToString("0.##",CultureInfo.InvariantCulture)+" °C":
                    "\n"+(recipe.Operation==SpellOperation.Heat?"指定した総熱量：":"最大除去熱量：")+Units(recipe.Power,"J");
                float duration=recipe.DurationSeconds>0?recipe.DurationSeconds:_world.Simulator.Config.DirectedThermalSeconds;
                return text+"\n抵抗・熱漏れの保護："+duration.ToString("0.##",CultureInfo.InvariantCulture)+"秒（補熱なし）";
            }
            if(recipe.Operation==SpellOperation.Charge)return text+"\n最大充填量："+Units(recipe.Power,"MU");
            if(recipe.Operation==SpellOperation.Build)return text+"\n生成する素材："+JapaneseNames.Material(recipe.Material);
            if(recipe.Operation==SpellOperation.Transmute)return text+"\n250℃以上・保有6 MU以上の鉄を錬成します。";
            if(recipe.Operation==SpellOperation.Move)return text+"\n"+SpellRecipe.DirectionLabel(recipe.Direction)+"へ "+recipe.Distance+" m移動します。";
            return text+"\n範囲内の物質を掘削します。";
        }

        private static string ShortDirection(SpellDirection direction)
        {
            for(int i=0;i<MoveDirections.Length;i++)if(MoveDirections[i]==direction)return MoveDirectionLabels[i];
            return "不明";
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
