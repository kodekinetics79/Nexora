namespace ERP_RFQ_Automation.Boq;

/// <summary>
/// The ~10 starter assemblies seeded lazily per business unit (electrical +
/// mechanical basics). Rates are deliberately conservative placeholders in the
/// tenant's base currency — every seeded row carries IsStarter = true and a
/// "Starter rate — review before quoting" description so tenants treat them as
/// editable defaults, not gospel. Seeding is idempotent: it only runs when the
/// tenant has NO assemblies at all (see BoqBuilderService.SeedStarterAssembliesAsync).
/// </summary>
public static class BoqStarterAssemblies
{
    public sealed record StarterComponent(string Description, string Unit, decimal QtyPer, string ItemType, decimal? DefaultRate);

    public sealed record StarterAssembly(
        string Code, string Name, string Description, string ServiceCategory, string Unit,
        IReadOnlyList<StarterComponent> Components);

    private const string StarterNote =
        "Starter assembly seeded by Nexora — review quantities and rates before quoting.";

    public static IReadOnlyList<StarterAssembly> All { get; } = new List<StarterAssembly>
    {
        new("DB-PANEL-250A", "Distribution panel 250A — supply & install", StarterNote, "electrical", "EA",
            new List<StarterComponent>
            {
                new("Distribution board 250A, 24-way, form 2", "EA", 1m, BoqItemType.Material, 1800m),
                new("MCCB 250A incomer", "EA", 1m, BoqItemType.Material, 350m),
                new("Panel accessories (busbar links, labels, glands)", "lot", 1m, BoqItemType.Material, 150m),
                new("Electrician — panel mounting & termination", "hr", 12m, BoqItemType.Labor, 45m),
                new("Point-to-point test & energization check", "lot", 1m, BoqItemType.Labor, 120m),
            }),

        new("CABLE-RUN-M", "LV cable run per meter (incl. tray, glands, labor)", StarterNote, "electrical", "m",
            new List<StarterComponent>
            {
                new("4-core LV cable (typ. 25mm²)", "m", 1.05m, BoqItemType.Material, 12m),
                new("Cable tray incl. supports", "m", 1m, BoqItemType.Material, 8m),
                new("Glands, lugs & fixings (prorated)", "m", 1m, BoqItemType.Material, 1.5m),
                new("Electrician — pulling & dressing", "hr", 0.25m, BoqItemType.Labor, 45m),
            }),

        new("MOTOR-INSTALL", "Motor installation & alignment (to 30 kW)", StarterNote, "mechanical", "EA",
            new List<StarterComponent>
            {
                new("Anchor bolts, shims & consumables", "lot", 1m, BoqItemType.Material, 90m),
                new("Millwright — setting & laser alignment", "hr", 8m, BoqItemType.Labor, 55m),
                new("Electrician — termination & rotation check", "hr", 4m, BoqItemType.Labor, 45m),
                new("Crane / lifting equipment", "hr", 2m, BoqItemType.Equipment, 120m),
            }),

        new("PUMP-OVERHAUL", "Centrifugal pump overhaul (medium duty)", StarterNote, "mechanical", "EA",
            new List<StarterComponent>
            {
                new("Overhaul kit (seals, bearings, gaskets)", "set", 1m, BoqItemType.Material, 650m),
                new("Mechanic — strip, inspect, rebuild", "hr", 16m, BoqItemType.Labor, 55m),
                new("Workshop machining allowance", "lot", 1m, BoqItemType.Subcontract, 300m),
                new("Post-overhaul performance test", "lot", 1m, BoqItemType.Labor, 150m),
            }),

        new("LIGHT-POINT", "Lighting point — supply & install", StarterNote, "electrical", "EA",
            new List<StarterComponent>
            {
                new("LED luminaire (industrial, typ. 36W)", "EA", 1m, BoqItemType.Material, 65m),
                new("Wiring, conduit & accessories per point", "lot", 1m, BoqItemType.Material, 25m),
                new("Electrician — installation & test", "hr", 1.5m, BoqItemType.Labor, 45m),
            }),

        new("PIPE-SB-M", "Small-bore piping per meter (to DN50, carbon steel)", StarterNote, "mechanical", "m",
            new List<StarterComponent>
            {
                new("CS pipe & fittings (prorated)", "m", 1.1m, BoqItemType.Material, 18m),
                new("Pipe supports & consumables", "m", 1m, BoqItemType.Material, 6m),
                new("Pipefitter/welder — fabricate & erect", "hr", 0.8m, BoqItemType.Labor, 55m),
            }),

        new("SCAFFOLD-M3", "Scaffolding erection & dismantling per m³", StarterNote, "civil", "m3",
            new List<StarterComponent>
            {
                new("Scaffold material hire (per m³, prorated)", "m3", 1m, BoqItemType.Equipment, 6m),
                new("Scaffolder crew — erect & dismantle", "hr", 0.5m, BoqItemType.Labor, 40m),
                new("Inspection & tagging", "lot", 0.02m, BoqItemType.Labor, 80m),
            }),

        new("TECH-DAY", "Technician day rate (site services)", StarterNote, "manpower", "day",
            new List<StarterComponent>
            {
                new("Qualified technician — 8h site day", "day", 1m, BoqItemType.Labor, 380m),
                new("Hand tools & PPE allowance", "day", 1m, BoqItemType.Equipment, 25m),
            }),

        new("TEST-CIRCUIT", "Electrical testing per circuit (IR, continuity, functional)", StarterNote, "electrical", "EA",
            new List<StarterComponent>
            {
                new("Test technician — per circuit", "hr", 0.75m, BoqItemType.Labor, 50m),
                new("Test instruments (prorated)", "EA", 1m, BoqItemType.Equipment, 10m),
                new("Test report & documentation (prorated)", "EA", 1m, BoqItemType.Labor, 8m),
            }),

        new("HVAC-SPLIT-INSTALL", "HVAC split unit installation (to 24k BTU)", StarterNote, "mechanical", "EA",
            new List<StarterComponent>
            {
                new("Refrigerant piping kit, brackets & drain", "set", 1m, BoqItemType.Material, 180m),
                new("HVAC technician — mount, vac & charge", "hr", 6m, BoqItemType.Labor, 50m),
                new("Electrician — power point & isolator", "hr", 2m, BoqItemType.Labor, 45m),
                new("Commissioning & airflow check", "lot", 1m, BoqItemType.Labor, 80m),
            }),
    };
}
