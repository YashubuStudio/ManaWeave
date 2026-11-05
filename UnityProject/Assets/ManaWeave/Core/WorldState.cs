using System;
using System.Collections.Generic;

namespace ManaWeave.Core
{
    public struct Cell
    {
        public MaterialId Material;
        public float Mass, Energy, AmbientMana, StoredMana, Fuel;
    }

    public sealed class WorldState
    {
        public const int MaximumCells = 2097152;
        public readonly int Width, Height, Depth;
        public readonly Cell[] Cells;
        public long Tick;
        public double SimulatedSeconds;
        public int Seed;
        public float CasterMana = 1800f, CasterManaMax = 1800f;
        public int CasterX, CasterY, CasterZ;
        public readonly HashSet<int> Dirty = new HashSet<int>();

        public WorldState(int width, int height, int depth, int seed)
        {
            long size = (long)width * height * depth;
            if (width < 1 || height < 1 || depth < 1 || width > 256 || height > 256 || depth > 256 || size > MaximumCells)
                throw new ArgumentOutOfRangeException("dimensions", "World must contain 1..2,097,152 cells, with each dimension <= 256.");
            Width = width; Height = height; Depth = depth; Seed = seed;
            Cells = new Cell[(int)size];
            CasterX = width / 2; CasterY = Math.Min(height - 1, 12); CasterZ = depth / 2;
            for (int i = 0; i < Cells.Length; i++)
            {
                var d = MaterialCatalog.Get(MaterialId.Air);
                Cells[i] = new Cell { Material = MaterialId.Air, Mass = d.DefaultMass,
                    Energy = MaterialCatalog.EnergyAtTemperature(MaterialId.Air, d.DefaultMass, 20f),
                    AmbientMana = 14f };
            }
        }

        public bool InBounds(int x, int y, int z)
        { return x >= 0 && x < Width && y >= 0 && y < Height && z >= 0 && z < Depth; }
        public int Index(int x, int y, int z)
        {
            if (!InBounds(x, y, z)) throw new ArgumentOutOfRangeException("position");
            return x + Width * (y + Height * z);
        }
        public void Coordinates(int index, out int x, out int y, out int z)
        {
            if (index < 0 || index >= Cells.Length) throw new ArgumentOutOfRangeException("index");
            x = index % Width; int q = index / Width; y = q % Height; z = q / Height;
        }
        public float Temperature(int index) { return MaterialCatalog.Temperature(Cells[index]); }

        /// <summary>
        /// Initial/editor placement helper, NOT a physical reaction. Runtime changes should use WorldSimulator.Cast.
        /// Replacing matter never creates stored mana; overflow is released into local space.
        /// </summary>
        public void SetMaterial(int x, int y, int z, MaterialId material)
        {
            int i = Index(x, y, z);
            Cell old = Cells[i]; var d = MaterialCatalog.Get(material);
            Cell c = new Cell { Material = material, Mass = d.DefaultMass,
                Energy = MaterialCatalog.EnergyAtTemperature(material, d.DefaultMass, 20f),
                AmbientMana = old.AmbientMana, StoredMana = old.StoredMana,
                Fuel = material == MaterialId.Wood ? d.DefaultMass : 0f };
            float overflow = Math.Max(0f, c.StoredMana - MaterialCatalog.StorageCapacity(c));
            c.StoredMana -= overflow; c.AmbientMana += overflow;
            Cells[i] = c; MarkDirty(i);
        }

        public void MarkDirty(int index)
        {
            if (index < 0 || index >= Cells.Length) return;
            Dirty.Add(index);
            int x, y, z; Coordinates(index, out x, out y, out z);
            if (x > 0) Dirty.Add(index - 1);
            if (x + 1 < Width) Dirty.Add(index + 1);
            if (y > 0) Dirty.Add(index - Width);
            if (y + 1 < Height) Dirty.Add(index + Width);
            if (z > 0) Dirty.Add(index - Width * Height);
            if (z + 1 < Depth) Dirty.Add(index + Width * Height);
        }

        public double TotalMana()
        {
            double sum = CasterMana;
            for (int i = 0; i < Cells.Length; i++) sum += Cells[i].AmbientMana + (double)Cells[i].StoredMana;
            return sum;
        }
        public double TotalEnergy()
        {
            double sum = 0;
            for (int i = 0; i < Cells.Length; i++) sum += Cells[i].Energy;
            return sum;
        }
        public double TotalMass()
        {
            double sum = 0;
            for (int i = 0; i < Cells.Length; i++) sum += Cells[i].Mass;
            return sum;
        }
    }
}
