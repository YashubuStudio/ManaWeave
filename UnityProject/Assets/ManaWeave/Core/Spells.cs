namespace ManaWeave.Core
{
    public enum SpellOperation { Heat, Cool, Excavate, Build, Charge, Transmute }

    public sealed class SpellCommand
    {
        public SpellOperation Operation;
        public int X, Y, Z, Radius = 1;
        public float Power = 120000f;
        public MaterialId Material = MaterialId.Stone;
    }

    public struct SpellResult
    {
        public bool Success;
        public string Message;
        public float ManaCost;
        public static SpellResult Fail(string message)
        { return new SpellResult { Success = false, Message = message, ManaCost = 0 }; }
    }
}
