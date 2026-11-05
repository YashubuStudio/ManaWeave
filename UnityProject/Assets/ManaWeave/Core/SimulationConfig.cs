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
        public float CriticalEnergy = 160000f;
        public float JoulesPerMana = 1600f;
        public float AmbientSaturation = 28f;
        public float CasterAbsorptionRate = 8f;
        public float DisturbanceThreshold = 8f;
        public float CoherenceSlope = 1.4f;
        public bool EnableCombustion = true;
        public bool EnableFluidFlow = true;
        public bool EnablePhaseChanges = true;
        public bool EnableStoredInteraction = true;

        internal void Validate()
        {
            float[] nonnegative = { ManaGeneration, ManaDiffusion, AbsorptionScale, ThermalDiffusion,
                NaturalCoupling, CasterAbsorptionRate, DisturbanceThreshold };
            for (int i = 0; i < nonnegative.Length; i++)
                if (!MaterialCatalog.Finite(nonnegative[i]) || nonnegative[i] < 0f)
                    throw new ArgumentOutOfRangeException("config", "Rates must be finite and nonnegative.");
            float[] positive = { CriticalEnergy, JoulesPerMana, AmbientSaturation, CoherenceSlope };
            for (int i = 0; i < positive.Length; i++)
                if (!MaterialCatalog.Finite(positive[i]) || positive[i] <= 0f)
                    throw new ArgumentOutOfRangeException("config", "Thresholds and units must be finite and positive.");
        }
    }

    public sealed class SimulationStats
    {
        public double GeneratedMana, ManifestedMana, StabilizedJoules, AmplifiedJoules, SpellJoules, AbsorbedMana;
        public double DirectManifestedMana, StoredManifestedMana, NaturalEnergyJoules, CombustionJoules;
        public double SandboxEnergyJoules, SandboxMassKg, ExperimentMana, ReactionJoules;
        public double TransferredMana;
        public int ActivePhenomena;
        public string LastEvent = "World initialized. Mana is generated in space and can act directly.";
    }
}
