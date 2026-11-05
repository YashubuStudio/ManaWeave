using System;
using System.IO;
using System.Security.Cryptography;

namespace ManaWeave.Core
{
    /// <summary>
    /// Versioned bounded binary format with SHA-256 integrity check. Save uses a same-directory temporary file
    /// and atomic rename/replace. Cell data and all mana stores are saved; transient UI and statistics are not.
    /// </summary>
    public static class WorldPersistence
    {
        const uint Magic = 0x444C574D; // MWLD, little endian
        const int Version = 1;
        const int MaximumBytes = 64 * 1024 * 1024;

        public static void Save(WorldState world, string path)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Save path is required.", "path");
            ValidateState(world);
            byte[] payload;
            using (var buffer = new MemoryStream())
            {
                using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(world.Width); writer.Write(world.Height); writer.Write(world.Depth); writer.Write(world.Seed);
                    writer.Write(world.Tick); writer.Write(world.SimulatedSeconds);
                    writer.Write(world.CasterMana); writer.Write(world.CasterManaMax);
                    writer.Write(world.CasterX); writer.Write(world.CasterY); writer.Write(world.CasterZ);
                    writer.Write(world.Cells.Length);
                    for (int i = 0; i < world.Cells.Length; i++)
                    {
                        Cell c = world.Cells[i]; writer.Write((byte)c.Material); writer.Write(c.Mass); writer.Write(c.Energy);
                        writer.Write(c.AmbientMana); writer.Write(c.StoredMana); writer.Write(c.Fuel);
                    }
                }
                payload = buffer.ToArray();
            }
            byte[] hash; using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(payload);
            string absolute = Path.GetFullPath(path), directory = Path.GetDirectoryName(absolute);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string temporary = absolute + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(Magic); writer.Write(Version); writer.Write(payload.Length); writer.Write(hash); writer.Write(payload);
                        writer.Flush();
                    }
                    file.Flush(true);
                }
                if (File.Exists(absolute)) File.Replace(temporary, absolute, null);
                else File.Move(temporary, absolute);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static WorldState Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Save path is required.", "path");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(file))
            {
                if (file.Length < 44 || file.Length > MaximumBytes) throw new InvalidDataException("Invalid save length.");
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Not a ManaWeave world.");
                if (reader.ReadInt32() != Version) throw new InvalidDataException("Unsupported save version.");
                int bytes = reader.ReadInt32();
                if (bytes < 56 || bytes > MaximumBytes - 44 || bytes != file.Length - 44)
                    throw new InvalidDataException("Invalid payload length.");
                byte[] expected = reader.ReadBytes(32), payload = reader.ReadBytes(bytes), actual;
                if (payload.Length != bytes) throw new InvalidDataException("Truncated save.");
                using (SHA256 sha = SHA256.Create()) actual = sha.ComputeHash(payload);
                for (int i = 0; i < actual.Length; i++)
                    if (actual[i] != expected[i]) throw new InvalidDataException("Save integrity check failed.");
                using (var buffer = new MemoryStream(payload, false))
                using (var data = new BinaryReader(buffer))
                {
                    int width = data.ReadInt32(), height = data.ReadInt32(), depth = data.ReadInt32(), seed = data.ReadInt32();
                    // Check dimensions before allocating a world from untrusted save bytes.
                    if (width < 1 || width > 256 || height < 1 || height > 256 || depth < 1 || depth > 256 ||
                        (long)width * height * depth > WorldState.MaximumCells)
                        throw new InvalidDataException("Invalid world dimensions.");
                    long tick = data.ReadInt64(); double seconds = data.ReadDouble();
                    float mana = data.ReadSingle(), maxMana = data.ReadSingle();
                    int casterX = data.ReadInt32(), casterY = data.ReadInt32(), casterZ = data.ReadInt32();
                    int count = data.ReadInt32();
                    if (count != width * height * depth || buffer.Length - buffer.Position != (long)count * 21)
                        throw new InvalidDataException("Invalid cell count.");
                    var w = new WorldState(width, height, depth, seed) { Tick = tick, SimulatedSeconds = seconds,
                        CasterMana = mana, CasterManaMax = maxMana, CasterX = casterX, CasterY = casterY, CasterZ = casterZ };
                    for (int i = 0; i < count; i++)
                    {
                        w.Cells[i] = new Cell { Material = (MaterialId)data.ReadByte(), Mass = data.ReadSingle(),
                            Energy = data.ReadSingle(), AmbientMana = data.ReadSingle(), StoredMana = data.ReadSingle(), Fuel = data.ReadSingle() };
                        w.Dirty.Add(i);
                    }
                    ValidateState(w); return w;
                }
            }
        }

        static void ValidateState(WorldState w)
        {
            if (w.Tick < 0 || double.IsNaN(w.SimulatedSeconds) || double.IsInfinity(w.SimulatedSeconds) || w.SimulatedSeconds < 0 ||
                !Nonnegative(w.CasterMana) || !Nonnegative(w.CasterManaMax) || w.CasterManaMax > 1000000000f ||
                w.CasterMana > w.CasterManaMax + .001f || !w.InBounds(w.CasterX, w.CasterY, w.CasterZ))
                throw new InvalidDataException("Invalid player or simulation state.");
            for (int i = 0; i < w.Cells.Length; i++)
            {
                Cell c = w.Cells[i];
                if (!MaterialCatalog.IsValid(c.Material) || !Nonnegative(c.Mass) || c.Mass > 1000000000f ||
                    !Nonnegative(c.Energy) || c.Energy > 1e20f || !Nonnegative(c.AmbientMana) || c.AmbientMana > 1e12f ||
                    !Nonnegative(c.StoredMana) || c.StoredMana > 1e12f || !Nonnegative(c.Fuel) || c.Fuel > c.Mass + .001f)
                    throw new InvalidDataException("Invalid material state at cell " + i + ".");
                if (c.StoredMana > MaterialCatalog.StorageCapacity(c) + .01f)
                    throw new InvalidDataException("Stored mana exceeds material capacity at cell " + i + ".");
            }
        }
        static bool Nonnegative(float f) { return MaterialCatalog.Finite(f) && f >= 0f; }
    }
}
