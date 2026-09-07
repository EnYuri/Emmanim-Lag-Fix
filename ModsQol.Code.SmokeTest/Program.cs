using HarmonyLib;
using System.Reflection;

// Standalone smoke host for the Mods QoL code module. It proves that every member the
// sentinel binder reaches for still exists on this game build and that all four patches
// actually installed, so a shape change surfaces here rather than as a wire network that
// quietly stops delivering.

var gameAssembly = Assembly.Load("Cosmoteer");

var proxyHandlerType = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.Logic.ProxyHandler`1", throwOnError: true)!;
var storageInterface = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.Resources.IResourceStorage", throwOnError: true)!;
var partComponentType = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.PartComponent", throwOnError: true)!;
var partType = gameAssembly.GetType("Cosmoteer.Ships.Parts.Part", throwOnError: true)!;
var baseStorageType = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.Resources.BaseResourceStorage", throwOnError: true)!;
var proxyRulesType = gameAssembly.GetType(
    "Cosmoteer.Ships.Parts.Logic.ProxyRules", throwOnError: true)!;

// Both closed instantiations the game builds. ResourceStorageProxy uses the first,
// ComponentPresenceToggle the second; the binder patches each separately.
var closedHandlers = new[]
{
    proxyHandlerType.MakeGenericType(storageInterface),
    proxyHandlerType.MakeGenericType(partComponentType),
};

// Members the binder calls directly. A rename in any of them would compile (they are all
// reached through AccessTools or a publicized reference) but fail silently at runtime.
foreach (var handler in closedHandlers)
{
    _ = AccessTools.DeclaredMethod(handler, "OnProxiedPartAdded")
        ?? throw new MissingMethodException(handler.FullName, "OnProxiedPartAdded");
    _ = AccessTools.DeclaredMethod(handler, "OnProxiedPartComponentAdded")
        ?? throw new MissingMethodException(handler.FullName, "OnProxiedPartComponentAdded");
    _ = AccessTools.PropertySetter(handler, "ProxiedComponent")
        ?? throw new MissingMethodException(handler.FullName, "set_ProxiedComponent");
    _ = AccessTools.PropertyGetter(handler, "ProxiedComponent")
        ?? throw new MissingMethodException(handler.FullName, "get_ProxiedComponent");
    _ = AccessTools.PropertyGetter(handler, "Rules")
        ?? throw new MissingMethodException(handler.FullName, "get_Rules");
    _ = AccessTools.DeclaredField(handler, "_proxyableIndex")
        ?? throw new MissingFieldException(handler.FullName, "_proxyableIndex");
    // The sentinel scan resumes past the entry vanilla stopped on, so it has to re-evaluate
    // PartCriteria itself, which needs the proxy's own part.
    _ = AccessTools.DeclaredField(handler, "_parentPart")
        ?? throw new MissingFieldException(handler.FullName, "_parentPart");
}

var criteriaType = gameAssembly.GetType("Cosmoteer.Ships.Parts.RelativePartCriteria", throwOnError: true)!;
_ = AccessTools.Method(criteriaType, "IsMatch")
    ?? throw new MissingMethodException(criteriaType.FullName, "IsMatch");
_ = AccessTools.Field(proxyableTypeProbe(), "PartCriteria")
    ?? throw new MissingFieldException("ProxyRules.ProxyableComponent", "PartCriteria");

Type proxyableTypeProbe() => gameAssembly
    .GetType("Cosmoteer.Ships.Parts.Logic.ProxyRules", throwOnError: true)!
    .GetNestedType("ProxyableComponent", BindingFlags.Public | BindingFlags.NonPublic)!;

_ = AccessTools.Field(proxyRulesType, "ProxyableComponents")
    ?? throw new MissingFieldException(proxyRulesType.FullName, "ProxyableComponents");
var proxyableType = proxyRulesType.GetNestedType("ProxyableComponent", BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMemberException(proxyRulesType.FullName, "ProxyableComponent");
_ = AccessTools.Field(proxyableType, "ComponentID")
    ?? throw new MissingFieldException(proxyableType.FullName, "ComponentID");

_ = AccessTools.Method(baseStorageType, "GetAllOfTypeOnPart")
    ?? throw new MissingMethodException(baseStorageType.FullName, "GetAllOfTypeOnPart");
_ = AccessTools.PropertyGetter(baseStorageType, "MaxResources")
    ?? throw new MissingMethodException(baseStorageType.FullName, "get_MaxResources");
_ = AccessTools.PropertyGetter(baseStorageType, "ResourceType")
    ?? throw new MissingMethodException(baseStorageType.FullName, "get_ResourceType");

// The binder unsubscribes vanilla's own handler by rebuilding its delegate, so the event's
// delegate type has to stay Action<Part, PartComponent> for the removal to match.
var componentAdded = partType.GetEvent("ComponentAdded", BindingFlags.Public | BindingFlags.Instance)
    ?? throw new MissingMemberException(partType.FullName, "ComponentAdded");
var expectedHandlerType = typeof(Action<,>).MakeGenericType(partType, partComponentType);
if (componentAdded.EventHandlerType != expectedHandlerType)
{
    throw new InvalidOperationException(
        "Part.ComponentAdded is no longer Action<Part, PartComponent> (now "
        + componentAdded.EventHandlerType?.FullName
        + "), so the binder's unsubscribe would silently fail to match vanilla's delegate and "
        + "strand the handler on a part that is no longer proxied.");
}

// A sentinel resolves to its resource; an ordinary component name must not.
var binder = typeof(ModsQol.Code.EntryPoint).Assembly.GetType("ModsQol.Code.AnyStorageBinder", throwOnError: true)!;
var resolve = AccessTools.Method(binder, "ResolveSentinel")
    ?? throw new MissingMethodException(binder.FullName, "ResolveSentinel");
var componentIdType = resolve.GetParameters()[0].ParameterType;
object MakeId(string name) => Activator.CreateInstance(componentIdType, name)!;

if (resolve.Invoke(null, new[] { MakeId("znayuri_any_battery") }) is null)
{
    throw new InvalidOperationException("The sentinel 'znayuri_any_battery' did not resolve to a resource.");
}
if (resolve.Invoke(null, new[] { MakeId("BatteryStorage") }) is not null)
{
    throw new InvalidOperationException(
        "An ordinary component name resolved as a sentinel, so the module would no longer be inert "
        + "without Mods QoL.");
}

// Install for real and confirm all four patches took. PatchAll is invoked directly rather
// than through EntryPoint because the entry point also writes to Halfling's Logger, which
// has no game to initialize against in this standalone host.
new Harmony(ModsQol.Code.EntryPoint.HarmonyId).PatchAll(typeof(ModsQol.Code.EntryPoint).Assembly);
foreach (var handler in closedHandlers)
{
    var added = AccessTools.DeclaredMethod(handler, "OnProxiedPartAdded")!;
    if (Harmony.GetPatchInfo(added)?.Postfixes.Any(p => p.owner == ModsQol.Code.EntryPoint.HarmonyId) != true)
    {
        throw new InvalidOperationException(
            "The sentinel postfix was not installed on " + handler + ".OnProxiedPartAdded.");
    }

    var late = AccessTools.DeclaredMethod(handler, "OnProxiedPartComponentAdded")!;
    if (Harmony.GetPatchInfo(late)?.Prefixes.Any(p => p.owner == ModsQol.Code.EntryPoint.HarmonyId) != true)
    {
        throw new InvalidOperationException(
            "The late-bind prefix was not installed on " + handler + ".OnProxiedPartComponentAdded, so a "
            + "storage appearing after the proxy attached would never bind.");
    }
}

new Harmony(ModsQol.Code.EntryPoint.HarmonyId).UnpatchAll(ModsQol.Code.EntryPoint.HarmonyId);
Console.WriteLine(
    "PASS: sentinel storage-proxy targets resolved and all four ProxyHandler patches installed on this "
    + "game build; sentinel resolution is prefix-scoped, so the module stays inert without Mods QoL.");
