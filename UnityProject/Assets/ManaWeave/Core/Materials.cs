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
            // Effective heat capacity for a displayed metre-sized cell. The original .020 kg was 60 times too small.
            new MaterialDefinition("Air",    .42f,.67f,.80f,1005f, .8f, .10f,.04f,1.20f,false),
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
        public static bool IsGas(MaterialId id)
        { return id == MaterialId.Air || id == MaterialId.Steam; }

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
            double h = Math.Max(0f, cell.Energy) / (double)cell.Mass;
            switch (cell.Material)
            {
                case MaterialId.Ice: return (float)Math.Max(AbsoluteZero, Math.Min(0.0, AbsoluteZero + h / 2100.0));
                case MaterialId.Water:
                    if (h <= WaterAtZero) return 0f;
                    return (float)Math.Min(100.0, (h - WaterAtZero) / 4184.0);
                case MaterialId.Steam: return (float)Math.Min(float.MaxValue, Math.Max(100.0, 100.0 + (h - SteamAtBoiling) / 2010.0));
                default: return (float)Math.Min(float.MaxValue, Math.Max(AbsoluteZero, AbsoluteZero + h / Get(cell.Material).HeatCapacity));
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
