namespace ManaWeave.Core
{
    /// <summary>Player-facing Japanese names. Enum values and stored/DSL identifiers remain unchanged.</summary>
    public static class JapaneseNames
    {
        public static string Material(MaterialId material)
        {
            switch (material)
            {
                case MaterialId.Air: return "空気";
                case MaterialId.Soil: return "土";
                case MaterialId.Stone: return "石";
                case MaterialId.Wood: return "木材";
                case MaterialId.Water: return "水";
                case MaterialId.Ice: return "氷";
                case MaterialId.Steam: return "蒸気";
                case MaterialId.Iron: return "鉄";
                case MaterialId.Crystal: return "魔導結晶";
                case MaterialId.Alloy: return "魔導合金";
                case MaterialId.Ash: return "灰";
                default: return "不明な物質";
            }
        }

        public static string Operation(SpellOperation operation)
        {
            switch (operation)
            {
                case SpellOperation.Heat: return "加熱";
                case SpellOperation.Cool: return "冷却";
                case SpellOperation.Excavate: return "掘削";
                case SpellOperation.Build: return "建築";
                case SpellOperation.Charge: return "魔力充填";
                case SpellOperation.Transmute: return "錬成";
                case SpellOperation.Move: return "物質移動";
                default: return "不明な術式";
            }
        }
    }
}
