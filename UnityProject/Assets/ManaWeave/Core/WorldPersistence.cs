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
        const int Version = 2;
        const int MaximumBytes = 72 * 1024 * 1024; // 33 bytes * MaximumCells, plus header, remains bounded below this.

        public static void Save(WorldState world, string path)
        {
            if (world == null) throw new ArgumentNullException("world");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("保存先を指定してください。", "path");
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
                        writer.Write(c.ThermalCommandUntil);
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
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("保存先を指定してください。", "path");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(file))
            {
                if (file.Length < 44 || file.Length > MaximumBytes) throw new InvalidDataException("保存データのサイズが不正です。");
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("ManaWeaveの世界データではありません。");
                int version = reader.ReadInt32();
                if (version != 1 && version != Version) throw new InvalidDataException("この保存形式のバージョンには対応していません。");
                int bytes = reader.ReadInt32();
                if (bytes < 56 || bytes > MaximumBytes - 44 || bytes != file.Length - 44)
                    throw new InvalidDataException("保存データの内容とサイズが一致しません。");
                byte[] expected = reader.ReadBytes(32), payload = reader.ReadBytes(bytes), actual;
                if (payload.Length != bytes) throw new InvalidDataException("保存データが途中で切れています。");
                using (SHA256 sha = SHA256.Create()) actual = sha.ComputeHash(payload);
                for (int i = 0; i < actual.Length; i++)
                    if (actual[i] != expected[i]) throw new InvalidDataException("保存データの整合性検査に失敗しました。破損している可能性があります。");
                using (var buffer = new MemoryStream(payload, false))
                using (var data = new BinaryReader(buffer))
                {
                    int width = data.ReadInt32(), height = data.ReadInt32(), depth = data.ReadInt32(), seed = data.ReadInt32();
                    // Check dimensions before allocating a world from untrusted save bytes.
                    if (width < 1 || width > 256 || height < 1 || height > 256 || depth < 1 || depth > 256 ||
                        (long)width * height * depth > WorldState.MaximumCells)
                        throw new InvalidDataException("保存データ内の世界の大きさが不正です。");
                    long tick = data.ReadInt64(); double seconds = data.ReadDouble();
                    float mana = data.ReadSingle(), maxMana = data.ReadSingle();
                    int casterX = data.ReadInt32(), casterY = data.ReadInt32(), casterZ = data.ReadInt32();
                    int count = data.ReadInt32();
                    int cellBytes = version == 1 ? 21 : 33;
                    if (count != width * height * depth || buffer.Length - buffer.Position != (long)count * cellBytes)
                        throw new InvalidDataException("保存データ内のセル数が一致しません。");
                    var w = new WorldState(width, height, depth, seed) { Tick = tick, SimulatedSeconds = seconds,
                        CasterMana = mana, CasterManaMax = maxMana, CasterX = casterX, CasterY = casterY, CasterZ = casterZ };
                    for (int i = 0; i < count; i++)
                    {
                        w.Cells[i] = new Cell { Material = (MaterialId)data.ReadByte(), Mass = data.ReadSingle(),
                            Energy = version == 1 ? (double)data.ReadSingle() : data.ReadDouble(),
                            AmbientMana = data.ReadSingle(), StoredMana = data.ReadSingle(), Fuel = data.ReadSingle(),
                            ThermalCommandUntil = version == 1 ? 0.0 : data.ReadDouble() };
                        w.Dirty.Add(i);
                    }
                    ValidateState(w);
                    if (version == 1) MigrateLegacyAir(w);
                    ValidateState(w); return w;
                }
            }
        }

        static void MigrateLegacyAir(WorldState world)
        {
            // Balance migration, not a physical heating reaction: retain thermal deviation energy from the
            // reference state while adopting the new effective air mass. The original save is never written.
            // Only the old default-mass air is rebased; custom masses and all other materials are unchanged.
            int migrated = 0;
            for (int i = 0; i < world.Cells.Length; i++)
            {
                Cell c = world.Cells[i];
                if (c.Material != MaterialId.Air || c.Mass != .020f) continue;
                float oldMass = c.Mass; double oldEnergy = c.Energy;
                double deviation = oldEnergy - (double)MaterialCatalog.RoomEnergy(c);
                c.Mass = MaterialCatalog.Get(MaterialId.Air).DefaultMass;
                c.Energy = Math.Max(0.0, MaterialCatalog.RoomEnergy(c) + deviation);
                world.MigrationMassKg += c.Mass - (double)oldMass;
                world.MigrationEnergyJoules += c.Energy - (double)oldEnergy;
                world.Cells[i] = c; migrated++;
            }
            world.MigrationNotice = migrated > 0 ? "旧保存の空気 " + migrated +
                "セルを新しい有効熱容量へ移行しました。基準からの熱変化量を保持し、温度表示を再計算しています。" :
                "旧保存形式を読み込みました。次の保存から、抵抗法則に対応した新しい形式を使います。";
        }

        static void ValidateState(WorldState w)
        {
            if (w.Tick < 0 || double.IsNaN(w.SimulatedSeconds) || double.IsInfinity(w.SimulatedSeconds) || w.SimulatedSeconds < 0 ||
                !Nonnegative(w.CasterMana) || !Nonnegative(w.CasterManaMax) || w.CasterManaMax > 1000000000f ||
                w.CasterMana > w.CasterManaMax + .001f || !w.InBounds(w.CasterX, w.CasterY, w.CasterZ))
                throw new InvalidDataException("保存データ内の調査者または世界の状態が不正です。");
            for (int i = 0; i < w.Cells.Length; i++)
            {
                Cell c = w.Cells[i];
                if (!MaterialCatalog.IsValid(c.Material) || !Nonnegative(c.Mass) || c.Mass > 1000000000f ||
                    !Nonnegative(c.Energy) || c.Energy > 1e20f || !Nonnegative(c.AmbientMana) || c.AmbientMana > 1e12f ||
                    !Nonnegative(c.StoredMana) || c.StoredMana > 1e12f || !Nonnegative(c.Fuel) || c.Fuel > c.Mass + .001f ||
                    double.IsNaN(c.ThermalCommandUntil) || double.IsInfinity(c.ThermalCommandUntil) || c.ThermalCommandUntil < 0 ||
                    c.ThermalCommandUntil > w.SimulatedSeconds + SimulationConfig.MaximumDirectedThermalSeconds + .000001)
                    throw new InvalidDataException("セル " + i + " の物質データが不正です。");
                if (c.StoredMana > MaterialCatalog.StorageCapacity(c) + .01f)
                    throw new InvalidDataException("セル " + i + " の保有魔力が、物質の保有容量を超えています。");
                // Finite float fields alone are insufficient: E/(m*c) can overflow for a subnormal mass.
                // This is a representability check, not an in-game maximum temperature rule.
                double kelvinScale = c.Mass > 0f ? c.Energy / (c.Mass * (double)MaterialCatalog.Get(c.Material).HeatCapacity) : 0.0;
                if ((c.Mass == 0f && c.Energy > 0f) || kelvinScale > float.MaxValue ||
                    !MaterialCatalog.Finite(MaterialCatalog.Temperature(c)))
                    throw new InvalidDataException("セル " + i + " の質量と熱量では、有効な温度を計算できません。");
            }
        }
        static bool Nonnegative(float f) { return MaterialCatalog.Finite(f) && f >= 0f; }
        static bool Nonnegative(double value) { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0; }
    }
}
