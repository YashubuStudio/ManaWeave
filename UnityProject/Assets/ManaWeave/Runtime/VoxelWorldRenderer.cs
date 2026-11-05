using System;
using System.Collections.Generic;
using ManaWeave.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ManaWeave.Runtime
{
    public enum ObservationMode { Material, Mana, Temperature }

    /// <summary>Original exposed-face mesher. Thermal changes recolor existing vertices.</summary>
    public sealed class VoxelWorldRenderer : MonoBehaviour
    {
        private const int ChunkSize = 12;
        private sealed class Chunk
        {
            public int X, Y, Z;
            public Mesh Mesh;
            public GameObject Object;
            public int[] VertexCells = new int[0];
            public Color32[] Colors = new Color32[0];
            public bool Rebuild, Recolor;
        }

        private WorldState _world;
        private MaterialId[] _materials;
        private Chunk[] _chunks;
        private int _nx, _ny, _nz;
        private int _roundRobin;
        private Material _surface, _aura;
        private Mesh _auraMesh;
        private float _colorTimer, _auraTimer, _overviewTimer;
        private ObservationMode _mode;
        public Texture2D Overview { get; private set; }
        public int VisibleFaces { get; private set; }
        public int PendingChunks { get; private set; }
        public ObservationMode Mode { get { return _mode; } }

        private static readonly Vector3Int[] Neighbors = {
            new Vector3Int(1,0,0), new Vector3Int(-1,0,0),
            new Vector3Int(0,1,0), new Vector3Int(0,-1,0),
            new Vector3Int(0,0,1), new Vector3Int(0,0,-1)
        };
        private static readonly Vector3[,] Corners = {
            {new Vector3(1,0,0),new Vector3(1,1,0),new Vector3(1,1,1),new Vector3(1,0,1)},
            {new Vector3(0,0,1),new Vector3(0,1,1),new Vector3(0,1,0),new Vector3(0,0,0)},
            {new Vector3(0,1,1),new Vector3(1,1,1),new Vector3(1,1,0),new Vector3(0,1,0)},
            {new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(1,0,1),new Vector3(0,0,1)},
            {new Vector3(1,0,1),new Vector3(1,1,1),new Vector3(0,1,1),new Vector3(0,0,1)},
            {new Vector3(0,0,0),new Vector3(0,1,0),new Vector3(1,1,0),new Vector3(1,0,0)}
        };

        public void Attach(WorldState world)
        {
            ReleaseGeometry();
            _world = world;
            _materials = new MaterialId[world.Cells.Length];
            for (int i = 0; i < _materials.Length; i++) _materials[i] = world.Cells[i].Material;
            _nx = (world.Width + ChunkSize - 1) / ChunkSize;
            _ny = (world.Height + ChunkSize - 1) / ChunkSize;
            _nz = (world.Depth + ChunkSize - 1) / ChunkSize;
            _chunks = new Chunk[_nx * _ny * _nz];
            Shader surfaceShader = Resources.Load<Shader>("ManaWeave/Voxel");
            if (surfaceShader == null) surfaceShader = Shader.Find("ManaWeave/Voxel");
            if (surfaceShader == null) throw new InvalidOperationException("地形描画用のシェーダー（ManaWeave/Voxel）が見つかりません。");
            _surface = new Material(surfaceShader) { name = "ManaWeaveの地形表面" };
            Shader auraShader = Resources.Load<Shader>("ManaWeave/Aura");
            if (auraShader == null) throw new InvalidOperationException("魔力描画用のシェーダー（ManaWeave/Aura）が見つかりません。");
            _aura = new Material(auraShader) { name = "ManaWeaveの魔力表示" };
            for (int z = 0; z < _nz; z++)
            for (int y = 0; y < _ny; y++)
            for (int x = 0; x < _nx; x++)
            {
                var obj = new GameObject("地形区画 " + x + ":" + y + ":" + z);
                obj.transform.SetParent(transform, false);
                var mesh = new Mesh { name = obj.name, indexFormat = IndexFormat.UInt32 };
                mesh.MarkDynamic();
                obj.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _surface;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                var chunk = new Chunk { X = x, Y = y, Z = z, Object = obj, Mesh = mesh };
                _chunks[ChunkIndex(x, y, z)] = chunk;
                Rebuild(chunk);
            }
            var auraObject = new GameObject("空間魔力と観測した現象");
            auraObject.transform.SetParent(transform, false);
            _auraMesh = new Mesh { name = "ManaWeaveの現象表示", indexFormat = IndexFormat.UInt32 };
            _auraMesh.MarkDynamic();
            auraObject.AddComponent<MeshFilter>().sharedMesh = _auraMesh;
            auraObject.AddComponent<MeshRenderer>().sharedMaterial = _aura;
            Overview = new Texture2D(world.Width, world.Depth, TextureFormat.RGBA32, false);
            Overview.name = "ManaWeaveの観測地図";
            Overview.filterMode = FilterMode.Point;
            Overview.wrapMode = TextureWrapMode.Clamp;
            world.Dirty.Clear();
            UpdateOverview();
            RefreshAuras();
        }

        public void SetMode(ObservationMode mode)
        {
            _mode = mode;
            if (_surface != null) _surface.SetFloat("_Survey", mode == ObservationMode.Material ? 0 : 1);
            if (_chunks == null) return;
            foreach (Chunk chunk in _chunks) chunk.Recolor = true;
            _auraTimer = 1;
            _overviewTimer = 1;
        }

        public void Synchronize(float elapsed)
        {
            if (_world == null) return;
            foreach (int index in _world.Dirty)
            {
                if (index < 0 || index >= _materials.Length) continue;
                int x, y, z;
                _world.Coordinates(index, out x, out y, out z);
                bool topology = _materials[index] != _world.Cells[index].Material;
                _materials[index] = _world.Cells[index].Material;
                MarkChunk(x, y, z, topology);
                if (topology)
                {
                    for (int n = 0; n < Neighbors.Length; n++)
                    {
                        Vector3Int offset = Neighbors[n];
                        MarkChunk(x + offset.x, y + offset.y, z + offset.z, true);
                    }
                }
            }
            _world.Dirty.Clear();
            _colorTimer += elapsed;
            _auraTimer += elapsed;
            _overviewTimer += elapsed;
            int rebuildBudget = 3, colorBudget = _colorTimer >= 0.12f ? 6 : 0;
            if (colorBudget > 0) _colorTimer = 0;
            // Round robin prevents a continuously burning chunk from starving another chunk.
            int start = _roundRobin;
            for (int i = 0; i < _chunks.Length; i++)
            {
                Chunk chunk = _chunks[(start + i) % _chunks.Length];
                if (chunk.Rebuild && rebuildBudget > 0) { Rebuild(chunk); rebuildBudget--; }
                else if (chunk.Recolor && colorBudget > 0 && !chunk.Rebuild) { Recolor(chunk); colorBudget--; }
            }
            _roundRobin = (_roundRobin + 3) % _chunks.Length;
            PendingChunks = 0;
            VisibleFaces = 0;
            foreach (Chunk chunk in _chunks)
            {
                if (chunk.Rebuild || chunk.Recolor) PendingChunks++;
                VisibleFaces += chunk.VertexCells.Length / 4;
            }
            if (_auraTimer >= 0.3f) { _auraTimer = 0; RefreshAuras(); }
            if (_overviewTimer >= 0.7f) { _overviewTimer = 0; UpdateOverview(); }
        }

        private void MarkChunk(int x, int y, int z, bool rebuild)
        {
            if (!_world.InBounds(x, y, z)) return;
            Chunk c = _chunks[ChunkIndex(x / ChunkSize, y / ChunkSize, z / ChunkSize)];
            c.Recolor = true;
            if (rebuild) c.Rebuild = true;
        }

        private int ChunkIndex(int x, int y, int z) { return x + _nx * (y + _ny * z); }
        private static bool Visible(MaterialId m) { return m != MaterialId.Air && m != MaterialId.Steam; }

        private void Rebuild(Chunk chunk)
        {
            var vertices = new List<Vector3>(2048);
            var normals = new List<Vector3>(2048);
            var triangles = new List<int>(3072);
            var cells = new List<int>(2048);
            for (int z = chunk.Z * ChunkSize; z < Math.Min((chunk.Z + 1) * ChunkSize, _world.Depth); z++)
            for (int y = chunk.Y * ChunkSize; y < Math.Min((chunk.Y + 1) * ChunkSize, _world.Height); y++)
            for (int x = chunk.X * ChunkSize; x < Math.Min((chunk.X + 1) * ChunkSize, _world.Width); x++)
            {
                int index = _world.Index(x, y, z);
                if (!Visible(_world.Cells[index].Material)) continue;
                Vector3 origin = new Vector3(x, y, z);
                for (int f = 0; f < 6; f++)
                {
                    Vector3Int n = Neighbors[f];
                    int ax = x + n.x, ay = y + n.y, az = z + n.z;
                    if (_world.InBounds(ax, ay, az) && Visible(_world.Cells[_world.Index(ax, ay, az)].Material)) continue;
                    int first = vertices.Count;
                    for (int v = 0; v < 4; v++)
                    {
                        vertices.Add(origin + Corners[f, v]);
                        normals.Add(new Vector3(n.x, n.y, n.z));
                        cells.Add(index);
                    }
                    triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
                    triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
                }
            }
            chunk.Mesh.Clear();
            chunk.Mesh.SetVertices(vertices);
            chunk.Mesh.SetNormals(normals);
            chunk.Mesh.SetTriangles(triangles, 0);
            chunk.VertexCells = cells.ToArray();
            chunk.Colors = new Color32[cells.Count];
            Recolor(chunk);
            chunk.Mesh.RecalculateBounds();
            chunk.Rebuild = false;
        }

        private void Recolor(Chunk chunk)
        {
            int previous = -1;
            Color32 color = Color.white;
            for (int i = 0; i < chunk.VertexCells.Length; i++)
            {
                int index = chunk.VertexCells[i];
                if (index != previous) { color = CellColor(index); previous = index; }
                chunk.Colors[i] = color;
            }
            chunk.Mesh.colors32 = chunk.Colors;
            chunk.Recolor = false;
        }

        private Color CellColor(int index)
        {
            Cell cell = _world.Cells[index];
            var material = MaterialCatalog.Get(cell.Material);
            float temperature = _world.Temperature(index);
            Color color;
            if (_mode == ObservationMode.Temperature)
            {
                if (temperature < 20)
                    color = Color.Lerp(new Color(0.15f,0.26f,0.75f), new Color(0.10f,0.78f,0.88f), Mathf.InverseLerp(-50,20,temperature));
                else if (temperature < 280)
                    color = Color.Lerp(new Color(0.10f,0.78f,0.88f), new Color(1f,0.73f,0.20f), Mathf.InverseLerp(20,280,temperature));
                else
                    color = Color.Lerp(new Color(1f,0.73f,0.20f), new Color(1f,0.12f,0.25f), Mathf.InverseLerp(280,1000,temperature));
            }
            else if (_mode == ObservationMode.Mana)
            {
                float ambient = Mathf.Clamp01(cell.AmbientMana / 25f);
                float stored = cell.StoredMana / Mathf.Max(0.001f, material.ManaCapacity * cell.Mass);
                color = Color.Lerp(new Color(0.12f,0.14f,0.30f), new Color(0.18f,0.90f,0.84f), ambient);
                color = Color.Lerp(color, new Color(0.82f,0.40f,1f), Mathf.Clamp01(stored));
            }
            else
            {
                color = new Color(material.ColorR, material.ColorG, material.ColorB);
                float variation = (((index * 37) % 17) - 8) * 0.004f;
                color *= 1 + variation;
                if (temperature > 100)
                    color = Color.Lerp(color, new Color(1f,0.30f,0.08f), Mathf.Clamp01((temperature - 100) / 1200));
                else if (temperature < -5)
                    color = Color.Lerp(color, new Color(0.57f,0.85f,0.94f), Mathf.Clamp01((-temperature - 5) / 160));
            }
            color.a = cell.Material == MaterialId.Crystal ? 0.8f : (_mode == ObservationMode.Material ? 0.05f : 0.45f);
            return color;
        }

        private void UpdateOverview()
        {
            var pixels = new Color32[_world.Width * _world.Depth];
            for (int z = 0; z < _world.Depth; z++)
            for (int x = 0; x < _world.Width; x++)
            {
                Color c = new Color(0.05f,0.12f,0.17f);
                for (int y = _world.Height - 1; y >= 0; y--)
                {
                    int index = _world.Index(x,y,z);
                    if (!Visible(_world.Cells[index].Material)) continue;
                    c = CellColor(index);
                    c *= 0.66f + 0.5f * y / _world.Height;
                    c.a = 1;
                    break;
                }
                pixels[x + z * _world.Width] = c;
            }
            Overview.SetPixels32(pixels);
            Overview.Apply(false, false);
        }

        private void RefreshAuras()
        {
            if (_auraMesh == null) return;
            var vertices = new List<Vector3>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            int count = 0;
            for (int z = 0; z < _world.Depth && count < 2200; z++)
            for (int y = 1; y < _world.Height && count < 2200; y++)
            for (int x = 0; x < _world.Width && count < 2200; x++)
            {
                int index = _world.Index(x,y,z);
                Cell cell = _world.Cells[index];
                Vector3 p = new Vector3(x + 0.5f,y + 0.55f,z + 0.5f);
                if (_mode == ObservationMode.Mana && x % 4 == 0 && y % 4 == 0 && z % 4 == 0 && !Visible(cell.Material))
                {
                    float density = Mathf.Clamp01(cell.AmbientMana / 25f);
                    AddCross(vertices,colors,triangles,p,0.055f + density*0.15f,0.15f,new Color(0.35f,0.78f,1f,0.10f+density*0.65f));
                    count++;
                }
                else if (cell.Material == MaterialId.Steam)
                {
                    AddCross(vertices,colors,triangles,p,0.31f,0.46f,new Color(0.61f,0.85f,0.96f,0.16f));
                    count++;
                }
                else if (cell.Material == MaterialId.Wood && _world.Temperature(index) > 250 && cell.Fuel > 0)
                {
                    float motion = 0.12f * Mathf.Sin(Time.time * 4 + index);
                    AddCross(vertices,colors,triangles,p+Vector3.up*0.55f,0.22f,0.65f+motion,new Color(1f,0.40f,0.04f,0.65f));
                    count++;
                }
                else if (cell.Material == MaterialId.Crystal && (y == _world.Height - 1 || !Visible(_world.Cells[_world.Index(x,y+1,z)].Material)))
                {
                    float fill = Mathf.Clamp01(cell.StoredMana / Mathf.Max(1,cell.Mass*MaterialCatalog.Get(cell.Material).ManaCapacity));
                    AddCross(vertices,colors,triangles,p+Vector3.up*0.55f,0.08f+fill*0.1f,0.16f,new Color(0.75f,0.43f,1f,0.25f+fill*0.4f));
                    count++;
                }
            }
            _auraMesh.Clear();
            _auraMesh.SetVertices(vertices);
            _auraMesh.SetColors(colors);
            _auraMesh.SetTriangles(triangles,0);
            _auraMesh.RecalculateBounds();
        }

        private static void AddCross(List<Vector3> vertices, List<Color> colors, List<int> triangles, Vector3 center, float width, float height, Color color)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                Vector3 right = (axis == 0 ? Vector3.right : Vector3.forward) * width;
                Vector3 up = Vector3.up * height;
                int start = vertices.Count;
                vertices.Add(center-right); vertices.Add(center+up); vertices.Add(center+right); vertices.Add(center-up*0.35f);
                colors.Add(color); colors.Add(new Color(color.r,color.g,color.b,0)); colors.Add(color); colors.Add(new Color(color.r,color.g,color.b,0));
                triangles.Add(start); triangles.Add(start+1); triangles.Add(start+2);
                triangles.Add(start); triangles.Add(start+2); triangles.Add(start+3);
            }
        }

        private void ReleaseGeometry()
        {
            if (_chunks != null)
            {
                foreach (Chunk chunk in _chunks)
                {
                    if (chunk.Mesh != null) Destroy(chunk.Mesh);
                    if (chunk.Object != null) Destroy(chunk.Object);
                }
            }
            if (_auraMesh != null) Destroy(_auraMesh);
            Transform oldAura = transform.Find("空間魔力と観測した現象");
            if (oldAura != null) Destroy(oldAura.gameObject);
            if (_surface != null) Destroy(_surface);
            if (_aura != null) Destroy(_aura);
            if (Overview != null) Destroy(Overview);
        }

        private void OnDestroy() { ReleaseGeometry(); }
    }
}
