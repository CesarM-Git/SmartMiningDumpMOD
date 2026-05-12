using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Mine;
using Mafi.Core.Console;
using Mafi.Core.Entities;
using Mafi.Core.Game;
using Mafi.Core.GameLoop;
using Mafi.Core.Mods;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;
using Mafi.Core.Vehicles.Jobs;
using Mafi.Core.Vehicles.Trucks;
using Mafi.Core.Vehicles.Trucks.JobProviders;
using Mafi.Localization;
using Mafi.Unity.Ui;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
// Disambiguate Label between Mafi.Unity.UiToolkit.Library.Label and System.Reflection.Emit.Label.
using Label = Mafi.Unity.UiToolkit.Library.Label;

namespace SmartMiningDumpMod;

/// <summary>
/// Mod that adds a "Prefer Dumping" toggle to mining towers. When enabled, trucks
/// assigned to that tower will dump materials at the tower's dumping designations
/// INSTEAD of delivering to assigned export storages.
///
/// Mechanism: wraps each assigned truck's m_jobProvider with a DumpFirstWrapper
/// that intercepts TryGetJobFor. The wrapper checks the toggle preference and, if
/// ON and the truck's cargo is in the tower's DumpableProducts, calls the dump-job
/// factory directly. On success, the wrapper short-circuits vanilla logic — the
/// storage-export priority in MineTowerTruckJobProvider.tryGetExcavatorDeliveryJob
/// (which puts AssignedInputStorages above the local-dump path) never runs.
/// On failure (dump factory rejects), the wrapper falls through to the vanilla
/// provider so trucks don't get stuck.
///
/// The wrapper is swapped out of m_jobProvider before every save (BeforeSave) and
/// swapped back in (UpdateAfterSync), so save files never contain our custom type
/// and remain loadable without the mod. A reconcile pass on UpdateStart handles
/// truck reassignments and despawns.
/// </summary>
public sealed class SmartMiningDumpMod : IMod, IDisposable
{
    public ModManifest Manifest { get; }
    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig { get; set; }
    public ModJsonConfig JsonConfig { get; }

    // ── Resolved dependencies ───────────────────────────────────────────
    private DependencyResolver m_resolver;
    private EntitiesManager m_entitiesManager;
    private IGameLoopEvents m_gameLoopEvents;
    private ISimLoopEvents m_simLoopEvents;

    // ── Cached reflection FieldInfos ────────────────────────────────────
    private static readonly FieldInfo s_trucksField = typeof(MineTower)
        .GetField("m_trucks", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo s_trucksJobProviderField = typeof(MineTower)
        .GetField("m_trucksJobProvider", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo s_contextField = typeof(TruckJobProviderBase)
        .GetField("Context", BindingFlags.NonPublic | BindingFlags.Instance);

    // ── Dump job factory (resolved once) ────────────────────────────────
    private object m_dumpJobFactory;       // DumpingJob.Factory instance
    private MethodInfo m_tryCreateDumpJob; // TryCreateAndEnqueueJob method

    // ── Cached tower list for dump job factory calls ────────────────────
    private Lyst<MineTower> m_towerCache = new Lyst<MineTower>();

    // ── Sim event delegates (stored for unsubscribe) ────────────────────
    private Action m_updateStartAction;
    private Action m_beforeSaveAction;
    private Action m_updateAfterSyncAction;

    // ── Singleton accessor (used by the inspector toggle callback to call
    //    wrap/unwrap when the user flips a tower's preference) ────────────
    public static SmartMiningDumpMod Instance { get; private set; }

    // ── Provider-wrapper bookkeeping ────────────────────────────────────
    // Tracks every truck whose m_jobProvider we've swapped to a DumpFirstWrapper.
    // Key: truck.Id.Value. Value: the wrapper currently installed (which holds a
    // reference to the original vanilla MineTowerTruckJobProvider for swap-back).
    //
    // Two reasons we need this:
    //  1. Save-safety: BeforeSave unwraps every entry (swaps back to vanilla
    //     provider) so the save file never contains our custom type. The wrapper
    //     would otherwise be serialized by Option<IJobProvider<Truck>>.Serialize
    //     → WriteGeneric, which writes the runtime type name, making the save
    //     un-loadable without our mod (or fragile to deserialize even with it).
    //  2. Truck reassignment: when MineTower.UnassignVehicle fires it calls
    //     truck.ResetJobProvider(), wiping our wrapper. The periodic reconcile
    //     pass uses this map to detect stale entries and re-wrap as needed.
    private readonly Dictionary<int, DumpFirstWrapper> m_activeWrappers
        = new Dictionary<int, DumpFirstWrapper>();

    // ── Diagnostics ─────────────────────────────────────────────────────
    // Towers we've logged a summary for at least once this session. Prevents
    // OnUpdateStart from spamming the log: we summarize each tower only the
    // first time we see it as toggled-ON during a session.
    private readonly HashSet<int> m_summarizedTowerIds = new HashSet<int>();
    // (tower.Id, truck.Id) pairs we've logged a dump-attempt verdict for at
    // least once. Prevents per-tick spam — each pair logs its first outcome,
    // and only re-logs if the outcome changes (tracked separately below).
    private readonly Dictionary<long, string> m_lastAttemptOutcome
        = new Dictionary<long, string>();

    // ═══════════════════════════════════════════════════════════════════
    // IMod lifecycle
    // ═══════════════════════════════════════════════════════════════════

    public SmartMiningDumpMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
        Instance = this;
    }

    public void RegisterPrototypes(ProtoRegistrator registrator) { }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded) { }

    public void EarlyInit(DependencyResolver resolver) { }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
    {
        m_resolver = resolver;
        Log.Info("SmartMiningDumpMOD: Initialize called.");

        try
        {
            m_entitiesManager = resolver.Resolve<EntitiesManager>();
            m_gameLoopEvents = resolver.Resolve<IGameLoopEvents>();
            m_simLoopEvents = resolver.Resolve<ISimLoopEvents>();
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: Failed to resolve core dependencies: {ex.Message}");
            return;
        }

        // Create preferences manager (per-save file)
        try
        {
            var gameNameConfig = resolver.Resolve<GameNameConfig>();
            string safeName = SanitizeFileName(gameNameConfig.GameName);
            string savePath = System.IO.Path.Combine(
                Manifest.RootDirectoryPath, $"dump_prefs_{safeName}.json");
            DumpPreferenceManager.Instance = new DumpPreferenceManager(savePath);
            Log.Info($"SmartMiningDumpMOD: Prefs file for '{gameNameConfig.GameName}': {savePath}");
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: Failed to create DumpPreferenceManager: {ex.Message}");
            DumpPreferenceManager.Instance = new DumpPreferenceManager(
                System.IO.Path.Combine(Manifest.RootDirectoryPath, "dump_prefs_default.json"));
        }

        // Defer sim-loop subscription, dump factory resolution, and inspector
        // patching to InitState (after InstantiateAllAndLock, all singletons are
        // fully available — and patching the inspector here means we run AFTER
        // every other mod's Initialize, so if another mod also replaced the
        // MineTower inspector via this same dict-swap pattern we can read their
        // type and STACK on top of it rather than overwriting them).
        m_gameLoopEvents.RegisterInitState(this, OnInitState);
    }

    /// <summary>
    /// Runs after InstantiateAllAndLock() — all dependencies are frozen.
    /// </summary>
    private void OnInitState()
    {
        PatchMineTowerInspector();
        ResolveDumpJobFactory();
        CacheReflectionForHotPath();
        SubscribeSimEvents();
        RegisterConsoleCommands();
        Log.Info("SmartMiningDumpMOD: InitState complete. All systems ready.");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Core dump-first logic (sim-loop interception)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the DumpingJob.Factory from the first available MineTower's
    /// TruckJobProviderContext. We can't reference the type at compile time
    /// (it may be internal), so we use reflection.
    /// </summary>
    private void ResolveDumpJobFactory()
    {
        try
        {
            // Find any MineTower to extract its provider context
            var towers = m_entitiesManager.GetAllEntitiesOfType<MineTower>().ToList();
            if (towers.Count == 0)
            {
                Log.Warning("SmartMiningDumpMOD: No MineTowers found. Will retry on first UpdateStart.");
                return;
            }

            ExtractDumpFactoryFromTower(towers[0]);
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: Failed to resolve DumpJobFactory: {ex}");
        }
    }

    private bool ExtractDumpFactoryFromTower(MineTower tower)
    {
        if (m_dumpJobFactory != null) return true;

        try
        {
            object provider = s_trucksJobProviderField.GetValue(tower);
            if (provider == null)
            {
                Log.Warning("SmartMiningDumpMOD: TrucksJobProvider is null on tower.");
                return false;
            }

            object context = s_contextField.GetValue(provider);
            if (context == null)
            {
                Log.Warning("SmartMiningDumpMOD: TruckJobProviderContext is null.");
                return false;
            }

            // TruckJobProviderContext.DumpJobFactory
            var dumpFactoryProp = context.GetType().GetProperty("DumpJobFactory",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (dumpFactoryProp == null)
            {
                // Try as field
                var dumpFactoryField = context.GetType().GetField("DumpJobFactory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (dumpFactoryField != null)
                    m_dumpJobFactory = dumpFactoryField.GetValue(context);
            }
            else
            {
                m_dumpJobFactory = dumpFactoryProp.GetValue(context);
            }

            if (m_dumpJobFactory == null)
            {
                Log.Error("SmartMiningDumpMOD: DumpJobFactory resolved as null!");
                return false;
            }

            // Find TryCreateAndEnqueueJob method
            // Signature: bool TryCreateAndEnqueueJob(Truck, Option<ProductProto>, ulong?, bool, IIndexable<MineTower>)
            foreach (var method in m_dumpJobFactory.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "TryCreateAndEnqueueJob") continue;
                var parms = method.GetParameters();
                if (parms.Length >= 3 && parms[0].ParameterType == typeof(Truck))
                {
                    m_tryCreateDumpJob = method;
                    Log.Info($"SmartMiningDumpMOD: Found DumpJobFactory.TryCreateAndEnqueueJob with {parms.Length} params.");
                    break;
                }
            }

            if (m_tryCreateDumpJob == null)
            {
                Log.Error("SmartMiningDumpMOD: Could not find TryCreateAndEnqueueJob method!");
                return false;
            }

            Log.Info("SmartMiningDumpMOD: DumpJobFactory resolved successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: ExtractDumpFactoryFromTower failed: {ex}");
            return false;
        }
    }

    private void SubscribeSimEvents()
    {
        // UpdateStart: periodic reconcile (wrap toggled-tower trucks, unwrap stale).
        m_updateStartAction = OnUpdateStart;
        m_simLoopEvents.UpdateStart.AddNonSaveable(this, m_updateStartAction);

        // BeforeSave: swap every wrapper back to its inner vanilla provider so the
        // save file only contains types that exist in vanilla Mafi. Critical for
        // save compatibility — both mod-uninstall and cross-version load.
        m_beforeSaveAction = OnBeforeSave;
        m_simLoopEvents.BeforeSave.AddNonSaveable(this, m_beforeSaveAction);

        // UpdateAfterSync: just after the save finishes (sync phase ends),
        // re-wrap toggled-tower trucks so the dump-first behavior resumes.
        m_updateAfterSyncAction = OnUpdateAfterSync;
        m_simLoopEvents.UpdateAfterSync.AddNonSaveable(this, m_updateAfterSyncAction);

        Log.Info("SmartMiningDumpMOD: Subscribed to UpdateStart, BeforeSave, UpdateAfterSync.");
    }

    // ── Cached reflection for dump job invocation (resolved once) ──────
    private ParameterInfo[] m_dumpJobParams;
    private MethodInfo m_deactivateCannotDeliver;
    private MethodInfo m_implicitOptionOp;
    private bool m_reflectionCached;

    /// <summary>
    /// One-time cache of reflection members used in the hot path.
    /// </summary>
    private void CacheReflectionForHotPath()
    {
        if (m_reflectionCached) return;
        m_reflectionCached = true;

        if (m_tryCreateDumpJob != null)
            m_dumpJobParams = m_tryCreateDumpJob.GetParameters();

        m_deactivateCannotDeliver = typeof(Truck).GetMethod("DeactivateCannotDeliver",
            BindingFlags.Public | BindingFlags.Instance);

        // Cache Option<ProductProto> implicit conversion
        if (m_dumpJobParams != null && m_dumpJobParams.Length >= 2)
        {
            var paramType = m_dumpJobParams[1].ParameterType;
            if (paramType != typeof(ProductProto))
            {
                m_implicitOptionOp = paramType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(ProductProto) }, null);
            }
        }
    }

    /// <summary>
    /// Fires before each sim step. Used to reconcile our provider-wrappers against
    /// the actual tower↔truck assignments (handles trucks that get reassigned
    /// between towers, towers that change toggle state, despawned trucks, etc.).
    /// </summary>
    private void OnUpdateStart()
    {
        if (DumpPreferenceManager.Instance == null) return;

        // Lazy-resolve factory if we didn't have towers at init time. Without
        // the factory we can't construct any wrappers, so we skip until we can.
        if (m_dumpJobFactory == null)
        {
            var anyTower = m_entitiesManager.GetAllEntitiesOfType<MineTower>().FirstOrDefault();
            if (anyTower == null) return;
            if (!ExtractDumpFactoryFromTower(anyTower)) return;
            CacheReflectionForHotPath();
        }

        ReconcileWrappers();
    }

    /// <summary>
    /// Hooked to <see cref="ISimLoopEvents.BeforeSave"/>. Swaps every wrapped truck's
    /// provider back to its inner vanilla <see cref="MineTowerTruckJobProvider"/>
    /// so the save file's <c>Option&lt;IJobProvider&lt;Truck&gt;&gt;</c> only references
    /// vanilla types. Critical: if our wrapper ends up in the save it makes the
    /// save un-loadable without our mod (and brittle to future game updates).
    /// </summary>
    private void OnBeforeSave()
    {
        if (m_activeWrappers.Count == 0) return;
        int unwrappedCount = 0;
        foreach (var kv in m_activeWrappers)
        {
            try
            {
                var truck = TryGetTruckById(kv.Key);
                if (truck == null) continue;
                truck.ResetJobProvider();
                truck.SetJobProvider(kv.Value.InnerProvider);
                unwrappedCount++;
            }
            catch (Exception ex)
            {
                Log.Warning($"SmartMiningDumpMOD: OnBeforeSave swap-out failed for truck {kv.Key}: {ex.Message}");
            }
        }
        Log.Info($"SmartMiningDumpMOD: BeforeSave — swapped {unwrappedCount} truck(s) back to vanilla provider.");
        // Note: we do NOT clear m_activeWrappers here. The map still tracks
        // which trucks we WANT wrapped; OnUpdateAfterSync re-installs them.
    }

    /// <summary>
    /// Hooked to <see cref="ISimLoopEvents.UpdateAfterSync"/>. Restores the wrappers
    /// we swapped out in OnBeforeSave so the dump-first behavior resumes after the
    /// game returns to the normal sim loop.
    /// </summary>
    private void OnUpdateAfterSync()
    {
        if (m_activeWrappers.Count == 0) return;
        int rewrappedCount = 0;
        foreach (var kv in m_activeWrappers)
        {
            try
            {
                var truck = TryGetTruckById(kv.Key);
                if (truck == null) continue;
                // Only rewrap if the truck's current provider is the inner vanilla
                // one (i.e., what we swapped to in BeforeSave). If something else
                // changed the provider in between, leave it alone — the reconcile
                // pass will fix it up.
                truck.ResetJobProvider();
                truck.SetJobProvider(kv.Value);
                rewrappedCount++;
            }
            catch (Exception ex)
            {
                Log.Warning($"SmartMiningDumpMOD: OnUpdateAfterSync rewrap failed for truck {kv.Key}: {ex.Message}");
            }
        }
        if (rewrappedCount > 0)
            Log.Info($"SmartMiningDumpMOD: UpdateAfterSync — rewrapped {rewrappedCount} truck(s).");
    }

    /// <summary>
    /// Walks every MineTower with toggle-ON and ensures all its assigned trucks
    /// have our DumpFirstWrapper installed. Also unwraps any stale entries
    /// (truck moved to a non-toggled tower, tower toggled off, truck despawned).
    /// </summary>
    private void ReconcileWrappers()
    {
        // Pass 1: wrap any unwrapped trucks on toggled towers.
        foreach (MineTower tower in m_entitiesManager.GetAllEntitiesOfType<MineTower>())
        {
            if (!DumpPreferenceManager.Instance.IsToggled(tower.Id)) continue;

            if (m_summarizedTowerIds.Add(tower.Id.Value))
                LogTowerSummary(tower);

            var allVehicles = tower.AllVehicles;
            int vehicleCount = allVehicles.Count;
            for (int i = 0; i < vehicleCount; i++)
            {
                if (!(allVehicles[i] is Truck truck)) continue;
                int truckId = truck.Id.Value;

                // Already wrapped for THIS tower? Skip.
                if (m_activeWrappers.TryGetValue(truckId, out var existing) && existing.Tower == tower)
                    continue;

                // Wrapped for a different tower (truck just moved)? Unwrap first.
                if (existing != null)
                    UnwrapTruck(truck);

                WrapTruck(truck, tower);
            }
        }

        // Pass 2: unwrap stale entries (truck no longer in any toggled tower's
        // assigned list, truck despawned, etc.).
        if (m_activeWrappers.Count > 0)
        {
            List<int> stale = null;
            foreach (var kv in m_activeWrappers)
            {
                var tower = kv.Value.Tower;
                bool towerToggled = DumpPreferenceManager.Instance.IsToggled(tower.Id);
                bool stillAssigned = false;
                if (towerToggled)
                {
                    var allVehicles = tower.AllVehicles;
                    for (int i = 0; i < allVehicles.Count; i++)
                    {
                        if (allVehicles[i] is Truck t && t.Id.Value == kv.Key)
                        {
                            stillAssigned = true;
                            break;
                        }
                    }
                }
                if (!stillAssigned)
                    (stale ?? (stale = new List<int>())).Add(kv.Key);
            }
            if (stale != null)
            {
                foreach (int truckId in stale)
                {
                    var truck = TryGetTruckById(truckId);
                    if (truck != null)
                        UnwrapTruck(truck);
                    else
                        m_activeWrappers.Remove(truckId); // truck despawned, just drop the entry
                }
            }
        }
    }

    /// <summary>
    /// Installs a <see cref="DumpFirstWrapper"/> on the given truck. The wrapper
    /// remembers the original vanilla provider (read from the truck's current
    /// m_jobProvider) so we can swap back cleanly later.
    /// </summary>
    private void WrapTruck(Truck truck, MineTower tower)
    {
        try
        {
            // Read current provider via reflection. We use a freshly-fetched value
            // rather than tower.m_trucksJobProvider because in some edge cases the
            // truck might transiently have a different provider (default truck
            // provider during a reassignment race) — capturing the truck's actual
            // current provider keeps swap-back faithful.
            var currentProvider = GetTruckCurrentProvider(truck);
            if (currentProvider == null)
            {
                Log.Warning($"SmartMiningDumpMOD: WrapTruck — truck {truck.Id} has no current provider; skipping.");
                return;
            }
            if (currentProvider is DumpFirstWrapper)
            {
                // Already wrapped (defensive — should be caught by the reconcile filter).
                return;
            }

            var wrapper = new DumpFirstWrapper(tower, currentProvider, this);
            truck.ResetJobProvider();
            truck.SetJobProvider(wrapper);
            m_activeWrappers[truck.Id.Value] = wrapper;
            RecordOutcome(tower, truck, "WRAPPED with DumpFirstWrapper");
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: WrapTruck failed for truck {truck.Id} on tower {tower.Id}: {ex.Message}");
        }
    }

    /// <summary>Removes our wrapper from a truck, restoring the inner vanilla provider.</summary>
    private void UnwrapTruck(Truck truck)
    {
        int truckId = truck.Id.Value;
        if (!m_activeWrappers.TryGetValue(truckId, out var wrapper))
            return;
        try
        {
            // Only swap-back if the truck's provider is still our wrapper. If
            // something else (e.g. onTruckUnassigned) already reset it, just drop
            // our map entry.
            var current = GetTruckCurrentProvider(truck);
            if (current == wrapper)
            {
                truck.ResetJobProvider();
                truck.SetJobProvider(wrapper.InnerProvider);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: UnwrapTruck failed for truck {truck.Id}: {ex.Message}");
        }
        m_activeWrappers.Remove(truckId);
    }

    /// <summary>
    /// Called from the inspector toggle's OnValueChanged callback (via
    /// <see cref="Instance"/>) when the user flips a tower's preference. Wraps
    /// (or unwraps) all the tower's assigned trucks immediately so the change
    /// takes effect on the next job request, not on the next reconcile tick.
    /// </summary>
    public void OnTogglePreferenceChanged(MineTower tower, bool nowOn)
    {
        try
        {
            if (m_dumpJobFactory == null && !ExtractDumpFactoryFromTower(tower)) return;
            if (!m_reflectionCached) CacheReflectionForHotPath();

            var allVehicles = tower.AllVehicles;
            int wrapped = 0, unwrapped = 0;
            for (int i = 0; i < allVehicles.Count; i++)
            {
                if (!(allVehicles[i] is Truck truck)) continue;
                if (nowOn)
                {
                    if (!m_activeWrappers.ContainsKey(truck.Id.Value))
                    {
                        WrapTruck(truck, tower);
                        wrapped++;
                    }
                }
                else
                {
                    if (m_activeWrappers.ContainsKey(truck.Id.Value))
                    {
                        UnwrapTruck(truck);
                        unwrapped++;
                    }
                }
            }
            Log.Info($"SmartMiningDumpMOD: Tower {tower.Id} preference {(nowOn ? "ON" : "OFF")} — " +
                $"wrapped={wrapped}, unwrapped={unwrapped}.");
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: OnTogglePreferenceChanged failed: {ex.Message}");
        }
    }

    /// <summary>Reads a truck's current m_jobProvider via reflection (the field is private).</summary>
    private static readonly FieldInfo s_truckJobProviderField = typeof(Truck)
        .GetField("m_jobProvider", BindingFlags.NonPublic | BindingFlags.Instance);

    private static IJobProvider<Truck> GetTruckCurrentProvider(Truck truck)
    {
        if (s_truckJobProviderField == null) return null;
        var opt = s_truckJobProviderField.GetValue(truck);
        // opt is Option<IJobProvider<Truck>>. Use reflection to read .ValueOrNull.
        var valueOrNullProp = opt.GetType().GetProperty("ValueOrNull",
            BindingFlags.Public | BindingFlags.Instance);
        return valueOrNullProp?.GetValue(opt) as IJobProvider<Truck>;
    }

    private Truck TryGetTruckById(int truckId)
    {
        // EntitiesManager doesn't expose a clean ById<T> lookup we know of; scan.
        // Trucks count is small (hundreds at most), called rarely (save/sync events).
        foreach (var truck in m_entitiesManager.GetAllEntitiesOfType<Truck>())
        {
            if (truck.Id.Value == truckId) return truck;
        }
        return null;
    }

    private void LogTowerSummary(MineTower tower)
    {
        try
        {
            int vehicleCount = tower.AllVehicles.Count;
            int truckCount = 0;
            for (int i = 0; i < vehicleCount; i++)
                if (tower.AllVehicles[i] is Truck) truckCount++;

            string dumpableProducts = string.Join(", ",
                tower.DumpableProducts.Select(p => p.Id.Value));
            if (string.IsNullOrEmpty(dumpableProducts)) dumpableProducts = "<none>";

            Log.Info($"SmartMiningDumpMOD: Tower {tower.Id} summary on first toggle-ON encounter: " +
                $"IsEnabled={tower.IsEnabled}, " +
                $"DumpingDesignations={tower.ManagedDumpingDesignations.Count}, " +
                $"Vehicles={vehicleCount} (Trucks={truckCount}), " +
                $"DumpableProducts=[{dumpableProducts}], " +
                $"AssignedInputTowers={tower.AssignedInputTowers.Count}.");
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: LogTowerSummary failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs a (tower, truck) outcome only when the outcome string changes from the
    /// previously-recorded one. This naturally rate-limits — a truck stuck in
    /// "HasJobs=true" logs once and stays silent until it transitions.
    /// </summary>
    private void RecordOutcome(MineTower tower, Truck truck, string outcome)
    {
        long key = ((long)tower.Id.Value << 32) | (uint)truck.Id.Value;
        if (m_lastAttemptOutcome.TryGetValue(key, out string prev) && prev == outcome) return;
        m_lastAttemptOutcome[key] = outcome;
        Log.Info($"SmartMiningDumpMOD: Tower {tower.Id} truck {truck.Id}: {outcome}.");
    }

    /// <summary>
    /// Called from <see cref="DumpFirstWrapper.TryGetJobFor"/> when the toggle is ON.
    /// Attempts to enqueue a dump job for the truck's cargo. Returns true on success
    /// (the wrapper then returns true to short-circuit vanilla's TryGetJobFor — the
    /// storage-export priority is never consulted). On false, the wrapper falls
    /// through to vanilla and the truck delivers to storage as it normally would.
    ///
    /// Filters:
    /// - Cargo must be non-empty (defensive, the wrapper already checks).
    /// - Cargo product must be in the tower's DumpableProducts (per-tower config).
    /// </summary>
    internal bool TryEnqueueDumpJob(Truck truck, MineTower tower)
    {
        // Mine trucks typically carry one product type — use FirstOrPhantom (no allocation)
        var first = truck.Cargo.FirstOrPhantom;
        if (first.IsEmpty)
        {
            RecordOutcome(tower, truck, "SKIP: Cargo became empty (race)");
            return false;
        }

        ProductProto product = first.Product;

        if (!tower.DumpableProducts.Contains(product))
        {
            RecordOutcome(tower, truck,
                $"SKIP: cargo '{product.Id.Value}' not in tower's DumpableProducts " +
                $"(count={tower.DumpableProducts.Count})");
            return false;
        }

        // Build tower cache — restrict dump to this tower's zone + assigned input towers.
        // Mirrors vanilla MineTowerTruckJobProvider.tryDumpAllCargoInAssignedTowersOrSelf.
        m_towerCache.Clear();
        m_towerCache.Add(tower);
        var inputTowers = tower.AssignedInputTowers;
        int inputCount = inputTowers.Count;
        for (int i = 0; i < inputCount; i++)
            m_towerCache.Add(inputTowers[i]);

        bool success = InvokeDumpFactory(truck, product);
        RecordOutcome(tower, truck,
            success
                ? $"DUMP ASSIGNED for '{product.Id.Value}' (cargo qty={first.Quantity.Value})"
                : $"DUMP FAILED: factory returned false for '{product.Id.Value}' " +
                  $"(towersInScope={m_towerCache.Count}, dumpDesigs={tower.ManagedDumpingDesignations.Count})");

        if (success)
        {
            truck.DumpingOfAllCargoPending = true;
            m_deactivateCannotDeliver?.Invoke(truck, null);
        }
        return success;
    }

    /// <summary>
    /// Calls DumpJobFactory.TryCreateAndEnqueueJob via cached reflection.
    /// Returns true if a dump job was successfully created and enqueued.
    /// </summary>
    private bool InvokeDumpFactory(Truck truck, ProductProto product)
    {
        if (m_tryCreateDumpJob == null || m_dumpJobParams == null) return false;

        try
        {
            object[] args;

            if (m_dumpJobParams.Length >= 5)
            {
                // (Truck, Option<ProductProto>, ulong?, bool, IIndexable<MineTower>)
                object productArg = (m_implicitOptionOp != null)
                    ? m_implicitOptionOp.Invoke(null, new object[] { product })
                    : (object)product;
                args = new object[] { truck, productArg, null, false, m_towerCache };
            }
            else
            {
                // (Truck, ProductProto, ulong?)
                args = new object[] { truck, product, truck.ZoneMask };
            }

            object result = m_tryCreateDumpJob.Invoke(m_dumpJobFactory, args);
            return result is bool b && b;
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: DumpJob creation failed for {product.Id}: {ex.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Inspector patching (adds toggle to MineTower inspector)
    // ═══════════════════════════════════════════════════════════════════

    private void PatchMineTowerInspector()
    {
        try
        {
            // Find vanilla MineTowerInspector type at runtime (it's internal in Mafi.Unity)
            Type mineTowerInspectorType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                mineTowerInspectorType = asm.GetType("Mafi.Unity.Ui.Inspectors.MineTowerInspector");
                if (mineTowerInspectorType != null) break;
            }

            if (mineTowerInspectorType == null)
            {
                Log.Error("SmartMiningDumpMOD: MineTowerInspector type not found!");
                return;
            }

            // Resolve InspectorsManager and its private inspector-type dictionary
            var inspectorsManager = m_resolver.Resolve<InspectorsManager>();
            FieldInfo dictField = typeof(InspectorsManager).GetField("m_inspectorsImplTypes",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (dictField == null)
            {
                Log.Error("SmartMiningDumpMOD: m_inspectorsImplTypes field not found!");
                return;
            }

            object dict = dictField.GetValue(inspectorsManager);
            Type dictType = dict.GetType();

            // ── Compatibility: stack on top of any prior replacement ───────
            // If another mod has already swapped the MineTower inspector via this same
            // m_inspectorsImplTypes dict (the only way to do "type replacement"), pick
            // up THEIR type and use it as our base. Result is a chain
            //   OurRuntime -> TheirRuntime -> MineTowerInspector -> BaseInspector<MineTower>
            // so both mods' ctor bodies (and any Harmony postfixes anywhere in the
            // chain) run, both UIs render, and we don't silently overwrite them.
            // If their type is incompatible (sealed / different ctor signature),
            // ChooseStackingBase returns null and we leave the dict untouched —
            // we'd rather lose our toggle than wipe out their UI.
            Type currentlyRegistered = ReadRegisteredInspectorType(dict, typeof(MineTower));
            Type baseToExtend = ChooseStackingBase(mineTowerInspectorType, currentlyRegistered);
            if (baseToExtend == null)
            {
                Log.Error("SmartMiningDumpMOD: Cannot safely patch inspector — leaving " +
                    "the existing registration alone. Smart-dump toggle will not be visible.");
                return;
            }

            // Build dynamic subclass on top of the chosen base
            Type concreteType = BuildDynamicInspectorType(baseToExtend);
            if (concreteType == null)
            {
                Log.Error("SmartMiningDumpMOD: Failed to build dynamic inspector type.");
                return;
            }

            // Patch InspectorsManager — overwrite the entry with our stacking type
            PropertyInfo indexer = dictType.GetProperty("Item");
            if (indexer != null)
            {
                indexer.SetValue(dict, concreteType, new object[] { typeof(MineTower) });
                Log.Info($"SmartMiningDumpMOD: Patched InspectorsManager for MineTower. " +
                    $"Chain: {concreteType.FullName} -> {baseToExtend.FullName}");
            }
            else
            {
                MethodInfo removeMethod = dictType.GetMethod("Remove", new[] { typeof(Type) });
                MethodInfo addMethod = dictType.GetMethod("Add", new[] { typeof(Type), typeof(Type) });
                if (removeMethod != null && addMethod != null)
                {
                    removeMethod.Invoke(dict, new object[] { typeof(MineTower) });
                    addMethod.Invoke(dict, new object[] { typeof(MineTower), concreteType });
                    Log.Info($"SmartMiningDumpMOD: Patched InspectorsManager via Remove+Add. " +
                        $"Chain: {concreteType.FullName} -> {baseToExtend.FullName}");
                }
                else
                {
                    Log.Error("SmartMiningDumpMOD: Could not find indexer or Remove/Add on dict.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: Inspector patching failed: {ex}");
        }
    }

    /// <summary>
    /// Reads the currently-registered inspector type for the given entity type from
    /// InspectorsManager.m_inspectorsImplTypes. Returns null if no entry exists or
    /// if the dict's API doesn't expose a way to read it without throwing.
    /// </summary>
    private static Type ReadRegisteredInspectorType(object dict, Type entityType)
    {
        try
        {
            Type dictType = dict.GetType();

            // Prefer TryGetValue (Mafi.Dict and System.Generic.Dictionary both expose it)
            // so we don't throw on a missing key.
            MethodInfo tryGet = dictType.GetMethod("TryGetValue");
            if (tryGet != null)
            {
                ParameterInfo[] parms = tryGet.GetParameters();
                if (parms.Length == 2)
                {
                    object[] args = new object[] { entityType, null };
                    object found = tryGet.Invoke(dict, args);
                    if (found is bool b && b)
                        return args[1] as Type;
                    return null;
                }
            }

            // Fall back to ContainsKey + indexer
            MethodInfo containsKey = dictType.GetMethod("ContainsKey", new[] { typeof(Type) });
            if (containsKey != null)
            {
                bool has = (bool)containsKey.Invoke(dict, new object[] { entityType });
                if (!has) return null;
            }

            PropertyInfo indexer = dictType.GetProperty("Item");
            if (indexer != null)
                return indexer.GetValue(dict, new object[] { entityType }) as Type;

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: ReadRegisteredInspectorType failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Picks the right base class for our IL-emitted runtime subclass.
    /// - If nothing is registered or the registered type is vanilla MineTowerInspector,
    ///   return vanilla.
    /// - If the registered type is a subclass of MineTowerInspector with a matching
    ///   ctor signature, return THAT so we stack on top of it.
    /// - If the registered type is incompatible (not a MineTowerInspector subclass,
    ///   sealed, or has a different ctor signature than vanilla), return null. The
    ///   caller MUST treat this as "do not patch" — extending vanilla and swapping
    ///   would overwrite the other mod's registration and erase their UI.
    /// </summary>
    private static Type ChooseStackingBase(Type vanillaInspectorType, Type currentlyRegistered)
    {
        if (currentlyRegistered == null || currentlyRegistered == vanillaInspectorType)
        {
            Log.Info("SmartMiningDumpMOD: Extending vanilla MineTowerInspector " +
                "(no prior replacement detected).");
            return vanillaInspectorType;
        }

        if (!vanillaInspectorType.IsAssignableFrom(currentlyRegistered))
        {
            Log.Warning($"SmartMiningDumpMOD: Registered inspector '{currentlyRegistered.FullName}' " +
                "is not a MineTowerInspector subclass — refusing to patch (would erase the " +
                "registered type's behavior).");
            return null;
        }

        if (currentlyRegistered.IsSealed)
        {
            Log.Warning($"SmartMiningDumpMOD: Stacking target '{currentlyRegistered.FullName}' " +
                "is sealed and cannot be subclassed — refusing to patch (would erase its UI).");
            return null;
        }

        // Verify the stacking target's ctor signature matches vanilla's, since our
        // IL-emitted ctor in BuildDynamicInspectorType blindly forwards Ldarg_0..N.
        ConstructorInfo vanillaCtor = vanillaInspectorType
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault();
        ConstructorInfo theirCtor = currentlyRegistered
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault();

        if (vanillaCtor != null && theirCtor != null)
        {
            Type[] vanillaParams = vanillaCtor.GetParameters().Select(p => p.ParameterType).ToArray();
            Type[] theirParams = theirCtor.GetParameters().Select(p => p.ParameterType).ToArray();
            if (!vanillaParams.SequenceEqual(theirParams))
            {
                Log.Warning($"SmartMiningDumpMOD: Stacking target '{currentlyRegistered.FullName}' " +
                    "has a different ctor signature than vanilla — refusing to patch " +
                    "(IL-emitted ctor only forwards the vanilla parameter list).");
                return null;
            }
        }

        Log.Info($"SmartMiningDumpMOD: Found prior inspector replacement " +
            $"'{currentlyRegistered.FullName}' — stacking on top of it.");
        return currentlyRegistered;
    }

    /// <summary>
    /// Creates a dynamic subclass of MineTowerInspector via IL emit.
    /// The constructor calls base ctor, then our static AddDumpPreferenceToggle method.
    /// Also overrides OnActivated to sync toggle state when switching entities.
    /// </summary>
    private Type BuildDynamicInspectorType(Type baseInspectorType)
    {
        try
        {
            ConstructorInfo baseCtor = baseInspectorType
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault();

            if (baseCtor == null)
            {
                Log.Error("SmartMiningDumpMOD: No constructor found on MineTowerInspector!");
                return null;
            }

            Type[] paramTypes = baseCtor.GetParameters().Select(p => p.ParameterType).ToArray();
            Log.Info($"SmartMiningDumpMOD: MineTowerInspector ctor has {paramTypes.Length} params: " +
                string.Join(", ", paramTypes.Select(t => t.Name)));

            // MineTowerInspector is `internal class` in Mafi.Unity. Earlier attempts
            // emitted IL that called the internal base ctor directly via `call`. That
            // hits the JIT's cross-assembly access check and throws MethodAccessException.
            // Unity's Mono fork does NOT honor IgnoresAccessChecksTo (tried both Run and
            // RunAndSave + Assembly.LoadFrom — both still threw).
            //
            // Solution: don't emit a `call` to the internal ctor at all. Our IL only
            // references public types (Object's ctor + static helpers in our own
            // assembly). The actual base-ctor invocation is done via reflection in
            // InvokeBaseCtor — reflection's access check operates on the *member*
            // (which is `public`), not the *type*'s visibility from the caller, so
            // a public ctor on an internal type IS reflectively invocable.
            //
            // The emitted IL technically violates "derived ctor must call its immediate
            // base ctor", but Mono doesn't verify dynamic-assembly IL in Run mode — it
            // JITs whatever bytecode we hand it. The end result is identical: by the
            // time AddDumpPreferenceToggle runs, MineTowerInspector's ctor body has
            // initialized all inherited fields (via the reflective Invoke).
            var assemblyName = new AssemblyName("SmartMiningDumpMOD.Dynamic");
            AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
                assemblyName, AssemblyBuilderAccess.Run);
            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");

            TypeBuilder typeBuilder = moduleBuilder.DefineType(
                "SmartMiningDumpMOD.SmartMineTowerInspector_Runtime",
                TypeAttributes.Public | TypeAttributes.Class,
                baseInspectorType);

            // ── Constructor ──
            // The emitted IL avoids any `call` to the internal MineTowerInspector..ctor.
            // Instead it:
            //   1. Calls Object..ctor() (always public, always accessible).
            //   2. Packs args into object[] and calls InvokeBaseCtor(this, args),
            //      which reflectively invokes the internal base ctor on `this`.
            //      Reflection.Invoke uses *member*-level access checks, not type-level,
            //      so the public-ctor-on-internal-type call succeeds where IL `call` fails.
            //   3. Calls AddDumpPreferenceToggle(this) to wire up the toggle UI.
            //
            // OnActivated is NOT overridden anymore — extending an internal type's
            // protected method has the same problem as the ctor, and we don't need
            // an override anyway: AddDumpPreferenceToggle now uses ObserveValue() to
            // keep the toggle's visual state reactive to the inspector's current entity.
            ConstructorBuilder ctorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                paramTypes);

            MethodInfo addToggleMethod = typeof(SmartMiningDumpMod).GetMethod(
                nameof(AddDumpPreferenceToggle),
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo invokeBaseCtorMethod = typeof(SmartMiningDumpMod).GetMethod(
                nameof(InvokeBaseCtor),
                BindingFlags.Public | BindingFlags.Static);
            ConstructorInfo objectCtor = typeof(object).GetConstructor(Type.EmptyTypes);

            ILGenerator ctorIl = ctorBuilder.GetILGenerator();

            // 1. Call Object..ctor() on `this`.
            //    Unverifiable (we're skipping over MineTowerInspector's ctor) but Mono
            //    accepts it under AssemblyBuilderAccess.Run.
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Call, objectCtor);

            // 2. Call InvokeBaseCtor(this, new object[] { arg1, arg2, ... }).
            ctorIl.Emit(OpCodes.Ldarg_0);                       // arg 0 of InvokeBaseCtor: instance
            ctorIl.Emit(OpCodes.Ldc_I4, paramTypes.Length);
            ctorIl.Emit(OpCodes.Newarr, typeof(object));        // new object[N]
            for (int i = 0; i < paramTypes.Length; i++)
            {
                ctorIl.Emit(OpCodes.Dup);
                ctorIl.Emit(OpCodes.Ldc_I4, i);
                ctorIl.Emit(OpCodes.Ldarg_S, (byte)(i + 1));
                // All ctor params are reference types (UiContext, TowerAreasRenderer,
                // AssignedBuildingsHighlighter, BuildingsAssigner,
                // NewInstanceOf<PolygonAreaSelectionController>) — no Box needed.
                ctorIl.Emit(OpCodes.Stelem_Ref);
            }
            ctorIl.Emit(OpCodes.Call, invokeBaseCtorMethod);

            // 3. Call AddDumpPreferenceToggle(this).
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Call, addToggleMethod);

            ctorIl.Emit(OpCodes.Ret);

            return typeBuilder.CreateType();
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: BuildDynamicInspectorType failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Reflectively invokes the base inspector's ctor on the already-allocated
    /// <paramref name="instance"/>. Called from the IL-emitted subclass ctor in
    /// place of an unverifiable `call MineTowerInspector..ctor` (which would throw
    /// MethodAccessException since MineTowerInspector is `internal class`).
    ///
    /// Why this works: Mono's reflection access check looks at the member's
    /// declared accessibility (the ctor is `public`), not the type's accessibility
    /// from the caller's assembly. So a public ctor on an internal type can be
    /// invoked via reflection from another assembly even though it can't be
    /// reached via a direct IL `call` instruction.
    ///
    /// ConstructorInfo.Invoke(object, object[]) — when the first argument is
    /// non-null — invokes the ctor body on the given instance rather than
    /// allocating a new one. This is the same pattern .NET's deserialization
    /// frameworks use after FormatterServices.GetUninitializedObject.
    /// </summary>
    public static void InvokeBaseCtor(object instance, object[] args)
    {
        var baseType = instance.GetType().BaseType;
        var ctor = baseType
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault();
        if (ctor == null)
        {
            Log.Error($"SmartMiningDumpMOD: InvokeBaseCtor — no ctor on {baseType.FullName}.");
            return;
        }
        ctor.Invoke(instance, args);
    }

    /// <summary>
    /// Called from the dynamic inspector's constructor. Adds a "Prefer Dumping" toggle
    /// panel to the inspector. Uses reflection to call protected AddPanelRow.
    /// </summary>
    public static void AddDumpPreferenceToggle(object inspector)
    {
        try
        {
            // Find AddPanelRow in the type hierarchy. BaseInspector has TWO
            // overloads: (Action<Row>, params UiComponent[]) and (params UiComponent[]).
            // We want the first one — predicate selects it by checking the 2-arg
            // signature where the second arg is an array.
            MethodInfo addPanelRow = FindMethodInHierarchy(inspector.GetType(),
                "AddPanelRow",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length == 2 && ps[1].ParameterType.IsArray;
                });

            // Find AddPanelWithHeader as fallback (no overload disambiguation needed).
            MethodInfo addPanelWithHeader = null;
            if (addPanelRow == null)
            {
                addPanelWithHeader = FindMethodInHierarchy(inspector.GetType(),
                    "AddPanelWithHeader",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            }

            if (addPanelRow == null && addPanelWithHeader == null)
            {
                Log.Error("SmartMiningDumpMOD: Neither AddPanelRow nor AddPanelWithHeader found!");
                return;
            }

            // Create a Toggle component.
            // Toggle's only constructor is Toggle(bool standalone). The label is set
            // via the .Label(LocStrFormatted) extension method on IComponentWithLabel.
            var toggle = new Toggle(standalone: true)
                .Label("Prefer Dumping over Storage".AsLoc())
                .Tooltip("When enabled, trucks will try to dump dumpable materials at dumping/leveling designations before delivering to storage.".AsLoc());

            // Wire up the toggle's value-changed callback (user → preference store +
            // immediate wrap/unwrap of the tower's assigned trucks).
            toggle.OnValueChanged(value =>
            {
                var entity = GetEntityFromInspector(inspector);
                if (entity != null && DumpPreferenceManager.Instance != null)
                {
                    DumpPreferenceManager.Instance.SetToggle(entity.Id, value);
                    string state = value ? "ON" : "OFF";
                    Log.Info($"SmartMiningDumpMOD: Tower {entity.Id} dump preference: {state}");
                    // Apply wrapping immediately so the change takes effect on the
                    // next job request, not on the next ReconcileWrappers tick.
                    SmartMiningDumpMod.Instance?.OnTogglePreferenceChanged(entity, value);
                }
            });

            // Reactive sync (preference store → toggle visual state). Re-evaluated by
            // the UI updater system every frame the inspector is visible, so when the
            // user clicks a different MineTower the toggle automatically updates to
            // that tower's stored preference. Replaces the old OnActivated override —
            // we can't override OnActivated on an internal base class.
            toggle.ObserveValue(() =>
            {
                var entity = GetEntityFromInspector(inspector);
                if (entity == null || DumpPreferenceManager.Instance == null) return false;
                return DumpPreferenceManager.Instance.IsToggled(entity.Id);
            });

            // Create a header label
            var label = new Label("Smart Dump".AsLoc())
                .TextAlign(TextAlignment.LeftMiddle)
                .FontStyle(FontStyle.Bold)
                .FlexGrow(1f);

            // Add the panel
            if (addPanelRow != null)
            {
                // AddPanelRow(Action<Row>, params UiComponent[])
                var parms = addPanelRow.GetParameters();
                if (parms.Length == 2 && parms[1].ParameterType.IsArray)
                {
                    Action<Row> rowConfig = row =>
                    {
                        // Try JustifyItemsSpaceBetween — may not exist on Row
                        try
                        {
                            var justify = row.GetType().GetMethod("JustifyItemsSpaceBetween");
                            justify?.Invoke(row, null);
                        }
                        catch { }
                    };

                    // First row: header
                    var headerComponents = new UiComponent[] { label };
                    addPanelRow.Invoke(inspector, new object[] { rowConfig, headerComponents });

                    // Second row: toggle
                    var toggleComponents = new UiComponent[] { toggle };
                    addPanelRow.Invoke(inspector, new object[] { rowConfig, toggleComponents });
                }
                else
                {
                    Log.Warning($"SmartMiningDumpMOD: AddPanelRow has unexpected signature: {parms.Length} params.");
                }
            }
            else if (addPanelWithHeader != null)
            {
                // AddPanelWithHeader(UiComponent content) — pass a Row with our toggle
                try
                {
                    var row = new Row { toggle };
                    addPanelWithHeader.Invoke(inspector, new object[] { row });
                }
                catch (Exception ex)
                {
                    Log.Warning($"SmartMiningDumpMOD: AddPanelWithHeader failed: {ex.Message}");
                }
            }

            Log.Info("SmartMiningDumpMOD: Toggle panel added to inspector.");
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: AddDumpPreferenceToggle failed: {ex}");
        }
    }

    /// <summary>
    /// Extracts the Entity property (MineTower) from the inspector instance.
    /// BaseInspector&lt;MineTower&gt; has a public Entity property.
    /// </summary>
    private static MineTower GetEntityFromInspector(object inspector)
    {
        try
        {
            var entityProp = FindPropertyInHierarchy(inspector.GetType(), "Entity",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (entityProp == null) return null;

            return entityProp.GetValue(inspector) as MineTower;
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Console commands
    // ═══════════════════════════════════════════════════════════════════

    private void RegisterConsoleCommands()
    {
        try
        {
            var executor = m_resolver.Resolve<GameConsoleCommandsExecutor>();
            int count = executor.ScanObjectForConsoleCommands(this, ignoreDuplicates: true);
            Log.Info($"SmartMiningDumpMOD: Registered {count} console command(s).");
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: Failed to register console commands: {ex.Message}");
        }
    }

    [ConsoleCommand(documentation: "Toggles dump preference on all mine towers. Usage: smart_dump_all")]
    private string SmartDumpAll()
    {
        if (DumpPreferenceManager.Instance == null)
            return "Error: DumpPreferenceManager not initialized.";

        var towers = m_entitiesManager.GetAllEntitiesOfType<MineTower>().ToList();
        if (towers.Count == 0)
            return "No mine towers found.";

        // Determine: if any are OFF, turn all ON. If all ON, turn all OFF.
        bool anyOff = towers.Any(t => !DumpPreferenceManager.Instance.IsToggled(t.Id));
        bool newState = anyOff;

        foreach (var tower in towers)
            DumpPreferenceManager.Instance.SetToggle(tower.Id, newState);

        string state = newState ? "ON" : "OFF";
        return $"Set dump preference {state} for {towers.Count} mine tower(s).";
    }

    [ConsoleCommand(documentation: "Shows dump preference status of all mine towers. Usage: smart_dump_status")]
    private string SmartDumpStatus()
    {
        if (DumpPreferenceManager.Instance == null)
            return "Error: DumpPreferenceManager not initialized.";

        var towers = m_entitiesManager.GetAllEntitiesOfType<MineTower>().ToList();
        if (towers.Count == 0)
            return "No mine towers found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Mine Tower Dump Preference Status ({towers.Count} towers):");
        foreach (var tower in towers)
        {
            bool isOn = DumpPreferenceManager.Instance.IsToggled(tower.Id);
            int dumpDesigs = tower.ManagedDumpingDesignations.Count;
            int dumpProducts = tower.DumpableProducts.Count;
            string status = isOn ? "ON " : "OFF";
            sb.AppendLine($"  Tower {tower.Id}: [{status}] DumpDesigns={dumpDesigs} DumpProducts={dumpProducts}");
        }
        return sb.ToString();
    }

    [ConsoleCommand(documentation: "Per-tower-and-truck diagnostic dump for ALL toggled-ON mine towers. Shows IsEnabled, dump designations, DumpableProducts, AssignedInputTowers, and per-assigned-truck state (HasJobs, Cargo, IsEnabled). Run this WHILE trucks are misbehaving so we can see which condition is failing. Usage: smart_dump_diag")]
    private string SmartDumpDiag()
    {
        if (DumpPreferenceManager.Instance == null)
            return "Error: DumpPreferenceManager not initialized.";

        var towers = m_entitiesManager.GetAllEntitiesOfType<MineTower>()
            .Where(t => DumpPreferenceManager.Instance.IsToggled(t.Id))
            .ToList();
        if (towers.Count == 0)
            return "No toggled-ON mine towers found. Toggle one in its inspector first.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Smart Dump Diagnostics ({towers.Count} toggled tower(s)) ===");
        foreach (var tower in towers)
        {
            string dumpableProducts = string.Join(", ",
                tower.DumpableProducts.Select(p => p.Id.Value));
            if (string.IsNullOrEmpty(dumpableProducts)) dumpableProducts = "<none>";

            sb.AppendLine();
            sb.AppendLine($"Tower {tower.Id} [ON]:");
            sb.AppendLine($"  IsEnabled:            {tower.IsEnabled}");
            sb.AppendLine($"  DumpingDesignations:  {tower.ManagedDumpingDesignations.Count}");
            sb.AppendLine($"  DumpableProducts:     [{dumpableProducts}]");
            sb.AppendLine($"  AssignedInputTowers:  {tower.AssignedInputTowers.Count}");
            sb.AppendLine($"  AssignedVehicles:     {tower.AllVehicles.Count}");

            // Pre-filter reasons mirror those in OnUpdateStart — but here we print
            // for every truck so the user can spot patterns (all stuck on "HasJobs",
            // or all stuck on "Cargo not in DumpableProducts", etc.).
            int vehicleCount = tower.AllVehicles.Count;
            int truckIdx = 0;
            for (int i = 0; i < vehicleCount; i++)
            {
                if (!(tower.AllVehicles[i] is Truck truck)) continue;
                truckIdx++;

                string cargoDesc;
                ProductProto cargoProduct = null;
                if (truck.Cargo.IsEmpty)
                {
                    cargoDesc = "<empty>";
                }
                else
                {
                    var first = truck.Cargo.FirstOrPhantom;
                    cargoDesc = first.IsEmpty ? "<empty>" :
                        $"{first.Product.Id.Value} qty={first.Quantity.Value}";
                    if (!first.IsEmpty) cargoProduct = first.Product;
                }

                // The new model is "is the wrapper installed on this truck?" — when
                // installed, our DumpFirstWrapper.TryGetJobFor intercepts the truck's
                // next job request and tries dump before vanilla storage logic.
                bool isWrapped = m_activeWrappers.ContainsKey(truck.Id.Value);

                string verdict;
                if (!isWrapped)                              verdict = "NOT WRAPPED (will run vanilla logic next request)";
                else if (truck.Cargo.IsEmpty)                verdict = "WRAPPED, empty cargo — next dump attempt on next loading";
                else if (cargoProduct == null)               verdict = "WRAPPED, cargo race";
                else if (!tower.DumpableProducts.Contains(cargoProduct))
                                                             verdict = "WRAPPED, but cargo NOT in DumpableProducts → wrapper will fall through to vanilla";
                else                                         verdict = "WRAPPED, cargo dumpable → wrapper will try dump on next job request";

                sb.AppendLine($"    Truck {truck.Id}: HasJobs={truck.HasJobs}, " +
                    $"IsEnabled={truck.IsEnabled}, Wrapped={isWrapped}, Cargo={cargoDesc} → {verdict}");
            }
            if (truckIdx == 0)
                sb.AppendLine("    (no trucks assigned)");
        }
        return sb.ToString();
    }

    [ConsoleCommand(documentation: "Toggles dump preference for a specific mine tower by ID. Usage: smart_dump <tower_entity_id>")]
    private string SmartDump(int towerId)
    {
        if (DumpPreferenceManager.Instance == null)
            return "Error: DumpPreferenceManager not initialized.";

        MineTower tower = null;
        foreach (var t in m_entitiesManager.GetAllEntitiesOfType<MineTower>())
        {
            if (t.Id.Value == towerId)
            {
                tower = t;
                break;
            }
        }

        if (tower == null)
            return $"No mine tower found with ID {towerId}. Use smart_dump_status to see all towers.";

        bool newState = DumpPreferenceManager.Instance.Toggle(tower.Id);
        string state = newState ? "ON" : "OFF";
        return $"Tower {tower.Id} dump preference: {state}";
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Walks the type hierarchy looking for a method by name. Avoids
    /// <see cref="Type.GetMethod(string, BindingFlags)"/> because that throws
    /// <see cref="AmbiguousMatchException"/> when the name has overloads — which
    /// is exactly the case for <c>BaseInspector.AddPanelRow</c> (2 overloads:
    /// <c>(Action&lt;Row&gt;, params UiComponent[])</c> and <c>(params UiComponent[])</c>).
    ///
    /// Pass a <paramref name="predicate"/> to disambiguate; the first matching
    /// method (by walking declaring-type → base-type) is returned.
    /// </summary>
    private static MethodInfo FindMethodInHierarchy(Type type, string name, BindingFlags flags,
        Func<MethodInfo, bool> predicate = null)
    {
        while (type != null)
        {
            foreach (var m in type.GetMethods(flags | BindingFlags.DeclaredOnly))
            {
                if (m.Name != name) continue;
                if (predicate == null || predicate(m)) return m;
            }
            type = type.BaseType;
        }
        return null;
    }

    private static PropertyInfo FindPropertyInHierarchy(Type type, string name, BindingFlags flags)
    {
        while (type != null)
        {
            var prop = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            if (prop != null) return prop;
            type = type.BaseType;
        }
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "default";
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    public void Dispose()
    {
        if (m_simLoopEvents != null && m_updateStartAction != null)
        {
            try
            {
                m_simLoopEvents.UpdateStart.RemoveNonSaveable(this, m_updateStartAction);
            }
            catch { }
        }
        if (m_simLoopEvents != null && m_beforeSaveAction != null)
        {
            try { m_simLoopEvents.BeforeSave.RemoveNonSaveable(this, m_beforeSaveAction); } catch { }
        }
        if (m_simLoopEvents != null && m_updateAfterSyncAction != null)
        {
            try { m_simLoopEvents.UpdateAfterSync.RemoveNonSaveable(this, m_updateAfterSyncAction); } catch { }
        }

        // Unwrap every truck on shutdown so the game returns to a clean state.
        // This is also what happens at save time (OnBeforeSave) but doing it here
        // covers the mod-disabled / mod-reload path too.
        foreach (var kv in m_activeWrappers)
        {
            try
            {
                var truck = TryGetTruckById(kv.Key);
                if (truck == null) continue;
                truck.ResetJobProvider();
                truck.SetJobProvider(kv.Value.InnerProvider);
            }
            catch { }
        }
        m_activeWrappers.Clear();

        if (Instance == this) Instance = null;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// DumpFirstWrapper
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Decorates a tower's <see cref="MineTowerTruckJobProvider"/> with a "try dump
/// first if the toggle is ON" behavior. Installed on a truck's m_jobProvider via
/// <see cref="SmartMiningDumpMod.WrapTruck"/>; the truck's normal
/// <c>tryGetJob()</c> path calls this wrapper's <see cref="TryGetJobFor"/>
/// instead of the vanilla provider.
///
/// IMPORTANT (save-safety): this type MUST NEVER appear in a save file. The
/// truck's m_jobProvider is serialized via <c>Option&lt;IJobProvider&lt;Truck&gt;&gt;.Serialize</c>
/// → <c>BlobWriter.WriteGeneric</c>, which writes the runtime type name. If our
/// wrapper is in there, loading the save requires our mod (and our exact type
/// fully-qualified name) — fragile across mod updates and broken if the mod is
/// uninstalled. <see cref="SmartMiningDumpMod.OnBeforeSave"/> unwraps all
/// trucks before save; <see cref="SmartMiningDumpMod.OnUpdateAfterSync"/>
/// re-wraps them after. The window between BeforeSave and the actual save
/// write is on the sim thread (single-threaded), so there's no race.
/// </summary>
internal sealed class DumpFirstWrapper : IJobProvider<Truck>
{
    /// <summary>The tower this wrapper was installed for. Used to look up the
    /// toggle preference and pass to <see cref="SmartMiningDumpMod.TryEnqueueDumpJob"/>.</summary>
    public MineTower Tower { get; }

    /// <summary>The original vanilla provider we're decorating. Saved here so
    /// <see cref="SmartMiningDumpMod.OnBeforeSave"/> can swap back to it, and
    /// <see cref="TryGetJobFor"/> can delegate when our dump path doesn't apply.</summary>
    public IJobProvider<Truck> InnerProvider { get; }

    private readonly SmartMiningDumpMod m_mod;

    public DumpFirstWrapper(MineTower tower, IJobProvider<Truck> inner, SmartMiningDumpMod mod)
    {
        Tower = tower;
        InnerProvider = inner;
        m_mod = mod;
    }

    public bool TryGetJobFor(Truck truck)
    {
        // Fast-path checks before the (somewhat-allocating) TryEnqueueDumpJob.
        if (DumpPreferenceManager.Instance != null
            && DumpPreferenceManager.Instance.IsToggled(Tower.Id)
            && Tower.IsEnabled
            && Tower.ManagedDumpingDesignations.Count > 0
            && truck.Cargo.IsNotEmpty
            && truck.IsEnabled)
        {
            if (m_mod.TryEnqueueDumpJob(truck, Tower))
                return true;
            // Fall through to vanilla if dump factory returned false — better to
            // deliver than to leave the truck stuck idle.
        }
        return InnerProvider.TryGetJobFor(truck);
    }
}
