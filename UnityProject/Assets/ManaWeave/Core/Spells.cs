namespace ManaWeave.Core
{
    public enum SpellOperation { Heat, Cool, Excavate, Build, Charge, Transmute, Move }
    public enum ThermalMode { Energy, Temperature }
    public enum SpellDirection { Up, Down, North, South, East, West, Forward, Backward, Left, Right }

    public sealed class SpellCommand
    {
        public SpellOperation Operation;
        public int X, Y, Z, Radius = 1;
        public float Power = 120000f;
        public MaterialId Material = MaterialId.Stone;
        public ThermalMode ThermalMode;
        public float TargetTemperature;
        // Zero preserves the original Config duration; an explicit duration is 1..30 simulated seconds.
        public float DurationSeconds;
        public bool HasMaterialFilter;
        public MaterialId TargetMaterial;
        // A cardinal world-space offset. Runtime resolves view-relative direction before calling Core.
        public int OffsetX, OffsetY, OffsetZ;
        public string Incantation;
    }

    public struct SpellQuote
    {
        public bool Success;
        public string Message;
        public float ManaCost, BaseManaCost, ChantMultiplier;
        public int EffectiveChantLength, AffectedCells;
        public double ThermalJoules;
    }

    public struct SpellResult
    {
        public bool Success;
        public string Message;
        public float ManaCost, BaseManaCost, ChantMultiplier;
        public int EffectiveChantLength;
        public static SpellResult Fail(string message)
        { return new SpellResult { Success = false, Message = message, ManaCost = 0 }; }
    }
}
