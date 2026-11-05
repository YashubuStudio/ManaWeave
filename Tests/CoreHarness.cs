using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ManaWeave.Core;

public static class CoreHarness
{
    static int passed, failed;
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--export-world")
        {
            ExportWorld(args[1]);
            return 0;
        }
        Run("seeded generation reproduces world and changes with seed", TestSeed);
        Run("mana originates in space without matter absorption", TestGeneration);
        Run("all materials absorb; capacity and rate differ", TestAbsorption);
        Run("mana diffusion conserves the transferred resource", TestManaDiffusion);
        Run("ordinary heat conduction conserves energy", TestHeatConduction);
        Run("ambient mana damps a small event with zero stored mana", TestDirectDamping);
        Run("cold damping warms toward reference while cold resonance cools further", TestColdInteraction);
        Run("connected large events amplify; separated small events damp", TestCoherence);
        Run("stored mana can manifest without ambient mana", TestStoredPath);
        Run("no mana means no supernatural damping or amplification", TestManaAbsent);
        Run("combined generation absorption and manifestation reconcile", TestManaLedger);
        Run("rejected spells mutate neither matter nor mana", TestAtomicFailure);
        Run("thermal spell adds its TOTAL energy, not energy per cell", TestThermalSpell);
        Run("charge transfers mana and destroys only control overhead", TestCharge);
        Run("alchemy validates heat and stock, retains mass", TestTransmute);
        Run("creative editing releases stored mana and records material source", TestCreativeEdits);
        Run("phase changes honor latent energy and retain mass/mana", TestPhases);
        Run("voxel fluid transport moves matter but leaves ambient field in space", TestFluidFlow);
        Run("combustion consumes finite fuel and records released heat", TestCombustion);
        Run("atomic save roundtrip overwrite checksum and invalid state handling", TestPersistence);
        Run("fixed input replay is reproducible on the same runtime", TestReplay);
        Run("invalid timestep and configuration are rejected", TestInvalidParameters);
        Run("four demonstration setups use explicit external ledgers", TestExperiments);
        Console.WriteLine("CORE_RESULT passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        var watch = Stopwatch.StartNew();
        try { test(); passed++; Console.WriteLine("PASS " + name + " (" + watch.ElapsedMilliseconds + " ms)"); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Near(double expected, double actual, double tolerance, string message)
    { if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance) throw new Exception(message + ": expected=" + expected + " actual=" + actual); }
    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    static SimulationConfig Quiet()
    {
        return new SimulationConfig { ManaGeneration = 0f, ManaDiffusion = 0f, AbsorptionScale = 0f,
            ThermalDiffusion = 0f, NaturalCoupling = 0f, CasterAbsorptionRate = 0f,
            EnableCombustion = false, EnableFluidFlow = false, EnablePhaseChanges = false };
    }
    static WorldState Empty(int width = 3, int height = 3, int depth = 3)
    {
        var w = new WorldState(width, height, depth, 17) { CasterMana = 0, CasterManaMax = 1800 };
        for (int i = 0; i < w.Cells.Length; i++) { Cell c = w.Cells[i]; c.AmbientMana = 0; w.Cells[i] = c; }
        return w;
    }
    static void SetAmbient(WorldState w, float amount)
    { for (int i = 0; i < w.Cells.Length; i++) { Cell c = w.Cells[i]; c.AmbientMana = amount; w.Cells[i] = c; } }
    static void Heat(WorldState w, int i, float excess)
    { Cell c = w.Cells[i]; c.Energy = MaterialCatalog.RoomEnergy(c) + excess; w.Cells[i] = c; }
    static void Temperature(WorldState w, int i, float value)
    { Cell c = w.Cells[i]; c.Energy = MaterialCatalog.EnergyAtTemperature(c.Material, c.Mass, value); w.Cells[i] = c; }
    static void Stored(WorldState w, int i, float value)
    { Cell c = w.Cells[i]; c.StoredMana = value; w.Cells[i] = c; }
    static void EqualCells(WorldState a, WorldState b)
    {
        Assert(a.Width == b.Width && a.Height == b.Height && a.Depth == b.Depth, "dimensions differ");
        Assert(a.Tick == b.Tick && a.SimulatedSeconds == b.SimulatedSeconds && a.Seed == b.Seed, "clock or seed differs");
        Assert(a.CasterMana == b.CasterMana && a.CasterManaMax == b.CasterManaMax, "caster differs");
        Assert(a.CasterX == b.CasterX && a.CasterY == b.CasterY && a.CasterZ == b.CasterZ, "caster position differs");
        for (int i = 0; i < a.Cells.Length; i++)
        {
            Cell x = a.Cells[i], y = b.Cells[i];
            Assert(x.Material == y.Material && x.Mass == y.Mass && x.Energy == y.Energy &&
                x.AmbientMana == y.AmbientMana && x.StoredMana == y.StoredMana && x.Fuel == y.Fuel, "cell differs at " + i);
        }
    }

    static void TestSeed()
    {
        var a = WorldGenerator.Generate(24, 16, 24, 731); var b = WorldGenerator.Generate(24, 16, 24, 731);
        EqualCells(a, b); var c = WorldGenerator.Generate(24, 16, 24, 732); bool different = false;
        for (int i = 0; i < a.Cells.Length; i++) if (a.Cells[i].Material != c.Cells[i].Material) { different = true; break; }
        Assert(different, "different seed has identical terrain");
        for (int id = 0; id < MaterialCatalog.Count; id++)
            Assert(MaterialCatalog.Get((MaterialId)id).ManaCapacity > 0f && MaterialCatalog.Get((MaterialId)id).AbsorptionRate > 0f,
                "a material cannot absorb/store mana");
    }

    static void TestGeneration()
    {
        var w = Empty(); var config = Quiet(); config.ManaGeneration = 1f;
        var sim = new WorldSimulator(w, config); sim.Step(.5f);
        Assert(w.Cells[0].AmbientMana > 0f && w.Cells[0].StoredMana == 0f, "generation depended on absorption");
        Near(sim.Stats.GeneratedMana, w.TotalMana(), .00001, "generated mana ledger");
    }

    static void TestAbsorption()
    {
        var w = Empty(MaterialCatalog.Count, 1, 1);
        for (int id = 0; id < MaterialCatalog.Count; id++) w.SetMaterial(id, 0, 0, (MaterialId)id);
        SetAmbient(w, 100f); double before = w.TotalMana(); var config = Quiet(); config.AbsorptionScale = 1f;
        var sim = new WorldSimulator(w, config); sim.Step(.5f);
        for (int i = 0; i < w.Cells.Length; i++)
            Assert(w.Cells[i].StoredMana > 0f && w.Cells[i].StoredMana <= MaterialCatalog.StorageCapacity(w.Cells[i]), "invalid store " + i);
        Assert(w.Cells[(int)MaterialId.Crystal].StoredMana > w.Cells[(int)MaterialId.Wood].StoredMana, "material rates ignored");
        Near(before, w.TotalMana(), .001, "absorption changed total mana");
    }

    static void TestManaDiffusion()
    {
        var w = Empty(3, 1, 1); Cell c = w.Cells[0]; c.AmbientMana = 100f; w.Cells[0] = c;
        var config = Quiet(); config.ManaDiffusion = .8f;
        new WorldSimulator(w, config).Step(.2f);
        Near(100, w.TotalMana(), .0001, "mana diffusion conservation");
        Assert(w.Cells[0].AmbientMana < 100 && w.Cells[1].AmbientMana > 0, "field did not diffuse");
    }

    static void TestHeatConduction()
    {
        var w = Empty(3, 1, 1);
        for (int x = 0; x < 3; x++) w.SetMaterial(x, 0, 0, MaterialId.Stone);
        Temperature(w, 0, 300); Temperature(w, 2, -50); double before = w.TotalEnergy();
        var config = Quiet(); config.ThermalDiffusion = 2f;
        new WorldSimulator(w, config).Step(.5f);
        Near(before, w.TotalEnergy(), .5, "heat conduction energy");
        Assert(w.Temperature(0) < 300 && w.Temperature(2) > -50, "heat did not transfer");
    }

    static void TestDirectDamping()
    {
        var w = Empty(1, 1, 1); SetAmbient(w, 20); Heat(w, 0, 40000); double before = w.TotalEnergy();
        var config = Quiet(); config.NaturalCoupling = .5f;
        var sim = new WorldSimulator(w, config); sim.Step(.2f);
        Assert(w.TotalEnergy() < before && w.Cells[0].StoredMana == 0, "direct damping absent");
        Assert(sim.Stats.DirectManifestedMana > 0 && sim.Stats.StoredManifestedMana == 0, "wrong resource path");
        Near(before + sim.Stats.NaturalEnergyJoules, w.TotalEnergy(), .001, "natural energy ledger");
    }

    static void TestCoherence()
    {
        var connected = Empty(3, 1, 1); SetAmbient(connected, 100);
        for (int i = 0; i < 3; i++) Heat(connected, i, 100000);
        double before = connected.TotalEnergy(); var config = Quiet(); config.NaturalCoupling = .5f;
        var sim = new WorldSimulator(connected, config); sim.Step(.1f);
        Assert(connected.TotalEnergy() > before && sim.Stats.ActivePhenomena == 1, "large coherent event failed to amplify");
        var separated = Empty(3, 1, 1); SetAmbient(separated, 100); Heat(separated, 0, 100000); Heat(separated, 2, 100000);
        double separateBefore = separated.TotalEnergy(); var other = new WorldSimulator(separated, config); other.Step(.1f);
        Assert(separated.TotalEnergy() < separateBefore && other.Stats.ActivePhenomena == 2, "unconnected events incorrectly merged");
    }

    static void TestColdInteraction()
    {
        var small = Empty(1, 1, 1); small.SetMaterial(0, 0, 0, MaterialId.Stone); SetAmbient(small, 100);
        Heat(small, 0, -50000); double before = small.TotalEnergy(); var config = Quiet(); config.NaturalCoupling = .5f;
        var s = new WorldSimulator(small, config); s.Step(.1f);
        Assert(small.TotalEnergy() > before && small.Temperature(0) < 20 && s.Stats.NaturalEnergyJoules > 0,
            "small cold event did not relax toward room temperature");
        Assert(s.Stats.StabilizedJoules > 0 && s.Stats.ManifestedMana > 0, "cold stabilization had no mana cost");
        var large = Empty(1, 1, 1); large.SetMaterial(0, 0, 0, MaterialId.Stone); SetAmbient(large, 100);
        Heat(large, 0, -300000); before = large.TotalEnergy();
        var l = new WorldSimulator(large, config); l.Step(.1f);
        Assert(large.TotalEnergy() < before && large.Cells[0].Energy >= 0 && l.Stats.NaturalEnergyJoules < 0,
            "large cold event did not amplify toward lower temperature");
        Assert(l.Stats.AmplifiedJoules > 0 && l.Stats.ManifestedMana > 0, "cold amplification had no mana cost");
    }

    static void TestStoredPath()
    {
        var w = Empty(1, 1, 1); w.SetMaterial(0, 0, 0, MaterialId.Crystal); Stored(w, 0, 20); Heat(w, 0, 40000);
        var config = Quiet(); config.NaturalCoupling = .5f;
        var sim = new WorldSimulator(w, config); sim.Step(.1f);
        Assert(w.Cells[0].StoredMana < 20 && sim.Stats.StoredManifestedMana > 0 && sim.Stats.DirectManifestedMana == 0,
            "stored-only manifestation failed");
    }

    static void TestManaAbsent()
    {
        var w = Empty(1, 1, 1); Heat(w, 0, 500000); double before = w.TotalEnergy();
        var config = Quiet(); config.NaturalCoupling = .5f; new WorldSimulator(w, config).Step(.3f);
        Near(before, w.TotalEnergy(), 0, "effect produced without mana");
    }

    static void TestManaLedger()
    {
        var w = Empty(4, 3, 4); SetAmbient(w, 15);
        w.SetMaterial(1, 1, 1, MaterialId.Crystal); w.SetMaterial(2, 1, 1, MaterialId.Iron);
        Heat(w, w.Index(1, 1, 1), 40000); Heat(w, w.Index(2, 1, 1), 280000);
        var config = new SimulationConfig { EnableCombustion = false, EnableFluidFlow = false };
        double mana = w.TotalMana(), energy = w.TotalEnergy(); var sim = new WorldSimulator(w, config);
        for (int i = 0; i < 30; i++) sim.Step(.05f);
        Near(mana + sim.Stats.GeneratedMana - sim.Stats.ManifestedMana, w.TotalMana(), .02, "combined mana ledger");
        Near(energy + sim.Stats.NaturalEnergyJoules, w.TotalEnergy(), 3, "combined energy ledger");
        foreach (Cell c in w.Cells) Assert(c.AmbientMana >= 0 && c.StoredMana >= 0 && c.Energy >= 0, "negative resource");
    }

    static void TestAtomicFailure()
    {
        var a = Empty(); var b = Empty(); var sim = new WorldSimulator(a, Quiet());
        var r = sim.Cast(new SpellCommand { Operation = SpellOperation.Heat, X = 1, Y = 1, Z = 1, Radius = 1, Power = 100000 });
        Assert(!r.Success, "unfunded cast accepted"); EqualCells(a, b);
        Assert(!sim.Cast(new SpellCommand { Operation = SpellOperation.Build, X = 1, Y = 1, Z = 1, Radius = 100 }).Success, "unbounded radius accepted");
        Assert(!sim.Cast(new SpellCommand { Operation = SpellOperation.Heat, X = 1, Y = 1, Z = 1, Power = float.NaN }).Success, "NaN accepted");
        Assert(!sim.Cast(new SpellCommand { Operation = (SpellOperation)123, X = 1, Y = 1, Z = 1 }).Success, "unknown operation accepted");
        EqualCells(a, b);
    }

    static void TestThermalSpell()
    {
        var w = Empty(); w.CasterMana = 1000; double energy = w.TotalEnergy(), mana = w.TotalMana();
        var sim = new WorldSimulator(w, Quiet());
        var r = sim.Cast(new SpellCommand { Operation = SpellOperation.Heat, X = 1, Y = 1, Z = 1, Radius = 1, Power = 100000 });
        Assert(r.Success, r.Message); Near(100000, w.TotalEnergy() - energy, .1, "power multiplied by target count");
        Near(mana - r.ManaCost, w.TotalMana(), .001, "spell mana debit");
        Near(100000, sim.Stats.SpellJoules, .1, "spell energy ledger");
        var second = sim.Cast(new SpellCommand { Operation = SpellOperation.Cool, X = 1, Y = 1, Z = 1, Radius = 1, Power = 100000 });
        Assert(second.Success, second.Message); Near(energy, w.TotalEnergy(), .2, "cooling total");
    }

    static void TestCharge()
    {
        var w = Empty(1, 1, 1); w.SetMaterial(0, 0, 0, MaterialId.Crystal); w.CasterMana = 100;
        double mana = w.TotalMana(); var sim = new WorldSimulator(w, Quiet());
        var r = sim.Cast(new SpellCommand { Operation = SpellOperation.Charge, Radius = 1, Power = 20 });
        Assert(r.Success, r.Message); Near(20, w.Cells[0].StoredMana, .0001, "charge store");
        Near(22, r.ManaCost, .0001, "charge total budget"); Near(mana - 2, w.TotalMana(), .0001, "transferred mana destroyed");
        Near(2, sim.Stats.ManifestedMana, .0001, "charge incorrectly counted as manifestation");
    }

    static void TestTransmute()
    {
        var w = Empty(1, 1, 1); w.SetMaterial(0, 0, 0, MaterialId.Iron); Stored(w, 0, 10); w.CasterMana = 100;
        var sim = new WorldSimulator(w, Quiet()); var command = new SpellCommand { Operation = SpellOperation.Transmute };
        double before = w.TotalEnergy(), mana = w.TotalMana();
        Assert(!sim.Cast(command).Success, "cold iron transmuted"); Near(before, w.TotalEnergy(), 0, "failed transmutation mutated energy");
        Near(mana, w.TotalMana(), 0, "failed transmutation spent mana");
        Temperature(w, 0, 300); float mass = w.Cells[0].Mass; before = w.TotalEnergy();
        Assert(sim.Cast(command).Success, "valid transmutation failed");
        Assert(w.Cells[0].Material == MaterialId.Alloy && w.Cells[0].Mass == mass, "material/mass result wrong");
        Near(4, w.Cells[0].StoredMana, .0001, "stored mana not consumed");
        Near(before + 9600, w.TotalEnergy(), .1, "reaction energy accounting");
        Near(mana - 16, w.TotalMana(), .001, "alchemy consumed wrong total mana");
    }

    static void TestCreativeEdits()
    {
        var w = Empty(1, 1, 1); w.SetMaterial(0, 0, 0, MaterialId.Crystal); Stored(w, 0, 40); w.CasterMana = 100;
        var sim = new WorldSimulator(w, Quiet()); double mana = w.TotalMana(), energy = w.TotalEnergy(), mass = w.TotalMass();
        var r = sim.Cast(new SpellCommand { Operation = SpellOperation.Excavate }); Assert(r.Success, r.Message);
        Near(mana - r.ManaCost, w.TotalMana(), .001, "excavation destroyed trapped mana");
        Near(w.TotalMass() - mass, sim.Stats.SandboxMassKg, .00001, "missing material ledger");
        Near(w.TotalEnergy() - energy, sim.Stats.SandboxEnergyJoules, .01, "missing terrain energy ledger");
        Assert(w.Cells[0].Material == MaterialId.Air && w.Cells[0].AmbientMana > 39, "stored mana not released");
        Assert(sim.Cast(new SpellCommand { Operation = SpellOperation.Build, Material = MaterialId.Wood }).Success, "build failed");
        Assert(w.Cells[0].Material == MaterialId.Wood && w.Cells[0].Fuel > 0, "wood source lacks fuel");
    }

    static void TestPhases()
    {
        var w = Empty(1, 1, 1); w.SetMaterial(0, 0, 0, MaterialId.Water); Stored(w, 0, 2);
        var config = Quiet(); config.EnablePhaseChanges = true; var sim = new WorldSimulator(w, config);
        Cell c = w.Cells[0]; c.Energy = c.Mass * (MaterialCatalog.WaterAtBoiling + 1000000f); w.Cells[0] = c;
        sim.Step(.05f); Assert(w.Cells[0].Material == MaterialId.Water, "boiled before latent heat supplied");
        Near(100, w.Temperature(0), .001, "latent plateau not maintained");
        c = w.Cells[0]; c.Energy = c.Mass * (MaterialCatalog.SteamAtBoiling + 2010f * 5); w.Cells[0] = c;
        double energy = w.TotalEnergy(), mass = w.TotalMass(), mana = w.TotalMana(); sim.Step(.05f);
        Assert(w.Cells[0].Material == MaterialId.Steam, "water did not become steam"); Near(105, w.Temperature(0), .001, "steam temperature");
        Near(energy, w.TotalEnergy(), 0, "phase created energy"); Near(mass, w.TotalMass(), 0, "phase created mass");
        Near(mana, w.TotalMana(), .001, "phase lost overflow mana");
        c = w.Cells[0]; c.Energy = c.Mass * MaterialCatalog.IceAtZero - 5000; w.Cells[0] = c; sim.Step(.05f);
        Assert(w.Cells[0].Material == MaterialId.Ice && w.Temperature(0) < 0, "water family did not freeze");
    }

    static void TestFluidFlow()
    {
        var w = Empty();
        for (int i = 0; i < w.Cells.Length; i++) { Cell c = w.Cells[i]; c.AmbientMana = i; w.Cells[i] = c; }
        w.SetMaterial(1, 2, 1, MaterialId.Water); Stored(w, w.Index(1, 2, 1), 1); Temperature(w, w.Index(1, 2, 1), 80);
        double mass = w.TotalMass(), energy = w.TotalEnergy(), mana = w.TotalMana();
        var config = Quiet(); config.EnableFluidFlow = true; new WorldSimulator(w, config).Step(.05f);
        Assert(w.Cells[w.Index(1, 1, 1)].Material == MaterialId.Water, "water did not fall one cell");
        for (int i = 0; i < w.Cells.Length; i++) Near(i, w.Cells[i].AmbientMana, 0, "ambient mana moved with matter");
        Near(mass, w.TotalMass(), .00001, "flow mass"); Near(energy, w.TotalEnergy(), .01, "flow energy"); Near(mana, w.TotalMana(), .001, "flow mana");
    }

    static void TestCombustion()
    {
        var w = Empty(1, 1, 1); w.SetMaterial(0, 0, 0, MaterialId.Wood); Temperature(w, 0, 300);
        float fuel = w.Cells[0].Fuel; double energy = w.TotalEnergy(), mass = w.TotalMass();
        var config = Quiet(); config.EnableCombustion = true; var sim = new WorldSimulator(w, config); sim.Step(.5f);
        Assert(w.Cells[0].Fuel < fuel && w.Cells[0].Fuel >= 0, "fuel did not decrease");
        Near(energy + sim.Stats.CombustionJoules, w.TotalEnergy(), .1, "burn ledger");
        Near((fuel - w.Cells[0].Fuel) * 13000000, sim.Stats.CombustionJoules, 3, "chemical fuel release mismatch");
        Near(mass, w.TotalMass(), 0, "coarse combustion lost tracked residue mass");
    }

    static void TestPersistence()
    {
        string folder = Path.Combine(Path.GetTempPath(), "manaweave-core-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, "world.mwld");
        try
        {
            var w = WorldGenerator.Generate(24, 16, 24, 77); var sim = new WorldSimulator(w); sim.RunExperiment(1); sim.Step(.1f);
            WorldPersistence.Save(w, file); EqualCells(w, WorldPersistence.Load(file));
            sim.Step(.1f); WorldPersistence.Save(w, file); EqualCells(w, WorldPersistence.Load(file));
            byte[] original = File.ReadAllBytes(file); Cell c = w.Cells[0], old = c; c.Energy = float.NaN; w.Cells[0] = c;
            Throws<InvalidDataException>(() => WorldPersistence.Save(w, file)); w.Cells[0] = old;
            byte[] intact = File.ReadAllBytes(file); Assert(original.Length == intact.Length, "failed save damaged existing file");
            for (int i = 0; i < original.Length; i++) Assert(original[i] == intact[i], "failed save changed existing bytes");
            intact[intact.Length - 1] ^= 1; File.WriteAllBytes(file, intact);
            Throws<InvalidDataException>(() => WorldPersistence.Load(file));
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 }); Throws<InvalidDataException>(() => WorldPersistence.Load(file));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    static void TestReplay()
    {
        var a = WorldGenerator.Generate(24, 16, 24, 734); var b = WorldGenerator.Generate(24, 16, 24, 734);
        var sa = new WorldSimulator(a); var sb = new WorldSimulator(b); sa.RunExperiment(1); sb.RunExperiment(1);
        for (int i = 0; i < 20; i++) { sa.Step(.05f); sb.Step(.05f); }
        EqualCells(a, b);
    }

    static void TestInvalidParameters()
    {
        var w = Empty(); var config = Quiet(); var sim = new WorldSimulator(w, config);
        Throws<ArgumentOutOfRangeException>(() => sim.Step(float.NaN));
        Throws<ArgumentOutOfRangeException>(() => sim.Step(-.1f));
        Throws<ArgumentOutOfRangeException>(() => sim.Step(2f));
        config.ManaGeneration = -1; Throws<ArgumentOutOfRangeException>(() => sim.Step(.1f));
        Assert(w.Tick == 0, "invalid tick mutated world");
    }

    static void TestExperiments()
    {
        var w = WorldGenerator.Generate(); var sim = new WorldSimulator(w);
        for (int id = 0; id < 4; id++)
        {
            double mass = w.TotalMass(), energy = w.TotalEnergy(), mana = w.TotalMana();
            double lm = sim.Stats.SandboxMassKg, le = sim.Stats.SandboxEnergyJoules, la = sim.Stats.ExperimentMana;
            sim.RunExperiment(id);
            Near(w.TotalMass() - mass, sim.Stats.SandboxMassKg - lm, .001, "fixture mass source");
            Near(w.TotalEnergy() - energy, sim.Stats.SandboxEnergyJoules - le, 1, "fixture energy source");
            Near(w.TotalMana() - mana, sim.Stats.ExperimentMana - la, .01, "fixture mana source");
            Assert(w.InBounds(sim.LastExperimentCenterX, sim.LastExperimentCenterY, sim.LastExperimentCenterZ), "demo location invalid");
        }
        int cell = w.Index(sim.LastExperimentCenterX, sim.LastExperimentCenterY, sim.LastExperimentCenterZ);
        Assert(w.Cells[cell].StoredMana == 0 && w.Temperature(cell) > 20, "direct demonstration isn't uncharged");
        sim.Step(.1f); Assert(sim.Stats.DirectManifestedMana > 0, "direct demonstration produced no direct manifestation");
    }

    static void ExportWorld(string path)
    {
        var w = WorldGenerator.Generate();
        string absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute));
        int count = 0;
        using (var output = new StreamWriter(absolute, false, new System.Text.UTF8Encoding(false)))
        {
            output.WriteLine("x,y,z,material,r,g,b,mass_kg,temperature_c,ambient_mu,stored_mu");
            for (int i = 0; i < w.Cells.Length; i++)
            {
                Cell c = w.Cells[i]; if (c.Material == MaterialId.Air || c.Material == MaterialId.Steam) continue;
                int x, y, z; w.Coordinates(i, out x, out y, out z);
                MaterialDefinition m = MaterialCatalog.Get(c.Material);
                output.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R}",
                    x, y, z, c.Material, m.ColorR, m.ColorG, m.ColorB, c.Mass, w.Temperature(i), c.AmbientMana, c.StoredMana));
                count++;
            }
        }
        Console.WriteLine("Exported seed=" + w.Seed + " dimensions=" + w.Width + "x" + w.Height + "x" + w.Depth + " visible_cells=" + count + " path=" + absolute);
    }
}
