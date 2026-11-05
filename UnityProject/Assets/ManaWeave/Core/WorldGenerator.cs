using System;

namespace ManaWeave.Core
{
    public static class WorldGenerator
    {
        public static WorldState Generate(int width = 48, int height = 24, int depth = 48, int seed = 731)
        {
            if (width < 12 || height < 12 || depth < 12)
                throw new ArgumentOutOfRangeException("dimensions", "世界の生成には、各方向に12セル以上の大きさが必要です。");
            var w = new WorldState(width, height, depth, seed);
            for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                double ridge = 1.6 * Math.Sin(x * .17 + seed * .01) + 1.35 * Math.Cos(z * .19)
                    + 1.2 * Math.Sin((x + z) * .10);
                int surface = Math.Max(2, Math.Min(height - 6, 7 + (int)Math.Round(ridge)));
                int lakeX = width / 2, lakeZ = depth / 2 + depth / 8;
                double dx = (x - lakeX) / 5.5, dz = (z - lakeZ) / 4.0;
                bool lake = dx * dx + dz * dz < 1.0;
                if (lake) surface = 3;
                for (int y = 0; y <= surface; y++)
                {
                    MaterialId id = y == surface ? MaterialId.Soil : MaterialId.Stone;
                    uint noise = Hash(x, y, z, seed);
                    if (y > 1 && y < surface && noise % 101 < 6) id = MaterialId.Iron;
                    if (y > 2 && y < surface && noise % 271 == 0) id = MaterialId.Crystal;
                    w.SetMaterial(x, y, z, id);
                }
                if (lake)
                {
                    for (int y = surface + 1; y <= Math.Min(6, height - 3); y++)
                        w.SetMaterial(x, y, z, MaterialId.Water);
                }
                else if (x > 3 && z > 3 && x < width - 4 && z < depth - 4 && Hash(x, 0, z, seed) % 127 == 0)
                {
                    for (int j = 1; j <= 3; j++) w.SetMaterial(x, surface + j, z, MaterialId.Wood);
                    // A deliberately simple voxel canopy: it is wood and therefore combustible.
                    for (int xx = -1; xx <= 1; xx++)
                    for (int zz = -1; zz <= 1; zz++)
                        w.SetMaterial(x + xx, surface + 3, z + zz, MaterialId.Wood);
                    w.SetMaterial(x, surface + 4, z, MaterialId.Wood);
                }
                for (int y = 0; y < height; y++)
                {
                    int i = w.Index(x, y, z); Cell c = w.Cells[i];
                    c.AmbientMana = 12f + 4f * (float)(.5 + .5 * Math.Sin(x * .11 + z * .09 + seed * .003));
                    w.Cells[i] = c;
                }
            }

            // Exposed material samples and violet crystal spires make the experimental materials discoverable.
            for (int n = 0; n < 8; n++)
            {
                int x = 2 + (int)(Hash(n, 3, 11, seed) % (uint)(width - 4));
                int z = 2 + (int)(Hash(n, 5, 17, seed) % (uint)(depth - 4));
                int y = Surface(w, x, z) + 1;
                for (int h = 0; h < 1 + n % 3 && y + h < height; h++)
                    w.SetMaterial(x, y + h, z, n % 2 == 0 ? MaterialId.Crystal : MaterialId.Iron);
            }
            for (int id = 0; id < 4; id++) PrepareLab(w, id);
            w.CasterX = width / 2; w.CasterZ = depth / 2;
            w.CasterY = Math.Min(height - 2, Surface(w, w.CasterX, w.CasterZ) + 4);
            return w;
        }

        public static int Surface(WorldState world, int x, int z)
        {
            for (int y = world.Height - 1; y >= 0; y--)
                if (world.Cells[world.Index(x, y, z)].Material != MaterialId.Air &&
                    world.Cells[world.Index(x, y, z)].Material != MaterialId.Steam) return y;
            return 0;
        }

        public static void ExperimentLocation(WorldState w, int id, out int x, out int y, out int z)
        {
            if (id < 0 || id > 3) throw new ArgumentOutOfRangeException("id");
            x = id % 2 == 0 ? w.Width / 4 : 3 * w.Width / 4;
            z = id < 2 ? w.Depth / 4 : 3 * w.Depth / 4;
            y = Math.Min(6, w.Height - 6) + 1;
            if (id == 3) y += 2; // The direct-field experiment sits above the empty laboratory floor.
        }

        public static void PrepareLab(WorldState w, int id)
        {
            int cx, cy, cz; ExperimentLocation(w, id, out cx, out cy, out cz);
            int floor = Math.Min(6, w.Height - 6);
            for (int dx = -3; dx <= 3; dx++)
            for (int dz = -3; dz <= 3; dz++)
            {
                int x = cx + dx, z = cz + dz;
                if (!w.InBounds(x, floor, z)) continue;
                for (int y = floor + 1; y < w.Height; y++) w.SetMaterial(x, y, z, MaterialId.Air);
                for (int y = Math.Max(0, floor - 1); y <= floor; y++)
                    w.SetMaterial(x, y, z, MaterialId.Stone);
                if (Math.Abs(dx) == 3 && Math.Abs(dz) == 3)
                    w.SetMaterial(x, floor + 1, z, MaterialId.Crystal);
            }
            if (id == 0) w.SetMaterial(cx, floor + 1, cz, MaterialId.Wood);
            if (id == 1)
                for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    if (w.InBounds(cx + dx, floor + 1, cz + dz))
                        w.SetMaterial(cx + dx, floor + 1, cz + dz, MaterialId.Stone);
            if (id == 2)
            {
                w.SetMaterial(cx, floor + 1, cz, MaterialId.Iron);
                if (w.InBounds(cx - 2, floor + 1, cz)) w.SetMaterial(cx - 2, floor + 1, cz, MaterialId.Crystal);
                if (w.InBounds(cx + 2, floor + 1, cz)) w.SetMaterial(cx + 2, floor + 1, cz, MaterialId.Crystal);
            }
        }

        static uint Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)seed ^ ((uint)x * 374761393u) ^ ((uint)y * 668265263u) ^ ((uint)z * 2246822519u);
                h = (h ^ (h >> 13)) * 1274126177u;
                return h ^ (h >> 16);
            }
        }
    }
}
