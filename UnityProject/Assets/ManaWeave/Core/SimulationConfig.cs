using System;

namespace ManaWeave.Core
{
    public sealed class SimulationConfig
    {
        public float ManaGeneration = .42f;
        public float ManaDiffusion = .8f;
        public float AbsorptionScale = 1f;
        public float ThermalDiffusion = 2f;
        public float NaturalCoupling = .32f;
        // Legacy public fields are retained for source compatibility only. No magnitude can enable amplification.
        public float CriticalEnergy = 160000f;
        public float JoulesPerMana = 1600f;
        public float AmbientSaturation = 28f;
        public float CasterAbsorptionRate = 8f;
        public float DisturbanceThreshold = .05f;
        public float CoherenceSlope = 1.4f;
        public float GasResistanceMultiplier = 8f;
        public float DirectedResistanceMultiplier = .08f;
        public float DirectedThermalSeconds = 1f;
        public float SpatialResistance = 3f;
        public const float MaximumDirectedThermalSeconds = 30f;
        public bool EnableCombustion = true;
        public bool EnableFluidFlow = true;
        public bool EnablePhaseChanges = true;
        public bool EnableStoredInteraction = true;

        internal void Validate()
        {
            float[] nonnegative = { ManaGeneration, ManaDiffusion, AbsorptionScale, ThermalDiffusion,
                NaturalCoupling, CasterAbsorptionRate, DisturbanceThreshold, DirectedThermalSeconds, SpatialResistance };
            for (int i = 0; i < nonnegative.Length; i++)
                if (!MaterialCatalog.Finite(nonnegative[i]) || nonnegative[i] < 0f)
                    throw new ArgumentOutOfRangeException("config", "変化率には、0以上の有限な値を指定してください。");
            float[] positive = { JoulesPerMana, AmbientSaturation, GasResistanceMultiplier };
            for (int i = 0; i < positive.Length; i++)
                if (!MaterialCatalog.Finite(positive[i]) || positive[i] <= 0f)
                    throw new ArgumentOutOfRangeException("config", "しきい値と単位量には、0より大きい有限な値を指定してください。");
            if (!MaterialCatalog.Finite(DirectedResistanceMultiplier) || DirectedResistanceMultiplier < 0f ||
                DirectedResistanceMultiplier > 1f || DirectedThermalSeconds > MaximumDirectedThermalSeconds)
                throw new ArgumentOutOfRangeException("config", "指定中の抵抗倍率は0～1、指定の有効時間は0～30秒にしてください。");
        }
    }

    public sealed class SimulationStats
    {
        public double GeneratedMana, ManifestedMana, StabilizedJoules, AmplifiedJoules, SpellJoules, AbsorbedMana;
        public double DirectManifestedMana, StoredManifestedMana, NaturalEnergyJoules, CombustionJoules;
        public double SandboxEnergyJoules, SandboxMassKg, ExperimentMana, ReactionJoules;
        public double TransferredMana;
        public double SuppressedTransportJoules, DirectedStabilizedJoules;
        public int ActivePhenomena;
        public string LastEvent = "世界を生成しました。命令されていない魔力は熱変化と伝播に抵抗し、自然な増幅は起こしません。";
    }
}
