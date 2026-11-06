using System;
using System.Collections.Generic;

namespace ManaWeave.Core
{
    public sealed partial class WorldSimulator
    {
        public const float MaximumSpellJoules = 8000000f;
        public const float MaximumTargetTemperature = 5000f;
        public const int MaximumMoveDistance = 8;
        public const float ThermalProtectionManaPerCellSecond = .05f;

        // Both preview and execution build the same complete, local change set.
        // A plan is never cached across frames; resources and targets are rechecked at cast time.
        sealed class SpellPlan
        {
            public SpellQuote Quote;
            public readonly Dictionary<int, Cell> Changes = new Dictionary<int, Cell>();
            public double BaseManifestation, TransferredMana, StoredManifestation;
            public double SpellJoules, ReactionJoules, SandboxEnergyJoules, SandboxMassKg;
            public double CasterDebit;
            public float CasterAfter;
            public string ResultMessage;
        }

        public SpellQuote Quote(SpellCommand command)
        {
            return CreateSpellPlan(command).Quote;
        }

        public SpellResult Cast(SpellCommand command)
        {
            SpellPlan plan = CreateSpellPlan(command);
            if (!plan.Quote.Success) return SpellResult.Fail(plan.Quote.Message);
            // No gameplay validation remains after this point; all cells and the debit were prepared first.
            World.CasterMana = plan.CasterAfter;
            foreach (KeyValuePair<int, Cell> change in plan.Changes)
            {
                World.Cells[change.Key] = change.Value;
                World.MarkDirty(change.Key);
            }
            Stats.ManifestedMana += plan.CasterDebit - plan.TransferredMana + plan.StoredManifestation;
            Stats.StoredManifestedMana += plan.StoredManifestation;
            Stats.TransferredMana += plan.TransferredMana;
            Stats.SpellJoules += plan.SpellJoules;
            Stats.ReactionJoules += plan.ReactionJoules;
            Stats.SandboxEnergyJoules += plan.SandboxEnergyJoules;
            Stats.SandboxMassKg += plan.SandboxMassKg;
            string message = plan.ResultMessage + " 体内魔力を " + F(plan.Quote.ManaCost) + " MU消費しました。";
            Stats.LastEvent = message;
            return new SpellResult { Success = true, Message = message, ManaCost = plan.Quote.ManaCost,
                BaseManaCost = plan.Quote.BaseManaCost, ChantMultiplier = plan.Quote.ChantMultiplier,
                EffectiveChantLength = plan.Quote.EffectiveChantLength };
        }

        SpellPlan CreateSpellPlan(SpellCommand command)
        {
            var plan = new SpellPlan();
            string error = ValidateSpell(command);
            if (error != null) { plan.Quote.Message = error; return plan; }
            var cells = Sphere(command);
            if (command.HasMaterialFilter)
                cells.RemoveAll(i => World.Cells[i].Material != command.TargetMaterial);
            if (cells.Count == 0)
            { plan.Quote.Message = "指定範囲に、条件と一致する物質がありません。"; return plan; }
            switch (command.Operation)
            {
                case SpellOperation.Heat: error = PlanThermal(command, cells, false, plan); break;
                case SpellOperation.Cool: error = PlanThermal(command, cells, true, plan); break;
                case SpellOperation.Charge: error = PlanCharge(command, cells, plan); break;
                case SpellOperation.Transmute: error = PlanTransmute(cells, plan); break;
                case SpellOperation.Excavate: error = PlanMatter(command, cells, false, plan); break;
                case SpellOperation.Build: error = PlanMatter(command, cells, true, plan); break;
                case SpellOperation.Move: error = PlanMove(command, cells, plan); break;
                default: error = "対応していない術式です。"; break;
            }
            if (error != null) { plan.Quote.Message = error; return plan; }
            FinalizeSpellCost(command, plan);
            return plan;
        }

        string ValidateSpell(SpellCommand command)
        {
            if (command == null) return "術式が指定されていません。";
            if (!Enum.IsDefined(typeof(SpellOperation), command.Operation)) return "対応していない術式です。";
            if (!World.InBounds(command.X, command.Y, command.Z)) return "対象が世界の範囲外です。島の中を狙ってください。";
            if (command.Radius < 0 || command.Radius > 5) return "半径は0～5セルで指定してください。0は対象一点です。";
            if (!MaterialCatalog.Finite(command.Power) || command.Power < 0f) return "術式の出力量には、0以上の有限な値を指定してください。";
            if (!MaterialCatalog.IsValid(command.Material) ||
                (command.HasMaterialFilter && !MaterialCatalog.IsValid(command.TargetMaterial))) return "対応していない物質です。";
            if (!Enum.IsDefined(typeof(ThermalMode), command.ThermalMode)) return "熱の指定方法が不明です。";
            if (!MaterialCatalog.Finite(command.TargetTemperature)) return "目標温度には有限な値を指定してください。";
            if (!MaterialCatalog.Finite(command.DurationSeconds) || command.DurationSeconds < 0f ||
                (command.DurationSeconds > 0f && command.DurationSeconds < 1f) ||
                command.DurationSeconds > SimulationConfig.MaximumDirectedThermalSeconds)
                return "熱の保護時間は1～30秒で指定してください。0は標準時間を使います。";
            bool thermal = command.Operation == SpellOperation.Heat || command.Operation == SpellOperation.Cool;
            if (!thermal && (command.ThermalMode != ThermalMode.Energy || command.DurationSeconds != 0f))
                return "温度と熱の保護時間を指定できるのは加熱・冷却だけです。";
            if (command.Operation == SpellOperation.Build && command.HasMaterialFilter)
                return "建築では対象素材の選別ではなく、配置する素材を指定してください。";
            if (command.Operation != SpellOperation.Move &&
                (command.OffsetX != 0 || command.OffsetY != 0 || command.OffsetZ != 0)) return "移動量は物質移動の術式で指定してください。";
            if (!MaterialCatalog.Finite(World.CasterMana) || World.CasterMana < 0f) return "体内魔力の状態が不正です。";
            if (command.Incantation != null)
            {
                if (command.Incantation.Length > ChantEconomy.MaximumInputLength) return "詠唱は512文字以内で指定してください。";
                try
                {
                    if (command.Incantation.Normalize(System.Text.NormalizationForm.FormKC).Length > ChantEconomy.MaximumInputLength)
                        return "表記を揃えた後の詠唱が512文字を超えています。";
                }
                catch (ArgumentException) { return "詠唱に扱えない文字が含まれています。"; }
            }
            try { Config.Validate(); }
            catch (ArgumentException e) { return e.Message; }
            return null;
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

        void FinalizeSpellCost(SpellCommand command, SpellPlan plan)
        {
            int length = ChantEconomy.EffectiveLength(command.Incantation);
            float multiplier = ChantEconomy.Multiplier(command.Incantation);
            double basis = plan.BaseManifestation + plan.TransferredMana;
            double requested = plan.BaseManifestation * multiplier + plan.TransferredMana;
            plan.Quote.BaseManaCost = (float)basis;
            plan.Quote.ChantMultiplier = multiplier;
            plan.Quote.EffectiveChantLength = length;
            plan.Quote.ThermalJoules = Math.Abs(plan.SpellJoules);
            plan.Quote.ManaCost = (float)requested;
            if (double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0.0 || requested > float.MaxValue)
            { plan.Quote.Message = "必要魔力を有効な数値で計算できません。"; return; }
            if (requested > World.CasterMana)
            { plan.Quote.Message = "体内魔力が不足しています。必要量は " + F(plan.Quote.ManaCost) + " MUです。"; return; }
            float after = (float)Math.Max(0.0, World.CasterMana - requested);
            // Round toward a paid debit, rather than granting free work at a float representability boundary.
            if (World.CasterMana - (double)after < requested && after > 0f) after = NextToward(after, 0f);
            plan.CasterAfter = after;
            plan.CasterDebit = World.CasterMana - (double)after;
            plan.Quote.ManaCost = (float)plan.CasterDebit;
            plan.Quote.Success = true;
            plan.Quote.Message = plan.ResultMessage;
        }

        string PlanThermal(SpellCommand command, List<int> cells, bool cooling, SpellPlan plan)
        {
            bool target = command.ThermalMode == ThermalMode.Temperature;
            if (target && (command.TargetTemperature < MaterialCatalog.AbsoluteZero || command.TargetTemperature > MaximumTargetTemperature))
                return "目標温度は−273.15～5,000 °Cで指定してください。";
            if (!target && (command.Power <= 0f || command.Power > MaximumSpellJoules))
                return "熱量は0 Jより大きく、8,000,000 J以下で指定してください。";
            cells.RemoveAll(i => World.Cells[i].Mass <= 0f);
            if (cells.Count == 0) return "対象範囲に、熱を操作できる物質がありません。";
            double weight = 0.0;
            var portions = new double[cells.Count];
            for (int j = 0; j < cells.Count; j++)
            {
                Cell c = World.Cells[cells[j]];
                double part;
                if (target)
                {
                    double desired = EnergyForTarget(c, command.TargetTemperature);
                    part = Math.Max(0.0, cooling ? c.Energy - desired : desired - c.Energy);
                }
                else part = cooling ? c.Energy : c.Mass * (double)MaterialCatalog.Get(c.Material).HeatCapacity;
                portions[j] = part; weight += part;
            }
            if (double.IsNaN(weight) || double.IsInfinity(weight)) return "対象の熱量を計算できません。";
            if (weight <= .000001 && (!target || command.DurationSeconds <= 0f))
                return target ? "対象はすでに指定した温度条件を満たしています。" : "対象範囲に操作できる熱量がありません。";
            double total = target ? weight : (cooling ? Math.Min(command.Power, weight) : command.Power);
            if (total > MaximumSpellJoules) return "この温度へ変えるための総熱量が8 MJを超えています。範囲を狭めるか、温度差を小さくしてください。";
            double duration = command.DurationSeconds > 0f ? command.DurationSeconds : Config.DirectedThermalSeconds;
            double actual = 0.0;
            for (int j = 0; j < cells.Count; j++)
            {
                int index = cells[j]; Cell c = World.Cells[index]; double old = c.Energy;
                double amount = target ? portions[j] : total * portions[j] / weight;
                c.Energy = cooling && amount >= c.Energy ? 0.0 : Math.Max(0.0, old + (cooling ? -amount : amount));
                actual += c.Energy - old;
                c.ThermalCommandUntil = Math.Max(c.ThermalCommandUntil, World.SimulatedSeconds + duration);
                if (target && Config.EnablePhaseChanges) ResolveWaterPhase(ref c);
                plan.Changes[index] = c;
            }
            plan.SpellJoules = actual;
            plan.BaseManifestation = Math.Abs(actual) / Config.JoulesPerMana + 2.0 + cells.Count * .08 +
                Math.Max(0.0, duration - Config.DirectedThermalSeconds) * cells.Count * ThermalProtectionManaPerCellSecond;
            plan.Quote.AffectedCells = cells.Count;
            plan.ResultMessage = cells.Count + "セル全体で " + F((float)Math.Abs(actual)) +
                (cooling ? " Jの熱を取り除きました。" : " Jの熱を加えました。") +
                " 指定範囲の熱抵抗を " + F((float)duration) + " 秒間弱めます。";
            return null;
        }

        static double EnergyForTarget(Cell cell, float temperature)
        {
            if (temperature <= MaterialCatalog.AbsoluteZero) return 0.0;
            double t = temperature, mass = cell.Mass;
            if (MaterialCatalog.IsWaterFamily(cell.Material))
            {
                if (t < 0.0) return mass * Math.Max(0.0, MaterialCatalog.IceAtZero + 2100.0 * t);
                if (t <= 100.0) return mass * (MaterialCatalog.WaterAtZero + 4184.0 * t);
                return mass * (MaterialCatalog.SteamAtBoiling + 2010.0 * (t - 100.0));
            }
            return mass * MaterialCatalog.Get(cell.Material).HeatCapacity * (t - MaterialCatalog.AbsoluteZero);
        }

        static void ResolveWaterPhase(ref Cell c)
        {
            if (!MaterialCatalog.IsWaterFamily(c.Material) || c.Mass <= 0f) return;
            MaterialId previous = c.Material; double h = c.Energy / c.Mass;
            if (c.Material == MaterialId.Ice && h >= MaterialCatalog.WaterAtZero) c.Material = MaterialId.Water;
            if (c.Material == MaterialId.Water && h >= MaterialCatalog.SteamAtBoiling) c.Material = MaterialId.Steam;
            if (c.Material == MaterialId.Steam && h <= MaterialCatalog.WaterAtBoiling) c.Material = MaterialId.Water;
            if (c.Material == MaterialId.Water && h <= MaterialCatalog.IceAtZero) c.Material = MaterialId.Ice;
            if (previous != c.Material) ReleaseOverflow(ref c);
        }

        string PlanCharge(SpellCommand command, List<int> cells, SpellPlan plan)
        {
            if (command.Power <= 0f || command.Power > 10000f) return "充填量は0 MUより大きく、10,000 MU以下で指定してください。";
            double capacity = 0.0;
            for (int j = cells.Count - 1; j >= 0; j--)
            {
                Cell c = World.Cells[cells[j]];
                double room = Math.Max(0.0, MaterialCatalog.StorageCapacity(c) - (double)c.StoredMana);
                if (MaterialCatalog.IsGas(c.Material) || room <= .000001) cells.RemoveAt(j);
                else capacity += room;
            }
            if (capacity <= .000001) return "対象範囲に、魔力の保有容量に空きがある物質がありません。";
            double amount = Math.Min(command.Power, capacity), moved = 0.0;
            for (int j = 0; j < cells.Count; j++)
            {
                int index = cells[j]; Cell c = World.Cells[index]; float cap = MaterialCatalog.StorageCapacity(c);
                double room = Math.Max(0.0, cap - (double)c.StoredMana);
                double portion = Math.Min(amount - moved, amount * room / capacity);
                float next = (float)Math.Min(cap, c.StoredMana + portion);
                if (next - (double)c.StoredMana > portion) next = NextToward(next, c.StoredMana);
                moved += next - (double)c.StoredMana; c.StoredMana = next;
                plan.Changes[index] = c;
            }
            if (moved <= .000001) return "指定した充填量が小さすぎます。";
            plan.TransferredMana = moved;
            plan.BaseManifestation = 2.0;
            plan.Quote.AffectedCells = cells.Count;
            plan.ResultMessage = "物質へ " + F((float)moved) + " MUを移しました。充填する魔力の元本は割引しません。";
            return null;
        }

        string PlanTransmute(List<int> cells, SpellPlan plan)
        {
            cells.RemoveAll(i => World.Cells[i].Material != MaterialId.Iron);
            if (cells.Count == 0) return "錬成には、対象範囲内に鉄が必要です。";
            foreach (int index in cells)
            {
                Cell c = World.Cells[index];
                if (MaterialCatalog.Temperature(c) < 250f || c.StoredMana < 6f)
                    return "範囲内の鉄は、各セルに250 °C以上の温度と6 MU以上の保有魔力が必要です。先に加熱・充填してください。";
            }
            foreach (int index in cells)
            {
                Cell c = World.Cells[index]; float after = (float)(c.StoredMana - 6.0);
                if (c.StoredMana - (double)after < 6.0 && after > 0f) after = NextToward(after, 0f);
                double spent = c.StoredMana - (double)after;
                c.StoredMana = after; c.Material = MaterialId.Alloy;
                double old = c.Energy; c.Energy += spent * Config.JoulesPerMana;
                plan.StoredManifestation += spent; plan.ReactionJoules += c.Energy - old;
                plan.Changes[index] = c;
            }
            plan.BaseManifestation = 10.0 * cells.Count;
            plan.Quote.AffectedCells = cells.Count;
            plan.ResultMessage = "鉄 " + cells.Count + "セルを魔導合金へ錬成しました。質量を保ち、保有魔力を合計 " +
                F((float)plan.StoredManifestation) + " MU消費しました。";
            return null;
        }

        string PlanMatter(SpellCommand command, List<int> cells, bool build, SpellPlan plan)
        {
            if (build && MaterialCatalog.IsGas(command.Material)) return "建築用の素材を選んでください。空気と蒸気は配置できません。";
            cells.RemoveAll(i => build ? !MaterialCatalog.IsGas(World.Cells[i].Material) : MaterialCatalog.IsGas(World.Cells[i].Material));
            if (cells.Count == 0) return build ? "建築する範囲に空きがありません。" : "対象範囲に掘削できる物質がありません。";
            MaterialId material = build ? command.Material : MaterialId.Air;
            MaterialDefinition definition = MaterialCatalog.Get(material);
            foreach (int index in cells)
            {
                Cell old = World.Cells[index];
                Cell c = new Cell { Material = material, Mass = definition.DefaultMass,
                    Energy = MaterialCatalog.EnergyAtTemperature(material, definition.DefaultMass, 20f),
                    AmbientMana = old.AmbientMana, StoredMana = old.StoredMana,
                    Fuel = material == MaterialId.Wood ? definition.DefaultMass : 0f, ThermalCommandUntil = 0.0 };
                ReleaseOverflow(ref c);
                plan.SandboxMassKg += c.Mass - (double)old.Mass;
                plan.SandboxEnergyJoules += c.Energy - old.Energy;
                plan.Changes[index] = c;
            }
            plan.BaseManifestation = 2.0 + cells.Count * (build ? 5.0 : 3.0);
            plan.Quote.AffectedCells = cells.Count;
            plan.ResultMessage = build ? JapaneseNames.Material(command.Material) + "を " + cells.Count + "セルに配置しました（創作モードの素材収支へ記録）。" :
                cells.Count + "セルを掘削しました（創作モードの素材収支へ記録）。";
            return null;
        }

        Cell PlannedCell(SpellPlan plan, int index)
        {
            Cell cell;
            return plan.Changes.TryGetValue(index, out cell) ? cell : World.Cells[index];
        }

        string PlanMove(SpellCommand command, List<int> cells, SpellPlan plan)
        {
            if (command.OffsetX < -MaximumMoveDistance || command.OffsetX > MaximumMoveDistance ||
                command.OffsetY < -MaximumMoveDistance || command.OffsetY > MaximumMoveDistance ||
                command.OffsetZ < -MaximumMoveDistance || command.OffsetZ > MaximumMoveDistance)
                return "移動距離は1～8 mで指定してください。";
            int axes = (command.OffsetX != 0 ? 1 : 0) + (command.OffsetY != 0 ? 1 : 0) + (command.OffsetZ != 0 ? 1 : 0);
            if (axes != 1) return "一度の物質移動では、上下・東西・南北のうち一方向を指定してください。";
            int dx = Math.Sign(command.OffsetX), dy = Math.Sign(command.OffsetY), dz = Math.Sign(command.OffsetZ);
            int distance = Math.Abs(command.OffsetX) + Math.Abs(command.OffsetY) + Math.Abs(command.OffsetZ);
            cells.RemoveAll(i => MaterialCatalog.IsGas(World.Cells[i].Material) || World.Cells[i].Mass <= 0f);
            if (cells.Count == 0) return "指定範囲に移動できる物質がありません。空気と蒸気は移動対象にしません。";
            // Farthest-first swaps retain displaced gas and safely support overlapping source/destination regions.
            cells.Sort((a, b) =>
            {
                int ax, ay, az, bx, by, bz;
                World.Coordinates(a, out ax, out ay, out az); World.Coordinates(b, out bx, out by, out bz);
                int order = (bx * dx + by * dy + bz * dz).CompareTo(ax * dx + ay * dy + az * dz);
                return order != 0 ? order : a.CompareTo(b);
            });
            var current = new List<int>(cells);
            double effort = 0.0;
            for (int step = 0; step < distance; step++)
            {
                var occupied = new HashSet<int>(current);
                var next = new List<int>(current.Count);
                foreach (int index in current)
                {
                    int x, y, z; World.Coordinates(index, out x, out y, out z);
                    if (!World.InBounds(x + dx, y + dy, z + dz)) return "移動経路または移動先が世界の範囲外です。";
                    int destination = World.Index(x + dx, y + dy, z + dz);
                    if (!occupied.Contains(destination) && !MaterialCatalog.IsGas(PlannedCell(plan, destination).Material))
                        return "移動経路に別の物質があります。通り道を空けてください。";
                    next.Add(destination);
                }
                for (int j = 0; j < current.Count; j++)
                {
                    int source = current[j], destination = next[j];
                    Cell a = PlannedCell(plan, source), b = PlannedCell(plan, destination);
                    double density = Math.Max(0.0, .5 * (a.AmbientMana + (double)b.AmbientMana));
                    effort += .5 * a.Mass * (1.0 + 4.0 * density / (density + 5.0));
                    float ambientA = a.AmbientMana, ambientB = b.AmbientMana;
                    double thermalA = a.ThermalCommandUntil, thermalB = b.ThermalCommandUntil;
                    b.AmbientMana = ambientA; a.AmbientMana = ambientB;
                    b.ThermalCommandUntil = thermalA; a.ThermalCommandUntil = thermalB;
                    plan.Changes[source] = b; plan.Changes[destination] = a;
                }
                current = next;
            }
            plan.BaseManifestation = 2.0 + cells.Count * .08 + effort;
            plan.Quote.AffectedCells = cells.Count;
            string direction = dx > 0 ? "東" : dx < 0 ? "西" : dy > 0 ? "上" : dy < 0 ? "下" : dz > 0 ? "北" : "南";
            plan.ResultMessage = "物質 " + cells.Count + "セルを" + direction + "へ " + distance + " m移動しました。";
            return null;
        }
    }
}
