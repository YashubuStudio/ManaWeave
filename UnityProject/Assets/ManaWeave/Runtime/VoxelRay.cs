using System;
using ManaWeave.Core;
using UnityEngine;

namespace ManaWeave.Runtime
{
    public struct VoxelHit
    {
        public bool HasHit;
        public int X, Y, Z;
        public int AdjacentX, AdjacentY, AdjacentZ;
        public Vector3 Point;
        public float Distance;
    }

    /// <summary>Grid traversal reads authoritative cell data; it never waits for a mesh collider.</summary>
    public static class VoxelRay
    {
        public static VoxelHit Trace(WorldState world, Ray ray, float maxDistance)
        {
            var result = new VoxelHit();
            Vector3 direction = ray.direction.normalized;
            if (direction.sqrMagnitude < 0.1f || maxDistance <= 0) return result;
            float enter = 0, exit = maxDistance;
            if (!Clip(ray.origin.x, direction.x, world.Width, ref enter, ref exit) ||
                !Clip(ray.origin.y, direction.y, world.Height, ref enter, ref exit) ||
                !Clip(ray.origin.z, direction.z, world.Depth, ref enter, ref exit)) return result;
            float distance = Mathf.Max(0, enter) + 0.0001f;
            if (distance > exit) return result;
            Vector3 position = ray.origin + direction * distance;
            int x = Mathf.Clamp(Mathf.FloorToInt(position.x), 0, world.Width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(position.y), 0, world.Height - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt(position.z), 0, world.Depth - 1);
            int px = x, py = y, pz = z;
            int sx = direction.x >= 0 ? 1 : -1;
            int sy = direction.y >= 0 ? 1 : -1;
            int sz = direction.z >= 0 ? 1 : -1;
            float dx = Mathf.Abs(direction.x) < 0.000001f ? float.PositiveInfinity : Mathf.Abs(1 / direction.x);
            float dy = Mathf.Abs(direction.y) < 0.000001f ? float.PositiveInfinity : Mathf.Abs(1 / direction.y);
            float dz = Mathf.Abs(direction.z) < 0.000001f ? float.PositiveInfinity : Mathf.Abs(1 / direction.z);
            float tx = Mathf.Abs(direction.x) < 0.000001f ? float.PositiveInfinity : distance + ((sx > 0 ? x + 1 : x) - position.x) / direction.x;
            float ty = Mathf.Abs(direction.y) < 0.000001f ? float.PositiveInfinity : distance + ((sy > 0 ? y + 1 : y) - position.y) / direction.y;
            float tz = Mathf.Abs(direction.z) < 0.000001f ? float.PositiveInfinity : distance + ((sz > 0 ? z + 1 : z) - position.z) / direction.z;

            // Each traversal crosses a cell plane; a bounded world therefore has a strict bound.
            int budget = world.Width + world.Height + world.Depth + 6;
            for (int i = 0; i < budget && world.InBounds(x, y, z) && distance <= exit; i++)
            {
                MaterialId material = world.Cells[world.Index(x, y, z)].Material;
                if (material != MaterialId.Air && material != MaterialId.Steam)
                {
                    result.HasHit = true;
                    result.X = x; result.Y = y; result.Z = z;
                    result.AdjacentX = px; result.AdjacentY = py; result.AdjacentZ = pz;
                    result.Distance = distance;
                    result.Point = ray.origin + direction * distance;
                    return result;
                }
                px = x; py = y; pz = z;
                if (tx <= ty && tx <= tz) { distance = tx; tx += dx; x += sx; }
                else if (ty <= tz) { distance = ty; ty += dy; y += sy; }
                else { distance = tz; tz += dz; z += sz; }
            }
            return result;
        }

        private static bool Clip(float origin, float direction, float extent, ref float enter, ref float exit)
        {
            if (Mathf.Abs(direction) < 0.000001f) return origin >= 0 && origin < extent;
            float a = -origin / direction;
            float b = (extent - origin) / direction;
            if (a > b) { float t = a; a = b; b = t; }
            enter = Mathf.Max(enter, a);
            exit = Mathf.Min(exit, b);
            return exit >= enter;
        }
    }
}
