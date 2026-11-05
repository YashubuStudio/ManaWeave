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
        readonly float[] _temperature;
        readonly bool[] _moved;
        readonly int[] _visualSignature;
        const float BurnEnergyPerKg = 13000000f;

        public WorldSimulator(WorldState world, SimulationConfig config = null)
        {
            if (world == null) throw new ArgumentNullException("world");
            World = world; Config = config ?? new SimulationConfig(); Config.Validate();
            Stats = new SimulationStats(); int n = world.Cells.Length;
            _heatDelta = new double[n]; _manaDelta = new double[n];
            _temperature = new float[n];
            _moved = new bool[n]; _visualSignature = new int[n];
            for (int i = 0; i < n; i++) _visualSignature[i] = VisualSignature(i);
        }

        public void Step(float dt)
        {
            if (!MaterialCatalog.Finite(dt) || dt <= 0f || dt > 1f)
                throw new ArgumentOutOfRangeException("dt", "1回の計算時間は、0秒より大きく1秒以下にしてください。");
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
            // Resolve conservative mana diffusion before reserving any mana for thermal resistance.
            // A separate reservation pass prevents six incident faces from spending the same field resource.
            Array.Clear(_manaDelta, 0, _manaDelta.Length);
            int width = World.Width, height = World.Height, layer = width * height;
            if (Config.ManaDiffusion > 0f)
            {
                for (int z = 0; z < World.Depth; z++)
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = x + width * (y + height * z);
                    if (x + 1 < width) ManaFluxPair(i, i + 1, dt);
                    if (y + 1 < height) ManaFluxPair(i, i + width, dt);
                    if (z + 1 < World.Depth) ManaFluxPair(i, i + layer, dt);
                }
                for (int i = 0; i < World.Cells.Length; i++)
                {
                    Cell c = World.Cells[i];
                    c.AmbientMana = (float)Math.Max(0.0, c.AmbientMana + _manaDelta[i]);
                    World.Cells[i] = c;
                }
            }
            if (Config.ThermalDiffusion == 0f) return;
            Array.Clear(_heatDelta, 0, _heatDelta.Length);
            for (int i = 0; i < World.Cells.Length; i++)
            {
                _temperature[i] = World.Temperature(i);
                _manaDelta[i] = World.Cells[i].AmbientMana * Math.Min(1.0, 8.0 * dt); // remaining per-cell resistance budget
            }
            for (int z = 0; z < World.Depth; z++)
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = x + width * (y + height * z);
                if (x + 1 < width) ThermalFluxPair(i, i + 1, dt);
                if (y + 1 < height) ThermalFluxPair(i, i + width, dt);
                if (z + 1 < World.Depth) ThermalFluxPair(i, i + layer, dt);
            }
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i];
                c.Energy = Math.Max(0.0, c.Energy + _heatDelta[i]);
                World.Cells[i] = c;
            }
        }

        void ManaFluxPair(int a, int b, float dt)
        {
            double q = (World.Cells[b].AmbientMana - (double)World.Cells[a].AmbientMana) * Math.Min(.10, Config.ManaDiffusion * dt);
            _manaDelta[a] += q; _manaDelta[b] -= q;
        }

        void ThermalFluxPair(int a, int b, float dt)
        {
            Cell ca = World.Cells[a], cb = World.Cells[b];
            MaterialDefinition da = MaterialCatalog.Get(ca.Material), db = MaterialCatalog.Get(cb.Material);
            double capa = Math.Max(.000001, da.HeatCapacity * ca.Mass), capb = Math.Max(.000001, db.HeatCapacity * cb.Mass);
            double harmonic = 2.0 * da.Conductivity * db.Conductivity / (da.Conductivity + db.Conductivity);
            double fraction = Math.Min(.10, Config.ThermalDiffusion * harmonic * dt);
            double qheat = (_temperature[b] - (double)_temperature[a]) / (1.0 / capa + 1.0 / capb) * fraction;
            // Six bounded edge transfers can never remove more than the available energy.
            qheat = Math.Max(-ca.Energy / 6.0, Math.Min(cb.Energy / 6.0, qheat));
            if (qheat != 0.0 && Config.SpatialResistance > 0f)
            {
                // Only an explicitly addressed region has reduced field resistance. A transported temperature
                // disturbance never grants its own authorization to the next cell.
                double accessA = DirectedThermalRemaining(a) > 0f ? Config.DirectedResistanceMultiplier : 1.0;
                double accessB = DirectedThermalRemaining(b) > 0f ? Config.DirectedResistanceMultiplier : 1.0;
                double opacity = .5 * (accessA * ca.AmbientMana / (ca.AmbientMana + 5.0) +
                    accessB * cb.AmbientMana / (cb.AmbientMana + 5.0));
                double attenuation = 1.0 - Math.Exp(-Config.SpatialResistance * opacity);
                double requested = Math.Abs(qheat) * attenuation;
                double availableA = Math.Max(0.0, Math.Min(ca.AmbientMana, _manaDelta[a]));
                double availableB = Math.Max(0.0, Math.Min(cb.AmbientMana, _manaDelta[b]));
                double available = availableA + availableB;
                double blocked = Math.Min(requested, available * Config.JoulesPerMana);
                if (blocked > 0.0 && available > 0.0)
                {
                    double cost = blocked / Config.JoulesPerMana;
                    // Commit representable debits immediately and reduce the shared per-cell budgets.
                    // Later faces see the remainder, so no field resource can be spent twice.
                    double spentA = RemoveAtMost(ref ca.AmbientMana, cost * availableA / available);
                    double spentB = RemoveAtMost(ref cb.AmbientMana, cost * availableB / available);
                    _manaDelta[a] = Math.Max(0.0, _manaDelta[a] - spentA);
                    _manaDelta[b] = Math.Max(0.0, _manaDelta[b] - spentB);
                    World.Cells[a] = ca; World.Cells[b] = cb;
                    double spent = spentA + spentB;
                    blocked = Math.Min(blocked, spent * Config.JoulesPerMana);
                    qheat -= Math.Sign(qheat) * blocked;
                    Stats.ManifestedMana += spent; Stats.DirectManifestedMana += spent;
                    Stats.SuppressedTransportJoules += blocked;
                }
            }
            _heatDelta[a] += qheat; _heatDelta[b] -= qheat;
        }

        /// <summary>Seconds left in an explicitly addressed thermal region; heat propagation never extends it.</summary>
        public float DirectedThermalRemaining(int index)
        {
            if (index < 0 || index >= World.Cells.Length) return 0f;
            return (float)Math.Max(0.0, World.Cells[index].ThermalCommandUntil - World.SimulatedSeconds);
        }

        void ApplyNaturalInteraction(float dt)
        {
            Stats.ActivePhenomena = 0;
            if (Config.NaturalCoupling == 0f) return;
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i];
                float temperature = MaterialCatalog.Temperature(c);
                if (Math.Abs(temperature - MaterialCatalog.RoomTemperature) < Config.DisturbanceThreshold) continue;
                double reference = MaterialCatalog.RoomEnergy(c), deviation = c.Energy - reference;
                if (Math.Abs(deviation) < .01) continue;
                Stats.ActivePhenomena++;
                double density = c.AmbientMana + (Config.EnableStoredInteraction ? .20 * c.StoredMana : 0.0);
                if (density <= 0.0) continue;
                bool directed = DirectedThermalRemaining(i) > 0f;
                double medium = MaterialCatalog.IsGas(c.Material) ? Config.GasResistanceMultiplier : 1.0;
                double rate = Config.NaturalCoupling * medium * density / (density + 5.0);
                if (directed) rate *= Config.DirectedResistanceMultiplier;
                // Exact exponential relaxation: every size and both temperature polarities approach the reference.
                // The factor is in [0,1]; neither large events nor a large timestep can change its sign or overshoot.
                double requested = Math.Abs(deviation) * (1.0 - Math.Exp(-rate * dt));
                float ambientAccess = c.AmbientMana * Math.Min(1f, 8f * dt);
                float storedAccess = Config.EnableStoredInteraction ? c.StoredMana * Math.Min(1f, .7f * dt) : 0f;
                double work = Math.Min(requested, (ambientAccess + (double)storedAccess) * Config.JoulesPerMana);
                if (work < .00001) continue;
                double proposed = EnergyToward(c.Energy, reference, work);
                if (proposed == c.Energy) continue;
                double cost = Math.Abs(proposed - (double)c.Energy) / Config.JoulesPerMana;
                float previousAmbient = c.AmbientMana, previousStored = c.StoredMana;
                double previousEnergy = c.Energy;
                double spentA = RemoveAtMost(ref c.AmbientMana, Math.Min(ambientAccess, cost));
                double spentS = RemoveAtMost(ref c.StoredMana, Math.Min(storedAccess, Math.Max(0.0, cost - spentA)));
                c.Energy = EnergyToward(previousEnergy, reference, (spentA + spentS) * Config.JoulesPerMana);
                if (c.Energy == previousEnergy) continue; // sub-ULP work is neither applied nor charged
                World.Cells[i] = c;
                double committed = c.Energy - (double)previousEnergy;
                double spentAmbient = previousAmbient - (double)c.AmbientMana;
                double spentStored = previousStored - (double)c.StoredMana;
                Stats.ManifestedMana += spentAmbient + spentStored;
                Stats.DirectManifestedMana += spentAmbient; Stats.StoredManifestedMana += spentStored;
                Stats.NaturalEnergyJoules += committed;
                Stats.StabilizedJoules += Math.Abs(committed);
                if (directed) Stats.DirectedStabilizedJoules += Math.Abs(committed);
                // AmplifiedJoules is retained as a legacy statistics field; it is never incremented.
            }
        }

        // Mana remains single precision; heat state is double precision. Neither can exceed its finite budget.
        // Values in these two helpers are finite and nonnegative; one neighbouring float is sufficient.
        static float NextToward(float value, float target)
        {
            if (value == target) return value;
            int bits = BitConverter.SingleToInt32Bits(value);
            return BitConverter.Int32BitsToSingle(bits + (target > value ? 1 : -1));
        }

        static double RemoveAtMost(ref float pool, double maximum)
        {
            maximum = Math.Max(0.0, Math.Min(pool, maximum));
            float old = pool, next = (float)Math.Max(0.0, pool - maximum);
            if (old - (double)next > maximum) next = NextToward(next, old);
            pool = Math.Max(0f, Math.Min(old, next));
            return old - (double)pool;
        }

        static double EnergyToward(double energy, double reference, double maximum)
        {
            double difference = reference - energy;
            double work = Math.Min(Math.Abs(difference), Math.Max(0.0, maximum));
            double result = energy + Math.Sign(difference) * work;
            if (Math.Abs(result - energy) > work)
            {
                long bits = BitConverter.DoubleToInt64Bits(result);
                result = BitConverter.Int64BitsToDouble(bits + (energy > result ? 1 : -1));
            }
            return Math.Max(0.0, difference > 0 ? Math.Min(reference, Math.Max(energy, result)) :
                Math.Max(reference, Math.Min(energy, result)));
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
                double before = c.Energy; c.Energy += used * (double)BurnEnergyPerKg; c.Fuel -= used;
                Stats.CombustionJoules += c.Energy - before;
                if (c.Fuel <= .000001f)
                {
                    c.Material = MaterialId.Ash; c.Fuel = 0f; ReleaseOverflow(ref c); World.MarkDirty(i);
                    Stats.LastEvent = "木材の燃料を使い切り、燃え残りが灰になりました。";
                }
                World.Cells[i] = c;
            }
        }

        void ChangePhases()
        {
            for (int i = 0; i < World.Cells.Length; i++)
            {
                Cell c = World.Cells[i]; if (!MaterialCatalog.IsWaterFamily(c.Material) || c.Mass <= 0f) continue;
                MaterialId previous = c.Material; double h = c.Energy / c.Mass;
                if (c.Material == MaterialId.Ice && h >= MaterialCatalog.WaterAtZero) c.Material = MaterialId.Water;
                if (c.Material == MaterialId.Water && h >= MaterialCatalog.SteamAtBoiling) c.Material = MaterialId.Steam;
                if (c.Material == MaterialId.Steam && h <= MaterialCatalog.WaterAtBoiling) c.Material = MaterialId.Water;
                if (c.Material == MaterialId.Water && h <= MaterialCatalog.IceAtZero) c.Material = MaterialId.Ice;
                if (previous != c.Material)
                {
                    ReleaseOverflow(ref c); World.Cells[i] = c; World.MarkDirty(i);
                    Stats.LastEvent = JapaneseNames.Material(previous) + " → " + JapaneseNames.Material(c.Material) + "（熱エネルギーと質量を維持）。";
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
            double commandA = ca.ThermalCommandUntil, commandB = cb.ThermalCommandUntil;
            cb.AmbientMana = spaceA; ca.AmbientMana = spaceB;
            cb.ThermalCommandUntil = commandA; ca.ThermalCommandUntil = commandB;
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
            if (command == null) return SpellResult.Fail("術式が指定されていません。");
            if (!Enum.IsDefined(typeof(SpellOperation), command.Operation)) return SpellResult.Fail("対応していない術式です。");
            if (!World.InBounds(command.X, command.Y, command.Z)) return SpellResult.Fail("対象が世界の範囲外です。島の中を狙ってください。");
            if (command.Radius < 1 || command.Radius > 5) return SpellResult.Fail("効果の半径は1～5セルで指定してください。");
            if (!MaterialCatalog.Finite(command.Power) || command.Power < 0f) return SpellResult.Fail("術式の出力量には、0以上の有限な値を指定してください。");
            if (!MaterialCatalog.IsValid(command.Material)) return SpellResult.Fail("対応していない物質です。");
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
                default: return SpellResult.Fail("対応していない術式です。");
            }
        }

        SpellResult CastThermal(SpellCommand command, List<int> cells, bool cooling)
        {
            if (command.Power <= 0f || command.Power > 8000000f) return SpellResult.Fail("熱量は0 Jより大きく、8,000,000 J以下で指定してください。");
            double weight = 0;
            for (int j = 0; j < cells.Count; j++)
            {
                Cell c = World.Cells[cells[j]];
                weight += cooling ? c.Energy : c.Mass * (double)MaterialCatalog.Get(c.Material).HeatCapacity;
            }
            if (weight <= .000001) return SpellResult.Fail("対象範囲に、熱を操作できる物質がありません。");
            double total = cooling ? Math.Min(command.Power, weight) : command.Power;
            float cost = (float)(total / Config.JoulesPerMana + 2f + cells.Count * .08f);
            if (!CanPay(cost)) return SpellResult.Fail("体内魔力が不足しています。必要量は " + F(cost) + " MUです。");
            Pay(cost); double actual = 0;
            for (int j = 0; j < cells.Count; j++)
            {
                int i = cells[j]; Cell c = World.Cells[i];
                double portion = cooling ? c.Energy : c.Mass * (double)MaterialCatalog.Get(c.Material).HeatCapacity;
                double old = c.Energy;
                c.Energy = cooling && total >= weight ? 0.0 :
                    Math.Max(0.0, c.Energy + (cooling ? -1.0 : 1.0) * total * portion / weight);
                actual += c.Energy - (double)old;
                c.ThermalCommandUntil = World.SimulatedSeconds + Config.DirectedThermalSeconds;
                World.Cells[i] = c; World.MarkDirty(i);
            }
            Stats.SpellJoules += actual;
            return Success(cells.Count + "セル全体で " + F((float)Math.Abs(actual)) + (cooling ? " Jの熱を取り除きました。" : " Jの熱を加えました。"), cost);
        }

        SpellResult CastCharge(SpellCommand command, List<int> cells)
        {
            if (command.Power <= 0f || command.Power > 10000f) return SpellResult.Fail("充填量は0 MUより大きく、10,000 MU以下で指定してください。");
            double capacity = 0;
            for (int j = cells.Count - 1; j >= 0; j--)
            {
                Cell c = World.Cells[cells[j]];
                if (c.Material == MaterialId.Air || c.Material == MaterialId.Steam) { cells.RemoveAt(j); continue; }
                float free = Math.Max(0f, MaterialCatalog.StorageCapacity(c) - c.StoredMana);
                if (free <= .000001f) cells.RemoveAt(j); else capacity += free;
            }
            if (capacity <= .000001) return SpellResult.Fail("対象範囲に、魔力の保有容量に空きがある物質がありません。");
            float amount = (float)Math.Min(command.Power, capacity), overhead = 2f, cost = amount + overhead;
            if (!CanPay(cost)) return SpellResult.Fail("体内魔力が不足しています。必要量は " + F(cost) + " MUです。");
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
            return Success("物質へ " + F((float)moved) + " MUを移しました。制御に2 MUを消費しました。", cost);
        }

        SpellResult CastTransmute(List<int> cells)
        {
            for (int j = cells.Count - 1; j >= 0; j--)
                if (World.Cells[cells[j]].Material != MaterialId.Iron) cells.RemoveAt(j);
            if (cells.Count == 0) return SpellResult.Fail("錬成には、対象範囲内に鉄が必要です。");
            for (int j = 0; j < cells.Count; j++)
            {
                Cell c = World.Cells[cells[j]];
                if (MaterialCatalog.Temperature(c) < 250f || c.StoredMana < 6f)
                    return SpellResult.Fail("範囲内の鉄は、各セルに250 °C以上の温度と6 MU以上の保有魔力が必要です。先に加熱・充填してください。");
            }
            float cost = 10f * cells.Count;
            if (!CanPay(cost)) return SpellResult.Fail("錬成に必要な体内魔力が不足しています。");
            Pay(cost);
            for (int j = 0; j < cells.Count; j++)
            {
                int i = cells[j]; Cell c = World.Cells[i];
                c.StoredMana -= 6f; c.Material = MaterialId.Alloy;
                double old = c.Energy; c.Energy += 6.0 * Config.JoulesPerMana;
                Stats.ReactionJoules += c.Energy - old;
                Stats.ManifestedMana += 6f; Stats.StoredManifestedMana += 6f;
                World.Cells[i] = c; World.MarkDirty(i);
            }
            return Success("鉄 " + cells.Count + "セルを魔導合金へ錬成しました。質量を保ち、各セルの保有魔力を6 MU消費しました。", cost);
        }

        SpellResult CastMatter(SpellCommand command, List<int> cells, bool build)
        {
            if (build && (command.Material == MaterialId.Air || command.Material == MaterialId.Steam))
                return SpellResult.Fail("建築用の素材を選んでください。空気と蒸気は配置できません。");
            for (int j = cells.Count - 1; j >= 0; j--)
            {
                MaterialId id = World.Cells[cells[j]].Material;
                bool empty = id == MaterialId.Air || id == MaterialId.Steam;
                if (build ? !empty : empty) cells.RemoveAt(j);
            }
            if (cells.Count == 0) return SpellResult.Fail(build ? "建築する範囲に空きがありません。" : "対象範囲に掘削できる物質がありません。");
            float cost = 2f + cells.Count * (build ? 5f : 3f);
            if (!CanPay(cost)) return SpellResult.Fail("地形を変更するための体内魔力が不足しています。");
            Pay(cost);
            for (int j = 0; j < cells.Count; j++)
            {
                int i = cells[j]; Cell before = World.Cells[i]; int x, y, z; World.Coordinates(i, out x, out y, out z);
                World.SetMaterial(x, y, z, build ? command.Material : MaterialId.Air);
                Cell after = World.Cells[i];
                Stats.SandboxMassKg += after.Mass - (double)before.Mass;
                Stats.SandboxEnergyJoules += after.Energy - (double)before.Energy;
            }
            return Success(build ? JapaneseNames.Material(command.Material) + "を " + cells.Count + "セルに配置しました（創作モードの素材収支へ記録）。" :
                cells.Count + "セルを掘削しました（創作モードの素材収支へ記録）。", cost);
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
                Stats.LastEvent = "小規模実験：50 kJの熱変化です。命令されていない空間魔力が、基準温度からの変化に抵抗します。";
            }
            else if (id == 1)
            {
                for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    if (World.InBounds(x + dx, y, z + dz)) InjectHeat(World.Index(x + dx, y, z + dz), 120000f);
                Stats.LastEvent = "大規模実験：石材25セルへ合計3 MJの熱を与えました。規模が大きくても増幅せず、熱変化と周囲への伝播に抵抗します。";
            }
            else if (id == 2)
            {
                int i = World.Index(x, y, z); Cell c = World.Cells[i];
                float target = MaterialCatalog.EnergyAtTemperature(MaterialId.Iron, c.Mass, 320f);
                InjectHeat(i, Math.Max(0f, target - c.Energy)); c = World.Cells[i];
                float injected = Math.Max(0f, 12f - c.StoredMana); c.StoredMana += injected;
                Stats.ExperimentMana += injected; World.Cells[i] = c; World.MarkDirty(i);
                Stats.LastEvent = "錬成準備：鉄を320 °C、保有魔力12 MUにしました。鉄を狙って錬成を発動すると魔導合金になります。";
            }
            else
            {
                int i = World.Index(x, y, z); Cell c = World.Cells[i];
                // No stored mana is needed. Releasing any residual stock retains the total mana inventory.
                c.AmbientMana += c.StoredMana; c.StoredMana = 0f; World.Cells[i] = c;
                InjectHeat(i, 60000f);
                Stats.LastEvent = "直接抵抗実験：保有魔力0の空気セルへ60 kJの熱を与えました。物質への吸収を経ず、空間魔力が熱変化に抵抗します。";
            }
        }

        void InjectHeat(int i, double joules)
        {
            Cell c = World.Cells[i]; double before = c.Energy; c.Energy += joules; World.Cells[i] = c;
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
