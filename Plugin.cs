// Belmont FastLoad: shorter loading screens and save-mirror travel in Castlevania: Belmont's Curse.
//
//  AnimationSpeed          scales deltaTime passed to LoadingScreenTransitionAnimation.Update.
//  MirrorHoldSpeed         raises Time.timeScale while MC_Crouch_MirrorInteract_Tp runs (actions tick at a fixed
//                          rate, so the hold can only be sped up by running more ticks). Restored on release or
//                          when the level load starts.
//  MirrorEnterDelayFrames  lowers the start frame of the MC_Crouch feature that launches the mirror action.
//  KeepGameMenuLoaded      skips AFMSceneManager.UnloadScene for GameMenu when the next state also contains it;
//                          the load step reuses the open scene.
//
// Game updates must disable features, never crash: hooks are matched by exact signature, checked for shared
// native code (IL2CPP identical-code folding), and patched one by one; the raw memory read is enabled only after
// its offsets are verified; patch bodies catch all exceptions and fall back to vanilla behaviour.
// Never hook or call methods with struct parameters (Il2CppInterop corrupts them).
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BelmontFastLoad;

[BepInPlugin(Guid, "Belmont FastLoad", "0.8.0")]
[BepInDependency(BelmontUiGuid, BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BasePlugin
{
    internal const string BelmontUiGuid = "leandrobueno.belmontui";
    public const string Guid = "belmontfastload";
    internal static ManualLogSource L;
    internal static ConfigEntry<float> AnimationSpeed;
    internal static ConfigEntry<float> MirrorHoldSpeed;
    internal static ConfigEntry<int> MirrorEnterDelayFrames;
    internal static ConfigEntry<bool> KeepGameMenuLoaded;
    internal static ConfigEntry<bool> ForceNativeScan;

    public override void Load()
    {
        L = Log;
        AnimationSpeed = Config.Bind("Loading screen", "AnimationSpeed", 4f,
            "Multiplier for loading-screen animation playback speed. 1 = vanilla.");
        MirrorHoldSpeed = Config.Bind("Mirror", "MirrorHoldSpeed", 4f,
            "Game speed multiplier while holding down at a save mirror to enter it (the hold still cancels if released). 1 = vanilla.");
        MirrorEnterDelayFrames = Config.Bind("Mirror", "MirrorEnterDelayFrames", 8,
            new ConfigDescription("Crouch frames (30 per second) at a save mirror before the mirror-entry animation starts. Vanilla = 30.",
                new AcceptableValueRange<int>(1, 30)));
        KeepGameMenuLoaded = Config.Bind("Loading (experimental)", "KeepGameMenuLoaded", false,
            "Keep the in-game menu scene (pause/map/inventory) loaded between levels instead of reloading it on every " +
            "level change. Saves ~1 s per loading screen. Experimental: check the map after travelling between biomes.");
        ForceNativeScan = Config.Bind("Debug", "ForceNativeScan", false,
            "Testing only: behave as if the game had been updated (scan native code before hooking).");

        try { Setup(); }
        catch (Exception e)
        {
            Features.DisableAll();
            L.LogError("FastLoad setup failed; all features disabled, the game runs unmodified: " + e);
        }
    }

    void Setup()
    {
        var h = new Harmony(Guid);
        bool anim = Hooks.Install(h, Hooks.Anim);
        bool loadState = Hooks.Install(h, Hooks.LoadState);
        bool unload = Hooks.Install(h, Hooks.Unload);
        bool startAction = Hooks.Install(h, Hooks.StartAction);
        bool layout = Safety.ValidateActionLayout();

        Features.Anim = anim;
        Features.MirrorDelay = startAction;
        // LoadStateInternal is only the normal end point; MirrorSpeed also times out on its own.
        Features.MirrorHold = startAction && layout;
        Features.KeepMenu = loadState && unload;

        if (Features.MirrorHold) AddComponent<MirrorSpeed>();
        AddComponent<LoadTimer>();

        // Optional Belmont UI settings page; BelmontUi.* is only referenced inside BelmontUiPage.
        if (IL2CPPChainloader.Instance.Plugins.ContainsKey(BelmontUiGuid))
        {
            try { BelmontUiPage.Register(); }
            catch (Exception e) { L.LogWarning("Belmont UI settings page unavailable: " + e.Message); }
        }

        L.LogInfo($"FastLoad ready. Features: AnimationSpeed={On(Features.Anim)} MirrorHoldSpeed={On(Features.MirrorHold)} " +
                  $"MirrorEnterDelayFrames={On(Features.MirrorDelay)} KeepGameMenuLoaded={On(Features.KeepMenu)}. " +
                  $"Settings: {AnimationSpeed.Value}x, {MirrorHoldSpeed.Value}x, {MirrorEnterDelayFrames.Value} frames, keep={KeepGameMenuLoaded.Value}");
    }

    static string On(bool b) => b ? "on" : "OFF";
}

/// Which features are usable this session. Set at startup from the self-checks; switched off by Fail().
internal static class Features
{
    internal static volatile bool Anim, MirrorHold, MirrorDelay, KeepMenu;
    static int _animFails, _mirrorFails, _delayFails, _keepFails, _loadFails;
    const int MaxFailures = 3;

    public static void DisableAll() { Anim = MirrorHold = MirrorDelay = KeepMenu = false; }

    public static void Fail(string feature, Exception e)
    {
        ref int n = ref _loadFails;
        switch (feature)
        {
            case nameof(Anim): n = ref _animFails; break;
            case nameof(MirrorHold): n = ref _mirrorFails; break;
            case nameof(MirrorDelay): n = ref _delayFails; break;
            case nameof(KeepMenu): n = ref _keepFails; break;
        }
        n++;
        Plugin.L?.LogError($"{feature}: error {n}/{MaxFailures}: {e}");
        if (n < MaxFailures) return;
        Disable(feature, "too many errors");
    }

    public static void Disable(string feature, string why)
    {
        switch (feature)
        {
            case nameof(Anim): Anim = false; break;
            case nameof(MirrorHold): MirrorHold = false; MirrorSpeed.End("feature disabled"); break;
            case nameof(MirrorDelay): MirrorDelay = false; break;
            case nameof(KeepMenu): KeepMenu = false; break;
        }
        Plugin.L?.LogWarning($"{feature} disabled for this session ({why}); the game behaves as vanilla for it.");
    }
}

/// Hook definitions. Game types appear only inside Resolve lambdas, so a missing type fails inside Install's try/catch.
internal static class Hooks
{
    internal sealed class Spec
    {
        public string Name, Il2CppAssembly, Namespace, Class, Method;
        public int ArgCount, VerifiedRva;
        public Func<MethodBase> Resolve;  // exact managed interop method, or null
        public Func<Type> ReturnType;
        public Type PatchClass;
        public bool IsPrefix;
    }

    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static MethodBase Exact(Type t, string name, params Type[] args) =>
        t.GetMethod(name, Any, null, args, null);

    internal static readonly Spec Anim = new()
    {
        Name = "LoadingScreenTransitionAnimation.Update", Il2CppAssembly = "Assembly-CSharp", Namespace = "",
        Class = "LoadingScreenTransitionAnimation", Method = "Update", ArgCount = 1, VerifiedRva = 0x6CB2A0,
        Resolve = () => Exact(typeof(LoadingScreenTransitionAnimation), "Update", typeof(float)),
        ReturnType = () => typeof(void), PatchClass = typeof(AnimPatch), IsPrefix = true,
    };

    internal static readonly Spec LoadState = new()
    {
        Name = "ApplicationFlowManager.LoadStateInternal", Il2CppAssembly = "MotherBase.ApplicationFlowManager",
        Namespace = "MotherBase.ApplicationFlowManager", Class = "ApplicationFlowManager", Method = "LoadStateInternal",
        ArgCount = 1, VerifiedRva = 0x1E994E0,
        Resolve = () => Exact(typeof(MotherBase.ApplicationFlowManager.ApplicationFlowManager), "LoadStateInternal",
            typeof(MotherBase.ApplicationFlowManager.ApplicationState)),
        ReturnType = () => typeof(void), PatchClass = typeof(LoadStatePatch), IsPrefix = true,
    };

    internal static readonly Spec Unload = new()
    {
        Name = "AFMSceneManager.UnloadScene", Il2CppAssembly = "MotherBase.ApplicationFlowManager",
        Namespace = "MotherBase.ApplicationFlowManager", Class = "AFMSceneManager", Method = "UnloadScene",
        ArgCount = 2, VerifiedRva = 0x1E98440,
        Resolve = () => Exact(typeof(MotherBase.ApplicationFlowManager.AFMSceneManager), "UnloadScene",
            typeof(MotherBase.ApplicationFlowManager.SceneWrapper), typeof(bool)),
        ReturnType = () => typeof(MotherBase.ApplicationFlowManager.ITask), PatchClass = typeof(UnloadScenePatch), IsPrefix = true,
    };

    internal static readonly Spec StartAction = new()
    {
        Name = "Actor.StartActionInternal", Il2CppAssembly = "MotherBase.ActionFramework",
        Namespace = "MotherBase.ActionFramework", Class = "Actor", Method = "StartActionInternal",
        ArgCount = 3, VerifiedRva = 0x1E73C80,
        Resolve = () => Exact(typeof(MotherBase.ActionFramework.Actor), "StartActionInternal",
            typeof(MotherBase.EntityFramework.Entity), typeof(MotherBase.ActionFramework.ActionData), typeof(int).MakeByRefType()),
        ReturnType = () => typeof(void), PatchClass = typeof(StartActionPatch), IsPrefix = false,
    };

    public static bool Install(Harmony h, Spec s)
    {
        try
        {
            var target = s.Resolve();
            if (target is not MethodInfo mi) { Skip(s, "method with the expected signature not found"); return false; }
            if (mi.ReturnType != s.ReturnType()) { Skip(s, $"return type changed to {mi.ReturnType}"); return false; }
            if (!Safety.IsUnsharedNativeCode(s, out var why)) { Skip(s, why); return false; }
            var patch = new HarmonyMethod(AccessTools.Method(s.PatchClass, "Patch"));
            if (s.IsPrefix) h.Patch(target, prefix: patch); else h.Patch(target, postfix: patch);
            Plugin.L.LogInfo($"Hooked {s.Name} ({why})");
            return true;
        }
        catch (Exception e) { Skip(s, e.GetType().Name + ": " + e.Message); return false; }
    }

    static void Skip(Spec s, string why) =>
        Plugin.L.LogWarning($"Not hooking {s.Name}: {why}. Features that need it are disabled (game update?).");
}

/// Runtime checks against the IL2CPP metadata. Uses only lookups that don't run game static constructors.
internal static unsafe class Safety
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

    static IntPtr _gameAssembly;
    static bool _scanned;
    static System.Collections.Generic.Dictionary<IntPtr, int> _pointerUse;

    static IntPtr Class(string asm, string ns, string name) => IL2CPP.GetIl2CppClass(asm + ".dll", ns, name);

    /// True if the hook target's native code belongs to this method only.
    public static bool IsUnsharedNativeCode(Hooks.Spec s, out string why)
    {
        var klass = Class(s.Il2CppAssembly, s.Namespace, s.Class);
        if (klass == IntPtr.Zero) { why = "IL2CPP class not found"; return false; }
        var method = IL2CPP.il2cpp_class_get_method_from_name(klass, s.Method, s.ArgCount);
        if (method == IntPtr.Zero) { why = "IL2CPP method not found"; return false; }
        var code = *(IntPtr*)method; // MethodInfo.methodPointer
        if (code == IntPtr.Zero) { why = "no native code"; return false; }

        if (_gameAssembly == IntPtr.Zero) _gameAssembly = GetModuleHandleW("GameAssembly.dll");
        long rva = (long)code - (long)_gameAssembly;
        if (rva == s.VerifiedRva && !Plugin.ForceNativeScan.Value) { why = "verified build"; return true; }

        // Game was rebuilt: make sure no other method shares this code (identical-code folding).
        if (!_scanned) ScanMethodPointers();
        int uses = _pointerUse != null && _pointerUse.TryGetValue(code, out var n) ? n : -1;
        if (uses == 1) { why = $"new build, code at 0x{rva:X} is unshared"; return true; }
        why = uses < 0 ? "could not verify native code (scan failed)" : $"native code at 0x{rva:X} is shared by {uses} methods";
        return false;
    }

    static void ScanMethodPointers()
    {
        _scanned = true;
        try
        {
            var map = new System.Collections.Generic.Dictionary<IntPtr, int>(1 << 18);
            uint count = 0;
            var assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
            for (uint a = 0; a < count; a++)
            {
                var image = IL2CPP.il2cpp_assembly_get_image(assemblies[a]);
                uint classes = IL2CPP.il2cpp_image_get_class_count(image);
                for (uint c = 0; c < classes; c++)
                {
                    var klass = IL2CPP.il2cpp_image_get_class(image, c);
                    if (klass == IntPtr.Zero) continue;
                    IntPtr iter = IntPtr.Zero, m;
                    while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                    {
                        var p = *(IntPtr*)m;
                        if (p == IntPtr.Zero) continue;
                        map.TryGetValue(p, out var k);
                        map[p] = k + 1;
                    }
                }
            }
            _pointerUse = map;
            Plugin.L.LogInfo($"Game build differs from the verified one; scanned {map.Count} native methods for shared code.");
        }
        catch (Exception e) { Plugin.L.LogWarning("Native method scan failed: " + e.Message); }
    }

    /// MirrorSpeed reads Actor.activeActions (List<ActionInstance>) from raw memory. Confirm every offset/size
    /// it uses (see MirrorSpeed.TryGetActionFrame) before enabling it.
    public static bool ValidateActionLayout()
    {
        try
        {
            const string asm = "MotherBase.ActionFramework", ns = "MotherBase.ActionFramework";
            var actor = Class(asm, ns, "Actor");
            var instance = Class(asm, ns, "ActionInstance");
            var frameInfo = Class(asm, ns, "FrameInfo");
            if (actor == IntPtr.Zero || instance == IntPtr.Zero || frameInfo == IntPtr.Zero) return Bad("class not found");

            var active = IL2CPP.il2cpp_class_get_field_from_name(actor, "activeActions");
            if (active == IntPtr.Zero) return Bad("Actor.activeActions not found");
            if (IL2CPP.il2cpp_field_get_offset(active) != 0x38) return Bad($"Actor.activeActions at 0x{IL2CPP.il2cpp_field_get_offset(active):X}");
            var listClass = IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(active));
            if (listClass == IntPtr.Zero) return Bad("activeActions type not found");
            var listName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(listClass));
            if (listName != "List`1") return Bad("activeActions is " + listName);
            // Offsets of List<T>._items/_size don't depend on T; use the generic definition if the instance
            // class isn't set up yet.
            if (!(FieldAt(listClass, "_items", 0x10) && FieldAt(listClass, "_size", 0x18)))
            {
                var listDef = IL2CPP.GetIl2CppClass("mscorlib.dll", "System.Collections.Generic", "List`1");
                if (listDef == IntPtr.Zero || !FieldAt(listDef, "_items", 0x10) || !FieldAt(listDef, "_size", 0x18))
                    return Bad($"List<T> layout changed (_items {Off(listClass, "_items")}, _size {Off(listClass, "_size")})");
            }

            uint align = 0;
            int size = IL2CPP.il2cpp_class_value_size(instance, ref align);
            if (size != 0x20) return Bad($"ActionInstance size 0x{size:X}");
            // IL2CPP may report value-type field offsets including the 0x10 object header; accept either.
            long b = Off(instance, "data");
            if (b != 0 && b != 0x10) return Bad($"ActionInstance.data at {b}");
            if (Off(instance, "id") != b + 0x8 || Off(instance, "frameInfo") != b + 0xC || Off(frameInfo, "integerFrame") != b)
                return Bad($"ActionInstance/FrameInfo layout changed (id {Off(instance, "id")}, frameInfo {Off(instance, "frameInfo")}, integerFrame {Off(frameInfo, "integerFrame")})");
            Plugin.L.LogDebug("Action memory layout verified");
            return true;
        }
        catch (Exception e) { return Bad(e.GetType().Name + ": " + e.Message); }

        static long Off(IntPtr klass, string name)
        {
            var f = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
            return f == IntPtr.Zero ? -1 : IL2CPP.il2cpp_field_get_offset(f);
        }
        static bool FieldAt(IntPtr klass, string name, uint offset) => Off(klass, name) == offset;
        static bool Bad(string why)
        {
            Plugin.L.LogWarning($"MirrorHoldSpeed disabled: action memory layout check failed ({why}). Game update?");
            return false;
        }
    }
}

// Patches: one class per hook. Bodies never throw; work that touches interop members is in [NoInlining] methods.

internal static class AnimPatch
{
    static void Patch(ref float deltaTime)
    {
        if (!Features.Anim) return;
        try
        {
            var s = Plugin.AnimationSpeed.Value;
            if (s > 0f && s != 1f && s <= 16f) deltaTime *= s;
        }
        catch (Exception e) { Features.Fail(nameof(Features.Anim), e); }
    }
}

internal static class LoadStatePatch
{
    static void Patch(MotherBase.ApplicationFlowManager.ApplicationState stateToLoad)
    {
        try { MirrorSpeed.End("level load started"); } catch (Exception e) { Features.Fail(nameof(Features.MirrorHold), e); }
        try { LoadTimer.Begin(StateName(stateToLoad)); } catch { }
        try { KeepScenes.Apply(stateToLoad); } catch (Exception e) { Features.Fail(nameof(Features.KeepMenu), e); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string StateName(MotherBase.ApplicationFlowManager.ApplicationState s) => s != null ? s.name : "?";
}

internal static class UnloadScenePatch
{
    // Returning false skips the original; any problem returns true (normal unload).
    static bool Patch(MotherBase.ApplicationFlowManager.SceneWrapper sceneWrapper, ref MotherBase.ApplicationFlowManager.ITask __result)
    {
        if (!Features.KeepMenu || !Plugin.KeepGameMenuLoaded.Value) return true;
        try { return KeepScenes.UnloadScenePrefix(sceneWrapper, ref __result); }
        catch (Exception e) { Features.Fail(nameof(Features.KeepMenu), e); return true; }
    }
}

internal static class StartActionPatch
{
    const string CrouchActionName = "MC_Crouch";

    static void Patch(MotherBase.ActionFramework.Actor __instance, MotherBase.ActionFramework.ActionData data, ref int instanceId)
    {
        if (!Features.MirrorHold && !Features.MirrorDelay) return;
        string n;
        try { n = Name(data); }
        catch (Exception e) { Features.Fail(nameof(Features.MirrorDelay), e); return; }
        if (n == null) return;

        if (n == MirrorSpeed.HoldActionName && Features.MirrorHold)
        {
            try { MirrorSpeed.Begin(__instance, instanceId); }
            catch (Exception e) { Features.Fail(nameof(Features.MirrorHold), e); }
        }
        else if (n == CrouchActionName && Features.MirrorDelay)
        {
            try { ApplyMirrorEnterDelay(data); }
            catch (Exception e) { Features.Fail(nameof(Features.MirrorDelay), e); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Name(MotherBase.ActionFramework.ActionData data) => data != null ? data.name : null;

    // Shorten the crouch -> mirror action wait on the MC_Crouch asset.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ApplyMirrorEnterDelay(MotherBase.ActionFramework.ActionData crouch)
    {
        int target = Math.Clamp(Plugin.MirrorEnterDelayFrames.Value, 1, 30);
        var features = crouch.actionFeatures;
        if (features == null) return;
        for (int i = 0; i < features.Count; i++)
        {
            var f = features[i]?.TryCast<TryStartActionOverTimeFeature>();
            if (f == null || f.conditionValidatedAction == null) continue;
            if (f.conditionValidatedAction.name != MirrorSpeed.HoldActionName) continue;
            if (f.startFrame == target) return; // already applied to this asset instance
            int window = Math.Max(1, f.endFrame - f.startFrame);
            Plugin.L.LogInfo($"Mirror enter delay: {crouch.name} -> {f.conditionValidatedAction.name} startFrame {f.startFrame} -> {target} (endFrame {f.endFrame} -> {target + window})");
            f.startFrame = target;
            f.endFrame = target + window;
            return;
        }
    }
}

/// Runs the game faster (Time.timeScale) while the hold-to-enter-mirror action plays.
public class MirrorSpeed : MonoBehaviour
{
    public MirrorSpeed(IntPtr ptr) : base(ptr) { }

    public const string HoldActionName = "MC_Crouch_MirrorInteract_Tp";
    const int CommitFrame = 75;          // InteractWithCurrentMirror fires at frame 75 of the hold action
    const float CommittedTimeout = 5f;   // real seconds to stay fast after commit if no level load starts
    const float HoldTimeout = 10f;       // real seconds; the vanilla hold takes ~2.5 s, so this only fires if we lose track

    static MotherBase.ActionFramework.Actor _actor;
    static int _instanceId;
    static bool _active, _committed;
    static int _maxFrame;
    static float _speed, _savedScale = 1f, _committedAt, _startedAt, _frame75At;
    static float Target => _savedScale * _speed;

    public static void Begin(MotherBase.ActionFramework.Actor actor, int instanceId)
    {
        var s = Math.Min(Plugin.MirrorHoldSpeed.Value, 16f);
        if (s <= 1f) return;
        if (!_active)
        {
            if (Time.timeScale <= 0f) return; // paused; leave the game alone
            _savedScale = Time.timeScale;
        }
        _actor = actor; _instanceId = instanceId; _active = true; _committed = false; _maxFrame = 0; _speed = s;
        _startedAt = Time.realtimeSinceStartup; _frame75At = -1f;
        Time.timeScale = Target;
        Plugin.L.LogInfo($"Mirror hold started: timeScale {_savedScale} -> {Time.timeScale}");
    }

    public static void End(string why)
    {
        if (!_active) return;
        _active = false; _actor = null;
        // Only restore if nobody else changed it meanwhile (e.g. pause).
        if (Mathf.Approximately(Time.timeScale, Target)) Time.timeScale = _savedScale;
        var now = Time.realtimeSinceStartup;
        var f75 = _frame75At < 0 ? "never" : $"{_frame75At - _startedAt:F2}s";
        Plugin.L.LogInfo($"Mirror hold ended ({why}, reached frame {_maxFrame}): timeScale -> {Time.timeScale}. " +
                         $"Real time: start->frame{CommitFrame}={f75}, start->end={now - _startedAt:F2}s");
    }

    // The game resets timeScale every frame; LateUpdate runs last, so re-assert it here.
    void LateUpdate()
    {
        if (!_active || _committed) return;
        if (!Features.MirrorHold) { End("feature disabled"); return; }
        var ts = Time.timeScale;
        if (ts > 0f && !Mathf.Approximately(ts, Target)) Time.timeScale = Target;
    }

    void Update()
    {
        if (!_active) return;
        try { Poll(); }
        catch (Exception e) { End("error"); Features.Fail(nameof(Features.MirrorHold), e); }
    }

    static void Poll()
    {
        var now = Time.realtimeSinceStartup;
        if (_committed)
        {
            if (now - _committedAt > CommittedTimeout) End("timeout after commit");
            return;
        }
        if (now - _startedAt > HoldTimeout) { End("timeout while holding"); return; }

        bool running = TryGetActionFrame(_actor, _instanceId, out int frame);
        if (frame == int.MinValue) { End("unexpected memory contents"); Features.Disable(nameof(Features.MirrorHold), "unexpected memory contents"); return; }
        if (running && frame > _maxFrame) _maxFrame = frame;
        if (_frame75At < 0 && _maxFrame >= CommitFrame) _frame75At = now;

        if (running)
        {
            // Keep our speed applied unless the game paused (timeScale 0) in the meantime.
            if (Time.timeScale > 0f && !Mathf.Approximately(Time.timeScale, Target)) Time.timeScale = Target;
            return;
        }
        if (_maxFrame >= CommitFrame - 1)
        {
            // Interaction happened: keep fast through the mirror entry until the level load starts.
            _committed = true; _committedAt = now;
            Plugin.L.LogInfo($"Mirror hold committed at frame {_maxFrame}; staying fast until level load");
        }
        else End("released/cancelled");
    }

    // Reads Actor.activeActions from raw memory (interop methods taking ActionInstance crash).
    // Offsets verified at startup by Safety.ValidateActionLayout:
    //   Actor:  activeActions @ 0x38
    //   List<T>: _items @ 0x10, _size @ 0x18;  T[]: max_length @ 0x18, data @ 0x20 (IL2CPP array header)
    //   ActionInstance (0x20 bytes): data @ 0x0, id @ 0x8, frameInfo.integerFrame @ 0xC
    // Returns true + frame if the instance is active; false if not; frame = int.MinValue if the contents look wrong.
    static unsafe bool TryGetActionFrame(MotherBase.ActionFramework.Actor actor, int instanceId, out int frame)
    {
        frame = 0;
        if (actor == null || actor.Pointer == IntPtr.Zero || actor.WasCollected) return false;
        var list = *(IntPtr*)((byte*)actor.Pointer + 0x38);
        if (list == IntPtr.Zero) return false;
        var items = *(IntPtr*)((byte*)list + 0x10);
        int size = *(int*)((byte*)list + 0x18);
        if (items == IntPtr.Zero) return false;
        long capacity = *(long*)((byte*)items + 0x18);
        if (size < 0 || size > capacity || capacity > 4096) { frame = int.MinValue; return false; }
        byte* data = (byte*)items + 0x20;
        for (int i = 0; i < size; i++)
        {
            byte* e = data + i * 0x20;
            if (*(int*)(e + 0x8) == instanceId)
            {
                if (*(IntPtr*)e == IntPtr.Zero) { frame = int.MinValue; return false; } // ActionData must be set
                frame = *(int*)(e + 0xC);
                return true;
            }
        }
        return false;
    }
}

/// Skips unloading GameMenu between gameplay states when the next state also contains it.
internal static class KeepScenes
{
    const string GameMenuPathSuffix = "/GameMenu.unity";
    const string GameMenuSceneName = "GameMenu";
    static bool _nextHasGameMenu, _keptThisLoad;
    static string _nextState = "?";

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool IsGameMenu(MotherBase.ApplicationFlowManager.SceneWrapper w) =>
        w != null && w.path != null && w.path.EndsWith(GameMenuPathSuffix);

    // Called from the LoadStateInternal prefix, before the old state is unloaded.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Apply(MotherBase.ApplicationFlowManager.ApplicationState next)
    {
        _nextHasGameMenu = false;
        _keptThisLoad = false;
        if (!Features.KeepMenu) return;
        _nextState = next != null ? next.name : "?";
        var scenes = next?.FetchScenesWithIncludes();
        if (scenes == null) return;
        for (int i = 0; i < scenes.Count; i++)
            if (IsGameMenu(scenes[i])) { _nextHasGameMenu = true; break; }
    }

    // Prefix body for AFMSceneManager.UnloadScene(SceneWrapper, bool) -> ITask. Return false to skip the unload.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool UnloadScenePrefix(MotherBase.ApplicationFlowManager.SceneWrapper sceneWrapper, ref MotherBase.ApplicationFlowManager.ITask __result)
    {
        if (!_nextHasGameMenu || !IsGameMenu(sceneWrapper)) return true;
        var done = new MotherBase.ApplicationFlowManager.TaskAction((Il2CppSystem.Action)new Action(() => { }), "FastLoad.KeepGameMenuLoaded");
        var task = done.TryCast<MotherBase.ApplicationFlowManager.ITask>();
        if (task == null) return true;
        __result = task;
        _keptThisLoad = true;
        Plugin.L.LogInfo($"KeepGameMenuLoaded: kept {sceneWrapper.path} open (next state {_nextState} also contains it)");
        return false;
    }

    // Called when the loading screen closes: if the kept scene wasn't reused, stop keeping it.
    public static void AfterLoad()
    {
        if (!_keptThisLoad) return;
        _keptThisLoad = false;
        int count = 0;
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            if (s.isLoaded && s.name == GameMenuSceneName) count++;
        }
        if (count != 1) Features.Disable(nameof(Features.KeepMenu), $"expected 1 open GameMenu scene after the load, found {count}");
    }
}

/// Logs how long each level load takes: from LoadStateInternal until the loading-screen scene is unloaded.
public class LoadTimer : MonoBehaviour
{
    public LoadTimer(IntPtr ptr) : base(ptr) { }
    static float _start = -1;
    static string _state;
    bool _hooked;

    public static void Begin(string state) { _start = Time.realtimeSinceStartup; _state = state; }

    void Update()
    {
        if (_hooked) return;
        _hooked = true;
        try
        {
            UnityEngine.SceneManagement.SceneManager.add_sceneUnloaded(
                (UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene>)new Action<UnityEngine.SceneManagement.Scene>(OnUnloaded));
        }
        catch (Exception e) { Plugin.L.LogWarning("Load timing unavailable: " + e.Message); }
    }

    static void OnUnloaded(UnityEngine.SceneManagement.Scene s)
    {
        try
        {
            if (s.name == null || !s.name.StartsWith("LoadingScreen")) return;
            try { KeepScenes.AfterLoad(); } catch (Exception e) { Features.Fail(nameof(Features.KeepMenu), e); }
            if (_start < 0) return;
            Plugin.L.LogInfo($"Load timing: '{_state}' took {Time.realtimeSinceStartup - _start:F2}s (KeepGameMenuLoaded={Plugin.KeepGameMenuLoaded.Value && Features.KeepMenu})");
            _start = -1;
        }
        catch { }
    }
}

/// Settings page on Belmont UI's MODS SETTINGS tab. Only entered after the chainloader check in Plugin.Setup.
internal static class BelmontUiPage
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Register()
    {
        BelmontUi.Settings.Register(Plugin.Guid, "Belmont FastLoad", menu =>
        {
            menu.Header("Loading screen");
            menu.Slider(Plugin.AnimationSpeed, "Animation speed", 1f, 8f, 0.5f, "{0:0.#}x",
                "Loading-screen animation playback speed. 1x = vanilla.");
            menu.Header("Save mirrors");
            menu.Slider(Plugin.MirrorHoldSpeed, "Hold-to-enter speed", 1f, 8f, 0.5f, "{0:0.#}x",
                "Speed of the hold-down animation at a save mirror. 1x = vanilla.");
            menu.IntSlider(Plugin.MirrorEnterDelayFrames, "Entry delay (frames)", 1, 30, 1,
                "Crouch frames before the mirror animation starts (30 per second). Vanilla = 30.");
            menu.Header("Experimental");
            menu.Toggle(Plugin.KeepGameMenuLoaded, "Keep menu loaded",
                "Keep the pause menu loaded between levels: ~1 s faster per loading screen.");
            menu.ResetButton();
        }, summary: "Faster loading screens and save-mirror travel.");
        Plugin.L.LogInfo("Registered settings page with Belmont UI");
    }
}
