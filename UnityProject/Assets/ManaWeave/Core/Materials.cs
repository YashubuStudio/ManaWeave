using System;

namespace ManaWeave.Core
{
    public enum MaterialId : byte { Air, Soil, Stone, Wood, Water, Ice, Steam, Iron, Crystal, Alloy, Ash }

    public sealed class MaterialDefinition
    {
        public readonly string Name;
        public readonly float ColorR, ColorG, ColorB;
        public readonly float HeatCapacity, ManaCapacity, AbsorptionRate, Conductivity, DefaultMass;
        public readonly bool Solid;

        public MaterialDefinition(string name, float r, float g, float b, float heatCapacity,
            float manaCapacity, float absorptionRate, float conductivity, float mass, bool solid)
        {
            Name = name; ColorR = r; ColorG = g; ColorB = b;
            HeatCapacity = heatCapacity; ManaCapacity = manaCapacity;
            AbsorptionRate = absorptionRate; Conductivity = conductivity;
            DefaultMass = mass; Solid = solid;
        }
    }

    /// <summary>
    /// Coarse game materials. Mass is effective simulated kg per occupied voxel, not real-world density.
    /// Water, ice and steam share one continuous enthalpy reference, including latent heat.
    /// </summary>
    public static class MaterialCatalog
    {
        public const float AbsoluteZero = -273.15f;
        public const float RoomTemperature = 20f;
        public const float IceAtZero = 2100f * 273.15f;
        public const float WaterAtZero = IceAtZero + 334000f;
        public const float WaterAtBoiling = WaterAtZero + 4184f * 100f;
        public const float SteamAtBoiling = WaterAtBoiling + 2256000f;

        static readonly MaterialDefinition[] Definitions =
        {
            new MaterialDefinition("Air",    .42f,.67f,.80f,1005f, .8f, .10f,.04f,.020f,false),
            new MaterialDefinition("Soil",   .28f,.43f,.24f, 800f,  4f, .18f,.20f,2.00f,true),
            new MaterialDefinition("Stone",  .37f,.43f,.46f, 850f,  6f, .07f,.55f,4.00f,true),
            new MaterialDefinition("Wood",   .46f,.28f,.12f,1800f, 30f, .65f,.10f,.40f,true),
            new MaterialDefinition("Water",  .10f,.59f,.69f,4184f, 12f, .50f,.65f,.25f,false),
            new MaterialDefinition("Ice",    .64f,.87f,.93f,2100f, 14f, .12f,.80f,.25f,true),
            new MaterialDefinition("Steam",  .70f,.85f,.88f,2010f,  3f, .30f,.07f,.25f,false),
            new MaterialDefinition("Iron",   .44f,.53f,.61f, 450f, 25f, .32f,2.00f,1.50f,true),
            new MaterialDefinition("Crystal",.48f,.29f,.81f, 900f,120f,3.00f,.35f,.35f,true),
            new MaterialDefinition("Alloy",  .83f,.65f,.25f, 520f, 65f,1.00f,1.30f,1.50f,true),
            new MaterialDefinition("Ash",    .24f,.25f,.27f, 900f,  2f, .08f,.12f,.40f,true)
        };

        public static int Count { get { return Definitions.Length; } }
        public static bool IsValid(MaterialId id) { return (int)id >= 0 && (int)id < Definitions.Length; }
        public static MaterialDefinition Get(MaterialId id)
        {
            if (!IsValid(id)) throw new ArgumentOutOfRangeException("id");
            return Definitions[(int)id];
        }
        public static bool IsWaterFamily(MaterialId id)
        { return id == MaterialId.Water || id == MaterialId.Ice || id == MaterialId.Steam; }

        public static float EnergyAtTemperature(MaterialId id, float mass, float temperature)
        {
            if (mass <= 0f) return 0f;
            temperature = Math.Max(AbsoluteZero, temperature);
            switch (id)
            {
                case MaterialId.Ice: return mass * Math.Max(0f, IceAtZero + 2100f * temperature);
                case MaterialId.Water: return mass * Math.Max(0f, WaterAtZero + 4184f * temperature);
                case MaterialId.Steam: return mass * Math.Max(0f, SteamAtBoiling + 2010f * (temperature - 100f));
                default: return mass * Get(id).HeatCapacity * (temperature - AbsoluteZero);
            }
        }

        public static float Temperature(Cell cell)
        {
            if (cell.Mass <= 0f) return RoomTemperature;
            float h = Math.Max(0f, cell.Energy) / cell.Mass;
            switch (cell.Material)
            {
                case MaterialId.Ice: return Math.Min(0f, AbsoluteZero + h / 2100f);
                case MaterialId.Water:
                    if (h <= WaterAtZero) return 0f;
                    return Math.Min(100f, (h - WaterAtZero) / 4184f);
                case MaterialId.Steam: return Math.Max(100f, 100f + (h - SteamAtBoiling) / 2010f);
                default: return AbsoluteZero + h / Get(cell.Material).HeatCapacity;
            }
        }

        public static float RoomEnergy(Cell cell)
        {
            // H2O is liquid at the reference state regardless of its current phase.
            MaterialId id = IsWaterFamily(cell.Material) ? MaterialId.Water : cell.Material;
            return EnergyAtTemperature(id, cell.Mass, RoomTemperature);
        }

        public static float StorageCapacity(Cell cell)
        { return Math.Max(0f, cell.Mass) * Get(cell.Material).ManaCapacity; }

        internal static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
