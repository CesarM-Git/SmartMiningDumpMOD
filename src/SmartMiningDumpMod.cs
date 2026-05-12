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
/// assigned to that tower will attempt to dump materials marked as dumpable in
/// dumping/leveling designations within the tower's zone BEFORE falling back to
/// storage delivery.
///
/// Mechanism: subscribes to ISimLoopEvents.UpdateStart which fires before each sim
/// step's Update(). When a truck finishes loading (has cargo, no pending jobs), our
/// handler runs before the truck's SimUpdateInternal can call TryGetJobFor, giving
/// us a one-tick window to pre-assign a dump job.
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

    // ── Inspector toggle sync ───────────────────────────────────────────
    /// <summary>
    /// Maps inspector instances to their Toggle components for state sync
    /// when the user selects a different MineTower.
    /// </summary>
    internal static readonly Dictionary<object, ToggleState> InspectorToggles
        = new Dictionary<object, ToggleState>();

    internal class ToggleState
    {
        public object Toggle;          // Mafi Toggle component
        public MethodInfo SetValueMethod;
    }

    // ═══════════════════════════════════════════════════════════════════
    // IMod lifecycle
    // ═══════════════════════════════════════════════════════════════════

    public SmartMiningDumpMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
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
        m_updateStartAction = OnUpdateStart;
        m_simLoopEvents.UpdateStart.AddNonSaveable(this, m_updateStartAction);
        Log.Info("SmartMiningDumpMOD: Subscribed to UpdateStart.");
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
    /// Fires before each sim step's Update(). Trucks that finished loading in the
    /// previous step now have cargo but no job — their SimUpdateInternal hasn't run yet.
    /// We pre-assign dump jobs for trucks on toggled towers.
    /// </summary>
    private void OnUpdateStart()
    {
        if (DumpPreferenceManager.Instance == null) return;
        if (DumpPreferenceManager.Instance.ToggledCount == 0) return;

        // Lazy-resolve factory if we didn't have towers at init time
        if (m_dumpJobFactory == null)
        {
            var anyTower = m_entitiesManager.GetAllEntitiesOfType<MineTower>().FirstOrDefault();
            if (anyTower == null) return;
            if (!ExtractDumpFactoryFromTower(anyTower)) return;
            CacheReflectionForHotPath();
        }

        foreach (MineTower tower in m_entitiesManager.GetAllEntitiesOfType<MineTower>())
        {
            if (!tower.IsEnabled) continue;
            if (!DumpPreferenceManager.Instance.IsToggled(tower.Id)) continue;
            if (tower.ManagedDumpingDesignations.Count == 0) continue;

            // Iterate assigned vehicles (AllVehicles is public, includes trucks + excavators)
            var allVehicles = tower.AllVehicles;
            int vehicleCount = allVehicles.Count;
            for (int i = 0; i < vehicleCount; i++)
            {
                if (!(allVehicles[i] is Truck truck)) continue;
                if (truck.HasJobs) continue;
                if (truck.Cargo.IsEmpty) continue;
                if (!truck.IsEnabled) continue;

                TryAssignDumpJob(truck, tower);
            }
        }
    }

    /// <summary>
    /// Attempts to create a dump job for the truck's cargo at the given tower's
    /// dumping designations. Only dumps products that are in the tower's DumpableProducts.
    /// Mine trucks typically carry a single product type, so we optimize for that case.
    /// </summary>
    private void TryAssignDumpJob(Truck truck, MineTower tower)
    {
        // Mine trucks typically carry one product type — use FirstOrPhantom (no allocation)
        var first = truck.Cargo.FirstOrPhantom;
        if (first.IsEmpty) return;

        ProductProto product = first.Product;

        // Only dump products that the tower has marked as dumpable
        if (!tower.DumpableProducts.Contains(product))
            return;

        // Build tower cache — restrict dump to this tower's zone + assigned input towers
        m_towerCache.Clear();
        m_towerCache.Add(tower);

        if (InvokeDumpFactory(truck, product))
        {
            truck.DumpingOfAllCargoPending = true;
            m_deactivateCannotDeliver?.Invoke(truck, null);
        }
        // If dump failed, truck will fall through to normal TryGetJobFor in SimUpdateInternal
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

            var assemblyName = new AssemblyName("SmartMiningDumpMOD.Dynamic");
            AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
                assemblyName, AssemblyBuilderAccess.Run);

            // MineTowerInspector is `internal class` in Mafi.Unity. The emitted ctor's
            // `call base..ctor` would otherwise throw MethodAccessException at invoke.
            // IgnoresAccessChecksTo on THIS (dynamic) assembly tells the Mono JIT to
            // skip the cross-assembly visibility check for calls into Mafi.Unity.
            var ignoreCtor = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute)
                .GetConstructor(new[] { typeof(string) });
            assemblyBuilder.SetCustomAttribute(
                new CustomAttributeBuilder(ignoreCtor, new object[] { "Mafi.Unity" }));

            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");

            TypeBuilder typeBuilder = moduleBuilder.DefineType(
                "SmartMiningDumpMOD.SmartMineTowerInspector_Runtime",
                TypeAttributes.Public | TypeAttributes.Class,
                baseInspectorType);

            // ── Constructor: call base ctor, then our static helper ──
            ConstructorBuilder ctorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                paramTypes);

            MethodInfo addToggleMethod = typeof(SmartMiningDumpMod).GetMethod(
                nameof(AddDumpPreferenceToggle),
                BindingFlags.Public | BindingFlags.Static);

            ILGenerator ctorIl = ctorBuilder.GetILGenerator();
            ctorIl.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < paramTypes.Length; i++)
                ctorIl.Emit(OpCodes.Ldarg_S, (byte)(i + 1));
            ctorIl.Emit(OpCodes.Call, baseCtor);

            // Call AddDumpPreferenceToggle(this)
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Call, addToggleMethod);
            ctorIl.Emit(OpCodes.Ret);

            // ── Override OnActivated: call base, then sync toggle ──
            MethodInfo baseOnActivated = baseInspectorType.GetMethod("OnActivated",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

            if (baseOnActivated != null)
            {
                MethodInfo syncMethod = typeof(SmartMiningDumpMod).GetMethod(
                    nameof(SyncInspectorToggle),
                    BindingFlags.Public | BindingFlags.Static);

                MethodBuilder onActivatedOverride = typeBuilder.DefineMethod("OnActivated",
                    MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    typeof(void), Type.EmptyTypes);

                ILGenerator oaIl = onActivatedOverride.GetILGenerator();
                oaIl.Emit(OpCodes.Ldarg_0);
                oaIl.Emit(OpCodes.Call, baseOnActivated);
                oaIl.Emit(OpCodes.Ldarg_0);
                oaIl.Emit(OpCodes.Call, syncMethod);
                oaIl.Emit(OpCodes.Ret);
            }
            else
            {
                Log.Warning("SmartMiningDumpMOD: OnActivated method not found; toggle won't sync on entity switch.");
            }

            return typeBuilder.CreateType();
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: BuildDynamicInspectorType failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Called from the dynamic inspector's constructor. Adds a "Prefer Dumping" toggle
    /// panel to the inspector. Uses reflection to call protected AddPanelRow.
    /// </summary>
    public static void AddDumpPreferenceToggle(object inspector)
    {
        try
        {
            // Find AddPanelRow in the type hierarchy
            MethodInfo addPanelRow = FindMethodInHierarchy(inspector.GetType(),
                "AddPanelRow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            // Find AddPanelWithHeader as fallback
            MethodInfo addPanelWithHeader = null;
            if (addPanelRow == null)
            {
                addPanelWithHeader = FindMethodInHierarchy(inspector.GetType(),
                    "AddPanelWithHeader", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
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

            // Wire up the toggle's value-changed callback
            toggle.OnValueChanged(value =>
            {
                var entity = GetEntityFromInspector(inspector);
                if (entity != null && DumpPreferenceManager.Instance != null)
                {
                    DumpPreferenceManager.Instance.SetToggle(entity.Id, value);
                    string state = value ? "ON" : "OFF";
                    Log.Info($"SmartMiningDumpMOD: Tower {entity.Id} dump preference: {state}");
                }
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

            // Store toggle reference for sync
            var setValueMethod = toggle.GetType().GetMethod("SetValue") ??
                                 toggle.GetType().GetMethod("set_Value");

            InspectorToggles[inspector] = new ToggleState
            {
                Toggle = toggle,
                SetValueMethod = setValueMethod
            };

            Log.Info("SmartMiningDumpMOD: Toggle panel added to inspector.");
        }
        catch (Exception ex)
        {
            Log.Error($"SmartMiningDumpMOD: AddDumpPreferenceToggle failed: {ex}");
        }
    }

    /// <summary>
    /// Called from the dynamic inspector's OnActivated override.
    /// Syncs the toggle state to match the currently-inspected MineTower.
    /// </summary>
    public static void SyncInspectorToggle(object inspector)
    {
        try
        {
            if (!InspectorToggles.TryGetValue(inspector, out var toggleState))
                return;

            var entity = GetEntityFromInspector(inspector);
            if (entity == null || DumpPreferenceManager.Instance == null)
                return;

            bool isToggled = DumpPreferenceManager.Instance.IsToggled(entity.Id);

            // Try SetValue(bool) or SetWithoutNotify(bool)
            if (toggleState.SetValueMethod != null)
            {
                toggleState.SetValueMethod.Invoke(toggleState.Toggle, new object[] { isToggled });
            }
            else
            {
                // Try SetWithoutNotify or similar
                var setWithout = toggleState.Toggle.GetType().GetMethod("SetWithoutNotify");
                setWithout?.Invoke(toggleState.Toggle, new object[] { isToggled });
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: SyncInspectorToggle failed: {ex.Message}");
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

    private static MethodInfo FindMethodInHierarchy(Type type, string name, BindingFlags flags)
    {
        while (type != null)
        {
            var method = type.GetMethod(name, flags | BindingFlags.DeclaredOnly);
            if (method != null) return method;
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

        InspectorToggles.Clear();
    }
}
