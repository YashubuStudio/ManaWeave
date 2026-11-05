using System;
using System.Collections.Generic;
using System.Globalization;

namespace ManaWeave.Core
{
    /// <summary>
    /// Original coarse voxel world-law solver. No Unity API, neural network or nondeterministic random state.
    /// Only this class applies live world-changing spells. Each cast validates its full resource budget first.
    /// </summary>
    public sealed class WorldSimulator
    {
        public WorldState World { get; private set; }
        public SimulationConfig Config { get; private set; }
        public SimulationStats Stats { get; private set; }
        public int LastExperimentCenterX { get; private set; }
        public int LastExperimentCenterY { get; private set; }
        public int LastExperimentCenterZ { get; private set; }

        readonly double[] _heatDelta, _manaDelta;
        readonly float[] _temperature, _deviation;
        readonly bool[] _visited, _moved;
        readonly int[] _queue, _visualSignature;
        readonly List<int> _component = new List<int>(256);
        const float BurnEnergyPerKg = 13000000f;

        public WorldSimulator(WorldState world, SimulationConfig config = null)
        {
            if (world == null) throw new ArgumentNullException("world");
            World = world; Config = config ?? new SimulationConfig(); Config.Validate();
            Stats = new SimulationStats(); int n = world.Cells.Length;
            _heatDelta = new double[n]; _manaDelta = new double[n];
            _temperature = new float[n]; _deviation = new float[n];
            _visited = new bool[n]; _moved = new bool[n]; _queue = new int[n]; _visualSignature = new int[n];
            for (int i = 0; i < n; i++) _visualSignature[i] = VisualSignature(i);
        }

        public void Step(float dt)
        {
            if (!MaterialCatalog.Finite(dt) || dt <= 0f || dt > 1f)
                throw new ArgumentOutOfRangeException("dt", "A tick must be in (0, 1] seconds.");
            Config.Validate();
            // Substeps bound all fluxes; continuous-time sources are multiplied by the actual substep.
            int count = Math.Max(1, (int)Math.Ceiling(dt / .05f));
            float step = dt / count;
            for (int s = 0; s < count; s++)
            {
                GenerateMana(step);
                CalculateOrdinaryFluxes(step);
                ApplyNaturalInteraction(step); // direct-field path executes WITHOUT requiring absorption.
                AbsorbMana(step);
                if (Config.EnableCombustion) BurnFuel(step);
                if (Config.EnablePhaseChanges) ChangePhases();
                if (Config.EnableFluidFlow) FlowMatter();
                AbsorbCasterMana(step);
                World.Tick++; World.SimulatedSeconds += step;
            }
            for (int i = 0; i < World.Cells.Length; i++)
            {
                int sig = VisualSignature(i);
                if (sig != _visualSignature[i]) { _visualSignature[i] = sig; World.MarkDirty(i); }
            }
        }

        void GenerateMana(float dt)
        {
            if (Config.ManaGeneration == 0f) return;
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i];
                float amount = Config.ManaGeneration * dt * Math.Max(0f, 1f - c.AmbientMana / Config.AmbientSaturation);
                float old = c.AmbientMana; c.AmbientMana += amount; World.Cells[i] = c;
                Stats.GeneratedMana += c.AmbientMana - old;
            }
        }

        void CalculateOrdinaryFluxes(float dt)
        {
            Array.Clear(_heatDelta, 0, _heatDelta.Length); Array.Clear(_manaDelta, 0, _manaDelta.Length);
            for (int i = 0; i < World.Cells.Length; i++) _temperature[i] = World.Temperature(i);
            int width = World.Width, height = World.Height, layer = width * height;
            for (int z = 0; z < World.Depth; z++)
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = x + width * (y + height * z);
                if (x + 1 < width) FluxPair(i, i + 1, dt);
                if (y + 1 < height) FluxPair(i, i + width, dt);
                if (z + 1 < World.Depth) FluxPair(i, i + layer, dt);
            }
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i];
                c.Energy = (float)Math.Max(0.0, c.Energy + _heatDelta[i]);
                c.AmbientMana = (float)Math.Max(0.0, c.AmbientMana + _manaDelta[i]);
                World.Cells[i] = c;
            }
        }

        void FluxPair(int a, int b, float dt)
        {
            Cell ca = World.Cells[a], cb = World.Cells[b];
            if (Config.ManaDiffusion > 0f)
            {
                double q = (cb.AmbientMana - (double)ca.AmbientMana) * Math.Min(.10, Config.ManaDiffusion * dt);
                _manaDelta[a] += q; _manaDelta[b] -= q;
            }
            if (Config.ThermalDiffusion == 0f) return;
            MaterialDefinition da = MaterialCatalog.Get(ca.Material), db = MaterialCatalog.Get(cb.Material);
            double capa = Math.Max(.000001, da.HeatCapacity * ca.Mass), capb = Math.Max(.000001, db.HeatCapacity * cb.Mass);
            double harmonic = 2.0 * da.Conductivity * db.Conductivity / (da.Conductivity + db.Conductivity);
            double fraction = Math.Min(.10, Config.ThermalDiffusion * harmonic * dt);
            double qheat = (_temperature[b] - (double)_temperature[a]) / (1.0 / capa + 1.0 / capb) * fraction;
            // Six bounded edge transfers can never remove more than the available energy.
            qheat = Math.Max(-ca.Energy / 6.0, Math.Min(cb.Energy / 6.0, qheat));
            _heatDelta[a] += qheat; _heatDelta[b] -= qheat;
        }

        void ApplyNaturalInteraction(float dt)
        {
            Stats.ActivePhenomena = 0;
            if (Config.NaturalCoupling == 0f) return;
            Array.Clear(_visited, 0, _visited.Length);
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i]; float temperature = MaterialCatalog.Temperature(c);
                _deviation[i] = Math.Abs(temperature - MaterialCatalog.RoomTemperature) >= Config.DisturbanceThreshold
                    ? c.Energy - MaterialCatalog.RoomEnergy(c) : 0f;
            }
            // Components have equal disturbance polarity. A large connected event is not mistaken for small isolated cells.
            for (int start = 0; start < World.Cells.Length; start++)
            {
                if (_visited[start] || Math.Abs(_deviation[start]) < .01f) continue;
                _component.Clear(); int head = 0, tail = 0; _queue[tail++] = start; _visited[start] = true;
                bool positive = _deviation[start] > 0f; double scale = 0.0;
                while (head < tail)
                {
                    int i = _queue[head++]; _component.Add(i); scale += Math.Abs(_deviation[i]);
                    int x, y, z; World.Coordinates(i, out x, out y, out z);
                    if (x > 0) Enqueue(i - 1, positive, ref tail);
                    if (x + 1 < World.Width) Enqueue(i + 1, positive, ref tail);
                    if (y > 0) Enqueue(i - World.Width, positive, ref tail);
                    if (y + 1 < World.Height) Enqueue(i + World.Width, positive, ref tail);
                    int stride = World.Width * World.Height;
                    if (z > 0) Enqueue(i - stride, positive, ref tail);
                    if (z + 1 < World.Depth) Enqueue(i + stride, positive, ref tail);
                }
                if (scale < .01) continue;
                Stats.ActivePhenomena++;
                double response = Math.Tanh(Config.CoherenceSlope * Math.Log(scale / Config.CriticalEnergy));
                double rate = Math.Min(.25, Config.NaturalCoupling * dt);
                for (int j = 0; j < _component.Count; j++)
                {
                    int i = _component[j]; Cell c = World.Cells[i];
                    float storedAccess = Config.EnableStoredInteraction ? c.StoredMana * Math.Min(1f, .7f * dt) : 0f;
                    float ambientAccess = c.AmbientMana * Math.Min(1f, 8f * dt);
                    double density = c.AmbientMana + (Config.EnableStoredInteraction ? c.StoredMana * .20 : 0.0);
                    double requested = _deviation[i] * response * rate * density / (density + 5.0);
                    if (requested < 0) requested = Math.Max(-c.Energy, requested);
                    double availableEffect = (ambientAccess + storedAccess) * (double)Config.JoulesPerMana;
                    double effect = Math.Sign(requested) * Math.Min(Math.Abs(requested), availableEffect);
                    if (Math.Abs(effect) < .00001) continue;
                    float cost = (float)(Math.Abs(effect) / Config.JoulesPerMana);
                    float fromAmbient = Math.Min(ambientAccess, cost);
                    float fromStored = Math.Min(storedAccess, Math.Max(0f, cost - fromAmbient));
                    // Use exactly the resource amount committed, avoiding negative pools from rounding.
                    double actual = Math.Sign(effect) * (fromAmbient + (double)fromStored) * Config.JoulesPerMana;
                    float previousEnergy = c.Energy;
                    c.AmbientMana = Math.Max(0f, c.AmbientMana - fromAmbient);
                    c.StoredMana = Math.Max(0f, c.StoredMana - fromStored);
                    c.Energy = (float)Math.Max(0.0, c.Energy + actual); World.Cells[i] = c;
                    double committed = c.Energy - (double)previousEnergy;
                    Stats.ManifestedMana += fromAmbient + (double)fromStored;
                    Stats.DirectManifestedMana += fromAmbient; Stats.StoredManifestedMana += fromStored;
                    Stats.NaturalEnergyJoules += committed;
                    if (response < 0) Stats.StabilizedJoules += Math.Abs(committed);
                    else Stats.AmplifiedJoules += Math.Abs(committed);
                }
            }
        }

        void Enqueue(int index, bool positive, ref int tail)
        {
            if (_visited[index] || Math.Abs(_deviation[index]) < .01f || (_deviation[index] > 0f) != positive) return;
            _visited[index] = true; _queue[tail++] = index;
        }

        void AbsorbMana(float dt)
        {
            if (Config.AbsorptionScale == 0f) return;
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i]; MaterialDefinition d = MaterialCatalog.Get(c.Material);
                float cap = MaterialCatalog.StorageCapacity(c);
                if (cap <= 0f || c.StoredMana >= cap || c.AmbientMana <= 0f) continue;
                float desired = d.AbsorptionRate * Math.Max(.02f, c.Mass) * Config.AbsorptionScale * dt * (1f - c.StoredMana / cap);
                float q = Math.Min(c.AmbientMana, Math.Min(cap - c.StoredMana, desired));
                c.AmbientMana -= q; c.StoredMana += q; World.Cells[i] = c;
                Stats.AbsorbedMana += q;
            }
        }

        void AbsorbCasterMana(float dt)
        {
            float demand = Math.Min(World.CasterManaMax - World.CasterMana, Config.CasterAbsorptionRate * dt);
            if (demand <= 0f) return;
            int cx = Math.Max(0, Math.Min(World.Width - 1, World.CasterX));
            int cy = Math.Max(0, Math.Min(World.Height - 1, World.CasterY));
            int cz = Math.Max(0, Math.Min(World.Depth - 1, World.CasterZ));
            for (int dz = -1; dz <= 1 && demand > .000001f; dz++)
            for (int dy = -1; dy <= 1 && demand > .000001f; dy++)
            for (int dx = -1; dx <= 1 && demand > .000001f; dx++)
            {
                if (!World.InBounds(cx + dx, cy + dy, cz + dz)) continue;
                int i = World.Index(cx + dx, cy + dy, cz + dz); Cell c = World.Cells[i];
                float q = Math.Min(c.AmbientMana, demand);
                c.AmbientMana -= q; World.Cells[i] = c; World.CasterMana += q;
                Stats.AbsorbedMana += q; demand -= q;
            }
        }

        void BurnFuel(float dt)
        {
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i];
                if (c.Material != MaterialId.Wood || c.Fuel <= 0f || MaterialCatalog.Temperature(c) < 140f) continue;
                float rate = .004f * Math.Min(4f, Math.Max(.2f, (MaterialCatalog.Temperature(c) - 120f) / 180f));
                float used = Math.Min(c.Fuel, rate * dt);
                float before = c.Energy; c.Energy += used * BurnEnergyPerKg; c.Fuel -= used;
                Stats.CombustionJoules += c.Energy - before;
                if (c.Fuel <= .000001f)
                {
                    c.Material = MaterialId.Ash; c.Fuel = 0f; ReleaseOverflow(ref c); World.MarkDirty(i);
                    Stats.LastEvent = "Wood fuel exhausted; solid residue became ash.";
                }
                World.Cells[i] = c;
            }
        }

        void ChangePhases()
        {
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i]; if (!MaterialCatalog.IsWaterFamily(c.Material) || c.Mass <= 0f) continue;
                MaterialId previous = c.Material; float h = c.Energy / c.Mass;
                if (c.Material == MaterialId.Ice && h >= MaterialCatalog.WaterAtZero) c.Material = MaterialId.Water;
                if (c.Material == MaterialId.Water && h >= MaterialCatalog.SteamAtBoiling) c.Material = MaterialId.Steam;
                if (c.Material == MaterialId.Steam && h <= MaterialCatalog.WaterAtBoiling) c.Material = MaterialId.Water;
                if (c.Material == MaterialId.Water && h <= MaterialCatalog.IceAtZero) c.Material = MaterialId.Ice;
                if (previous != c.Material)
                {
                    ReleaseOverflow(ref c); World.Cells[i] = c; World.MarkDirty(i);
                    Stats.LastEvent = previous + " -> " + c.Material + " (enthalpy and mass retained).";
                }
            }
        }

        static void ReleaseOverflow(ref Cell c)
        {
            float q = Math.Max(0f, c.StoredMana - MaterialCatalog.StorageCapacity(c));
            c.StoredMana -= q; c.AmbientMana += q;
        }

        void FlowMatter()
        {
            Array.Clear(_moved, 0, _moved.Length);
            for (int z = 0; z < World.Depth; z++)
            for (int y = 0; y < World.Height; y++)
            for (int x = 0; x < World.Width; x++)
            {
                int i = World.Index(x, y, z); if (_moved[i]) continue;
                MaterialId m = World.Cells[i].Material;
                if (m != MaterialId.Water && m != MaterialId.Steam) continue;
                int vertical = m == MaterialId.Water ? -1 : 1;
                int target;
                if (TryAir(x, y + vertical, z, out target)) { SwapMatter(i, target); continue; }
                // Whole-voxel gravity transport, deliberately not a pressure/Navier-Stokes solver.
                if ((World.Tick & 3) != 0) continue;
                int direction = (int)((World.Tick / 4 + x + z) & 3);
                for (int k = 0; k < 4; k++)
                {
                    int d = (direction + k) & 3;
                    int nx = x + (d == 0 ? 1 : d == 2 ? -1 : 0);
                    int nz = z + (d == 1 ? 1 : d == 3 ? -1 : 0);
                    if (!TryAir(nx, y, nz, out target)) continue;
                    int below;
                    // Water only moves sideways if it can subsequently fall. Steam disperses on a blocked ceiling.
                    if (m == MaterialId.Water && !TryAir(nx, y - 1, nz, out below)) continue;
                    SwapMatter(i, target); break;
                }
            }
        }

        bool TryAir(int x, int y, int z, out int index)
        {
            index = -1; if (!World.InBounds(x, y, z)) return false;
            index = World.Index(x, y, z);
            return !_moved[index] && World.Cells[index].Material == MaterialId.Air;
        }

        void SwapMatter(int a, int b)
        {
            Cell ca = World.Cells[a], cb = World.Cells[b];
            float spaceA = ca.AmbientMana, spaceB = cb.AmbientMana;
            cb.AmbientMana = spaceA; ca.AmbientMana = spaceB;
            World.Cells[a] = cb; World.Cells[b] = ca; _moved[a] = true; _moved[b] = true;
            World.MarkDirty(a); World.MarkDirty(b);
        }

        List<int> Sphere(SpellCommand command)
        {
            var list = new List<int>(128); int r = command.Radius;
            for (int z = Math.Max(0, command.Z - r); z <= Math.Min(World.Depth - 1, command.Z + r); z++)
            for (int y = Math.Max(0, command.Y - r); y <= Math.Min(World.Height - 1, command.Y + r); y++)
            for (int x = Math.Max(0, command.X - r); x <= Math.Min(World.Width - 1, command.X + r); x++)
            {
                int dx = x - command.X, dy = y - command.Y, dz = z - command.Z;
                if (dx * dx + dy * dy + dz * dz <= r * r) list.Add(World.Index(x, y, z));
            }
            return list;
        }

        public SpellResult Cast(SpellCommand command)
        {
            if (command == null) return SpellResult.Fail("No spell supplied.");
            if (!Enum.IsDefined(typeof(SpellOperation), command.Operation)) return SpellResult.Fail("Unknown operation.");
            if (!World.InBounds(command.X, command.Y, command.Z)) return SpellResult.Fail("Target is outside the simulated world.");
            if (command.Radius < 1 || command.Radius > 5) return SpellResult.Fail("Radius must be 1..5 voxels.");
            if (!MaterialCatalog.Finite(command.Power) || command.Power < 0f) return SpellResult.Fail("Power must be a finite nonnegative number.");
            if (!MaterialCatalog.IsValid(command.Material)) return SpellResult.Fail("Unknown material.");
            Config.Validate();
            List<int> cells = Sphere(command);
            switch (command.Operation)
            {
                case SpellOperation.Heat: return CastThermal(command, cells, false);
                case SpellOperation.Cool: return CastThermal(command, cells, true);
                case SpellOperation.Charge: return CastCharge(command, cells);
                case SpellOperation.Transmute: return CastTransmute(cells);
                case SpellOperation.Excavate: return CastMatter(command, cells, false);
                case SpellOperation.Build: return CastMatter(command, cells, true);
                default: return SpellResult.Fail("Unsupported operation.");
            }
        }

        SpellResult CastThermal(SpellCommand command, List<int> cells, bool cooling)
        {
            if (command.Power <= 0f || command.Power > 8000000f) return SpellResult.Fail("Thermal energy must be in (0, 8,000,000] J.");
            double weight = 0;
            for (int j = 0; j < cells.Count; j++)
            {
                Cell c = World.Cells[cells[j]];
                weight += cooling ? c.Energy : c.Mass * (double)MaterialCatalog.Get(c.Material).HeatCapacity;
            }
            if (weight <= .000001) return SpellResult.Fail("Target contains no usable thermal state.");
            double total = cooling ? Math.Min(command.Power, weight) : command.Power;
            float cost = (float)(total / Config.JoulesPerMana + 2f + cells.Count * .08f);
            if (!CanPay(cost)) return SpellResult.Fail("Insufficient body mana: requires " + F(cost) + " MU.");
            Pay(cost); double actual = 0;
            for (int j = 0; j < cells.Count; j++)
            {
                int i = cells[j]; Cell c = World.Cells[i];
                double portion = cooling ? c.Energy : c.Mass * (double)MaterialCatalog.Get(c.Material).HeatCapacity;
                float old = c.Energy;
                c.Energy = (float)Math.Max(0.0, c.Energy + (cooling ? -1.0 : 1.0) * total * portion / weight);
                actual += c.Energy - (double)old; World.Cells[i] = c; World.MarkDirty(i);
            }
            Stats.SpellJoules += actual;
            return Success((cooling ? "Removed " : "Added ") + F((float)Math.Abs(actual)) + " J across " + cells.Count + " cells.", cost);
        }

        SpellResult CastCharge(SpellCommand command, List<int> cells)
        {
            if (command.Power <= 0f || command.Power > 10000f) return SpellResult.Fail("Charge must be in (0, 10,000] MU.");
            double capacity = 0;
            for (int j = cells.Count - 1; j >= 0; j--)
            {
                Cell c = World.Cells[cells[j]];
                if (c.Material == MaterialId.Air || c.Material == MaterialId.Steam) { cells.RemoveAt(j); continue; }
                float free = Math.Max(0f, MaterialCatalog.StorageCapacity(c) - c.StoredMana);
                if (free <= .000001f) cells.RemoveAt(j); else capacity += free;
            }
            if (capacity <= .000001) return SpellResult.Fail("No material with free mana storage is in range.");
            float amount = (float)Math.Min(command.Power, capacity), overhead = 2f, cost = amount + overhead;
            if (!CanPay(cost)) return SpellResult.Fail("Insufficient body mana: requires " + F(cost) + " MU.");
            World.CasterMana -= cost; Stats.ManifestedMana += overhead;
            double moved = 0;
            for (int j = 0; j < cells.Count; j++)
            {
                int i = cells[j]; Cell c = World.Cells[i];
                float room = Math.Max(0f, MaterialCatalog.StorageCapacity(c) - c.StoredMana);
                float q = (float)(amount * room / capacity);
                c.StoredMana += q; moved += q; World.Cells[i] = c; World.MarkDirty(i);
            }
            Stats.TransferredMana += moved;
            return Success("Transferred " + F((float)moved) + " MU into matter; 2 MU used for control.", cost);
        }

        SpellResult CastTransmute(List<int> cells)
        {
            for (int j = cells.Count - 1; j >= 0; j--)
                if (World.Cells[cells[j]].Material != MaterialId.Iron) cells.RemoveAt(j);
            if (cells.Count == 0) return SpellResult.Fail("Transmute requires iron in the target volume.");
            for (int j = 0; j < cells.Count; j++)
            {
                Cell c = World.Cells[cells[j]];
                if (MaterialCatalog.Temperature(c) < 250f || c.StoredMana < 6f)
                    return SpellResult.Fail("Each iron cell needs at least 250 C and 6 stored MU. Heat and charge it first.");
            }
            float cost = 10f * cells.Count;
            if (!CanPay(cost)) return SpellResult.Fail("Insufficient body mana for alchemical binding.");
            Pay(cost);
            for (int j = 0; j < cells.Count; j++)
            {
                int i = cells[j]; Cell c = World.Cells[i];
                c.StoredMana -= 6f; c.Material = MaterialId.Alloy;
                float old = c.Energy; c.Energy += 6f * Config.JoulesPerMana;
                Stats.ReactionJoules += c.Energy - old;
                Stats.ManifestedMana += 6f; Stats.StoredManifestedMana += 6f;
                World.Cells[i] = c; World.MarkDirty(i);
            }
            return Success("Bound " + cells.Count + " iron cells into arcane alloy; mass retained, 6 stored MU used per cell.", cost);
        }

        SpellResult CastMatter(SpellCommand command, List<int> cells, bool build)
        {
            if (build && (command.Material == MaterialId.Air || command.Material == MaterialId.Steam))
                return SpellResult.Fail("Select a tangible building material.");
            for (int j = cells.Count - 1; j >= 0; j--)
            {
                MaterialId id = World.Cells[cells[j]].Material;
                bool empty = id == MaterialId.Air || id == MaterialId.Steam;
                if (build ? !empty : empty) cells.RemoveAt(j);
            }
            if (cells.Count == 0) return SpellResult.Fail(build ? "No empty space in the building volume." : "No matter to excavate.");
            float cost = 2f + cells.Count * (build ? 5f : 3f);
            if (!CanPay(cost)) return SpellResult.Fail("Insufficient body mana for terrain editing.");
            Pay(cost);
            for (int j = 0; j < cells.Count; j++)
            {
                int i = cells[j]; Cell before = World.Cells[i]; int x, y, z; World.Coordinates(i, out x, out y, out z);
                World.SetMaterial(x, y, z, build ? command.Material : MaterialId.Air);
                Cell after = World.Cells[i];
                Stats.SandboxMassKg += after.Mass - (double)before.Mass;
                Stats.SandboxEnergyJoules += after.Energy - (double)before.Energy;
            }
            return Success((build ? "Placed " + command.Material + " in " : "Excavated ") + cells.Count +
                " cells (creative material reservoir recorded).", cost);
        }

        bool CanPay(float cost) { return MaterialCatalog.Finite(cost) && cost >= 0f && World.CasterMana + .000001f >= cost; }
        void Pay(float cost) { World.CasterMana = Math.Max(0f, World.CasterMana - cost); Stats.ManifestedMana += cost; }
        SpellResult Success(string message, float cost)
        {
            Stats.LastEvent = message;
            return new SpellResult { Success = true, Message = message, ManaCost = cost };
        }

        /// <summary>Reproducible laboratory fixtures; setup inputs are explicitly recorded as external sandbox sources.</summary>
        public void RunExperiment(int id)
        {
            if (id < 0 || id > 3) throw new ArgumentOutOfRangeException("id");
            double energy = World.TotalEnergy(), mass = World.TotalMass(), mana = World.TotalMana();
            WorldGenerator.PrepareLab(World, id);
            Stats.SandboxEnergyJoules += World.TotalEnergy() - energy;
            Stats.SandboxMassKg += World.TotalMass() - mass;
            Stats.ExperimentMana += World.TotalMana() - mana;
            int x, y, z; WorldGenerator.ExperimentLocation(World, id, out x, out y, out z);
            LastExperimentCenterX = x; LastExperimentCenterY = y; LastExperimentCenterZ = z;
            if (id == 0)
            {
                InjectHeat(World.Index(x, y, z), 50000f);
                Stats.LastEvent = "Small event: 50 kJ disturbance, below the 160 kJ default critical scale; ambient mana damps it.";
            }
            else if (id == 1)
            {
                for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    if (World.InBounds(x + dx, y, z + dz)) InjectHeat(World.Index(x + dx, y, z + dz), 120000f);
                Stats.LastEvent = "Large event: connected 3 MJ wood bed; ambient resonance amplifies it while mana remains available.";
            }
            else if (id == 2)
            {
                int i = World.Index(x, y, z); Cell c = World.Cells[i];
                float target = MaterialCatalog.EnergyAtTemperature(MaterialId.Iron, c.Mass, 320f);
                InjectHeat(i, Math.Max(0f, target - c.Energy)); c = World.Cells[i];
                float injected = Math.Max(0f, 12f - c.StoredMana); c.StoredMana += injected;
                Stats.ExperimentMana += injected; World.Cells[i] = c; World.MarkDirty(i);
                Stats.LastEvent = "Alchemy fixture: iron at 320 C, 12 stored MU. Aim at it and cast transmute to bind arcane alloy.";
            }
            else
            {
                int i = World.Index(x, y, z); Cell c = World.Cells[i];
                // No stored mana is needed. Releasing any residual stock retains the total mana inventory.
                c.AmbientMana += c.StoredMana; c.StoredMana = 0f; World.Cells[i] = c;
                InjectHeat(i, 60000f);
                Stats.LastEvent = "Direct-field fixture: 60 kJ in an air cell with zero stored mana. Ambient mana acts before absorption.";
            }
        }

        void InjectHeat(int i, float joules)
        {
            Cell c = World.Cells[i]; float before = c.Energy; c.Energy += joules; World.Cells[i] = c;
            Stats.SandboxEnergyJoules += c.Energy - before; World.MarkDirty(i);
        }

        int VisualSignature(int i)
        {
            Cell c = World.Cells[i];
            int heat = (int)Math.Max(-1000, Math.Min(10000, MaterialCatalog.Temperature(c) / 10f));
            int ambient = (int)Math.Min(1000, c.AmbientMana * .5f);
            int stored = (int)Math.Min(1000, 10f * c.StoredMana / Math.Max(.001f, MaterialCatalog.StorageCapacity(c)));
            unchecked { return ((int)c.Material * 1000003) ^ (heat * 971) ^ (ambient * 37) ^ stored; }
        }
        static string F(float value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }
    }
}
