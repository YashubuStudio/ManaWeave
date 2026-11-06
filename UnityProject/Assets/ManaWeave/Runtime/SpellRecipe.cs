using System;
using System.Globalization;
using ManaWeave.Core;
using ManaWeave.Magic;

namespace ManaWeave.Runtime
{
    /// <summary>Serializable player spell. Every execution parameter survives copies and bookmarks.</summary>
    [Serializable]
    public sealed class SpellRecipe
    {
        public string Name, Description, Source;
        public SpellOperation Operation;
        public int Radius;
        public float Power;
        public MaterialId Material;
        public ThermalMode ThermalMode;
        public float TargetTemperature, DurationSeconds;
        public bool HasMaterialFilter;
        public MaterialId TargetMaterial;
        public SpellDirection Direction = SpellDirection.Up;
        public int Distance = 1;
        public string Incantation;

        public SpellRecipe Copy()
        {
            return new SpellRecipe {
                Name=Name, Description=Description, Source=Source, Operation=Operation,
                Radius=Radius, Power=Power, Material=Material, ThermalMode=ThermalMode,
                TargetTemperature=TargetTemperature, DurationSeconds=DurationSeconds,
                HasMaterialFilter=HasMaterialFilter, TargetMaterial=TargetMaterial,
                Direction=Direction, Distance=Distance, Incantation=Incantation
            };
        }

        public static SpellRecipe FromParsed(MagicParseResult parsed)
        {
            if(parsed==null||!parsed.Success)return null;
            var recipe=new SpellRecipe {
                Name=JapaneseNames.Operation(parsed.Operation), Source=parsed.Source,
                Operation=parsed.Operation, Radius=parsed.Radius, Power=parsed.Power,
                Material=parsed.Material, ThermalMode=parsed.ThermalMode,
                TargetTemperature=parsed.TargetTemperature, DurationSeconds=parsed.DurationSeconds,
                HasMaterialFilter=parsed.HasMaterialFilter, TargetMaterial=parsed.TargetMaterial,
                Direction=parsed.Direction, Distance=parsed.Distance, Incantation=parsed.Incantation
            };
            recipe.Description=recipe.Describe();
            return recipe;
        }

        public bool IsThermal { get { return Operation==SpellOperation.Heat||Operation==SpellOperation.Cool; } }

        // Presentation fields do not define an effect; chant text does define its cost.
        public bool SameEffectAs(SpellRecipe other)
        {
            return other!=null&&Operation==other.Operation&&Radius==other.Radius&&Power==other.Power&&
                Material==other.Material&&ThermalMode==other.ThermalMode&&TargetTemperature==other.TargetTemperature&&
                DurationSeconds==other.DurationSeconds&&HasMaterialFilter==other.HasMaterialFilter&&TargetMaterial==other.TargetMaterial&&
                Direction==other.Direction&&Distance==other.Distance&&string.Equals(Incantation??"",other.Incantation??"",StringComparison.Ordinal);
        }

        public bool ValidForBookmark()
        {
            if(!Enum.IsDefined(typeof(SpellOperation),Operation)||!MaterialCatalog.IsValid(Material)||
                !MaterialCatalog.IsValid(TargetMaterial)||!Enum.IsDefined(typeof(ThermalMode),ThermalMode)||
                !Enum.IsDefined(typeof(SpellDirection),Direction)||Radius<0||Radius>5||
                !Finite(Power)||Power<0||!Finite(TargetTemperature)||!Finite(DurationSeconds)||
                DurationSeconds<0||DurationSeconds>30||(DurationSeconds>0&&DurationSeconds<1)||
                (Incantation!=null&&Incantation.Length>MagicParser.MaxInputLength))return false;
            if(IsThermal)
            {
                if(ThermalMode==ThermalMode.Energy&&(Power<=0||Power>8000000f))return false;
                if(ThermalMode==ThermalMode.Temperature&&(TargetTemperature<MaterialCatalog.AbsoluteZero||TargetTemperature>5000))return false;
            }
            else if(ThermalMode!=ThermalMode.Energy||DurationSeconds!=0)return false;
            if(Operation==SpellOperation.Build&&HasMaterialFilter)return false;
            if(Operation==SpellOperation.Charge&&(Power<=0||Power>10000f))return false;
            if(Operation==SpellOperation.Move&&(Distance<1||Distance>8))return false;
            if(Incantation!=null)
            {
                try
                {
                    if(Incantation.Normalize(System.Text.NormalizationForm.FormKC).Length>MagicParser.MaxInputLength)return false;
                }
                catch(ArgumentException) { return false; }
            }
            return true;
        }

        public string Describe()
        {
            string text=JapaneseNames.Operation(Operation)+" / "+RangeLabel(Radius);
            if(HasMaterialFilter)text+=" / 対象："+JapaneseNames.Material(TargetMaterial);
            if(IsThermal)
            {
                text+=ThermalMode==ThermalMode.Temperature?" / 目標 "+F(TargetTemperature)+" °C":
                    " / "+(Operation==SpellOperation.Heat?"総熱量 ":"最大除去熱量 ")+F(Power)+" J";
                if(DurationSeconds>0)text+=" / 抵抗保護 "+F(DurationSeconds)+" 秒";
                return text;
            }
            if(Operation==SpellOperation.Charge)return text+" / 最大充填量 "+F(Power)+" MU";
            if(Operation==SpellOperation.Build)return text+" / 素材："+JapaneseNames.Material(Material);
            if(Operation==SpellOperation.Transmute)return text+" / 条件を満たす鉄を魔導合金へ";
            if(Operation==SpellOperation.Move)return text+" / "+DirectionLabel(Direction)+"へ "+Distance+" m";
            return text+" / 対象範囲の物質を取り除く";
        }

        public static string RangeLabel(int radius)
        { return radius==0?"照準の1セル":"半径 "+radius+" m"; }

        public static string DirectionLabel(SpellDirection direction)
        {
            switch(direction)
            {
                case SpellDirection.Up:return "上";
                case SpellDirection.Down:return "下";
                case SpellDirection.North:return "北（＋Z）";
                case SpellDirection.South:return "南（−Z）";
                case SpellDirection.East:return "東（＋X）";
                case SpellDirection.West:return "西（−X）";
                case SpellDirection.Forward:return "前（視点の水平方向）";
                case SpellDirection.Backward:return "後ろ（視点の水平方向）";
                case SpellDirection.Left:return "左（視点基準）";
                case SpellDirection.Right:return "右（視点基準）";
                default:return "不明な方向";
            }
        }

        private static bool Finite(float value) { return !float.IsNaN(value)&&!float.IsInfinity(value); }
        private static string F(float value) { return value.ToString("0.###",CultureInfo.InvariantCulture); }
    }
}
