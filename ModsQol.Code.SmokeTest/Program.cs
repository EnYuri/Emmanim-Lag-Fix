using HarmonyLib;
using System.Reflection;

// Standalone smoke host for the Mods QoL code module. It proves that every member the
// sentinel binder reaches for still exists on this game build and that all four patches
// actually installed, so a shape change surfaces here rather than as a wire network that
// quietly stops delivering.

var gameAssembly = Assembly.Load("Cosmoteer");

Type Game(string name) => gameAssembly.GetType(name, throwOnError: true)!;

var proxyHandlerType = Game("Cosmoteer.Ships.Parts.Logic.ProxyHandler`1");
var storageInterface = Game("Cosmoteer.Ships.Parts.Resources.IResourceStorage");
var partComponentType = Game("Cosmoteer.Ships.Parts.PartComponent");
var partType = Game("Cosmoteer.Ships.Parts.Part");
var partsManagerType = Game("Cosmoteer.Ships.Parts.PartsManager");
var partRulesType = Game("Cosmoteer.Ships.Parts.PartRules");
var baseStorageType = Game("Cosmoteer.Ships.Parts.Resources.BaseResourceStorage");
var proxyRulesType = Game("Cosmoteer.Ships.Parts.Logic.ProxyRules");
var storageProxyType = Game("Cosmoteer.Ships.Parts.Resources.ResourceStorageProxy");
var presenceToggleType = Game("Cosmoteer.Ships.Parts.Logic.ComponentPresenceToggle");

var closedHandlers = new[]
{
    proxyHandlerType.MakeGenericType(storageInterface),
    proxyHandlerType.MakeGenericType(partComponentType),
};

// ---------------------------------------------------------------------------
// The reason this module patches the owning components rather than ProxyHandler
// ---------------------------------------------------------------------------
// ProxyHandler is generic over reference types only, so the runtime shares one canonical
// body between both instantiations: the MethodInfos differ but the RuntimeMethodHandle is
// the same method. Patching "each" instantiation therefore patches one method twice, and
// Harmony cannot see the collision because its registry is keyed by MethodInfo. Version
// 2.1.0 did that and bound nothing at all. Assert the sharing is still real, so that if a
// future runtime ever stops sharing, this test says so rather than silently going quiet.
var handleA = AccessTools.DeclaredMethod(closedHandlers[0], "OnProxiedPartAdded")
    ?? throw new MissingMethodException(closedHandlers[0].FullName, "OnProxiedPartAdded");
var handleB = AccessTools.DeclaredMethod(closedHandlers[1], "OnProxiedPartAdded")
    ?? throw new MissingMethodException(closedHandlers[1].FullName, "OnProxiedPartAdded");
if (handleA.MethodHandle != handleB.MethodHandle)
{
    Console.WriteLine(
        "NOTE: ProxyHandler's instantiations no longer share one canonical method. Patching "
        + "them directly would now be safe, but this module does not rely on that.");
}

// Members the binder and the attachment layer reach for.
foreach (var handler in closedHandlers)
{
    _ = AccessTools.DeclaredMethod(handler, "OnProxiedPartComponentAdded")
        ?? throw new MissingMethodException(handler.FullName, "OnProxiedPartComponentAdded");
    // The rebind guard replays vanilla's own add path against the part that is still at the
    // cell, so its shape has to hold: one Part parameter, and a _proxiedPart to read back.
    var added = AccessTools.DeclaredMethod(handler, "OnProxiedPartAdded")
        ?? throw new MissingMethodException(handler.FullName, "OnProxiedPartAdded");
    var addedParams = added.GetParameters();
    if (addedParams.Length != 1 || addedParams[0].ParameterType != partType)
    {
        throw new InvalidOperationException(
            $"{handler.Name}.OnProxiedPartAdded is no longer (Part), so the rebind guard would "
            + "invoke it with the wrong arguments.");
    }
    _ = AccessTools.DeclaredField(handler, "_proxiedPart")
        ?? throw new MissingFieldException(handler.FullName, "_proxiedPart");
    _ = AccessTools.PropertySetter(handler, "ProxiedComponent")
        ?? throw new MissingMethodException(handler.FullName, "set_ProxiedComponent");
    _ = AccessTools.PropertyGetter(handler, "ProxiedComponent")
        ?? throw new MissingMethodException(handler.FullName, "get_ProxiedComponent");
    _ = AccessTools.PropertyGetter(handler, "Rules")
        ?? throw new MissingMethodException(handler.FullName, "get_Rules");
    // The sentinel scan resumes past the entry vanilla stopped on.
    _ = AccessTools.DeclaredField(handler, "_proxyableIndex")
        ?? throw new MissingFieldException(handler.FullName, "_proxyableIndex");
}

// The four patch targets. All four are non-generic methods on non-generic types, which is
// the whole point: no canonical sharing is possible here.
var patchTargets = new[]
{
    AccessTools.DeclaredMethod(storageProxyType, "OnPartAttached2")
        ?? throw new MissingMethodException(storageProxyType.FullName, "OnPartAttached2"),
    AccessTools.DeclaredMethod(storageProxyType, "OnPartDetaching2")
        ?? throw new MissingMethodException(storageProxyType.FullName, "OnPartDetaching2"),
    AccessTools.DeclaredMethod(presenceToggleType, "OnPartAttached")
        ?? throw new MissingMethodException(presenceToggleType.FullName, "OnPartAttached"),
    AccessTools.DeclaredMethod(presenceToggleType, "OnPartDetaching")
        ?? throw new MissingMethodException(presenceToggleType.FullName, "OnPartDetaching"),
};
foreach (var target in patchTargets)
{
    if (target.DeclaringType!.IsGenericType || target.IsGenericMethod)
    {
        throw new InvalidOperationException(
            $"{target.DeclaringType}.{target.Name} is generic; patching it risks the shared "
            + "canonical-code collision this module exists to avoid.");
    }
}

// The proxy handler each owning component holds, reached by field rather than reflection at
// runtime, so its exact closed type matters.
var storageProxyField = AccessTools.DeclaredField(storageProxyType, "_proxy")
    ?? throw new MissingFieldException(storageProxyType.FullName, "_proxy");
if (storageProxyField.FieldType != closedHandlers[0])
{
    throw new InvalidOperationException(
        $"ResourceStorageProxy._proxy is {storageProxyField.FieldType}, expected {closedHandlers[0]}.");
}
var presenceToggleField = AccessTools.DeclaredField(presenceToggleType, "_proxy")
    ?? throw new MissingFieldException(presenceToggleType.FullName, "_proxy");
if (presenceToggleField.FieldType != closedHandlers[1])
{
    throw new InvalidOperationException(
        $"ComponentPresenceToggle._proxy is {presenceToggleField.FieldType}, expected {closedHandlers[1]}.");
}

// The attachment layer mirrors vanilla's own cell registration, so it needs the same API.
var cellHandlerType = typeof(Action<>).MakeGenericType(partType);
foreach (var name in new[]
         {
             "RegisterCellAddHandler", "UnregisterCellAddHandler",
             "RegisterCellRemovingHandler", "UnregisterCellRemovingHandler",
         })
{
    var method = AccessTools.Method(partsManagerType, name)
        ?? throw new MissingMethodException(partsManagerType.FullName, name);
    if (method.GetParameters()[1].ParameterType != cellHandlerType)
    {
        throw new InvalidOperationException(
            $"PartsManager.{name} no longer takes Action<Part>, so the cell watches "
            + "would not match vanilla's registration.");
    }
}
_ = AccessTools.Method(partRulesType, "GetShipRelativeCell")
    ?? throw new MissingMethodException(partRulesType.FullName, "GetShipRelativeCell");

var proxyableType = proxyRulesType.GetNestedType("ProxyableComponent", BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMemberException(proxyRulesType.FullName, "ProxyableComponent");
foreach (var name in new[] { "ProxyableComponents", "PartLocation", "ProxyToggle" })
{
    _ = AccessTools.Field(proxyRulesType, name)
        ?? throw new MissingFieldException(proxyRulesType.FullName, name);
}
foreach (var name in new[] { "ComponentID", "PartCriteria" })
{
    _ = AccessTools.Field(proxyableType, name)
        ?? throw new MissingFieldException(proxyableType.FullName, name);
}

var criteriaType = Game("Cosmoteer.Ships.Parts.RelativePartCriteria");
_ = AccessTools.Method(criteriaType, "IsMatch")
    ?? throw new MissingMethodException(criteriaType.FullName, "IsMatch");

_ = AccessTools.Method(baseStorageType, "GetAllOfTypeOnPart")
    ?? throw new MissingMethodException(baseStorageType.FullName, "GetAllOfTypeOnPart");
_ = AccessTools.PropertyGetter(baseStorageType, "MaxResources")
    ?? throw new MissingMethodException(baseStorageType.FullName, "get_MaxResources");
_ = AccessTools.PropertyGetter(baseStorageType, "ResourceType")
    ?? throw new MissingMethodException(baseStorageType.FullName, "get_ResourceType");

// The binder unsubscribes vanilla's own handler by rebuilding its delegate, and the late
// watch subscribes one of its own, so the event's delegate type has to stay put.
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

// Install for real and confirm every patch took. PatchAll is invoked directly rather than
// through EntryPoint because the entry point also writes to Halfling's Logger, which has no
// game to initialize against in this standalone host.
new Harmony(ModsQol.Code.EntryPoint.HarmonyId).PatchAll(typeof(ModsQol.Code.EntryPoint).Assembly);

foreach (var target in patchTargets)
{
    var info = Harmony.GetPatchInfo(target);
    var mine = (info?.Prefixes.Count(p => p.owner == ModsQol.Code.EntryPoint.HarmonyId) ?? 0)
             + (info?.Postfixes.Count(p => p.owner == ModsQol.Code.EntryPoint.HarmonyId) ?? 0);
    if (mine != 1)
    {
        throw new InvalidOperationException(
            $"Expected exactly one sentinel patch on {target.DeclaringType!.Name}.{target.Name}, found {mine}.");
    }
}

// Regression guard for the 2.1.0 defect: nothing may patch ProxyHandler's shared canonical
// methods, because both instantiations resolve to the same method and the second patch wins.
foreach (var handler in closedHandlers)
{
    foreach (var name in new[] { "OnProxiedPartAdded", "OnProxiedPartComponentAdded" })
    {
        var method = AccessTools.DeclaredMethod(handler, name)!;
        var info = Harmony.GetPatchInfo(method);
        if (info != null && info.Owners.Contains(ModsQol.Code.EntryPoint.HarmonyId))
        {
            throw new InvalidOperationException(
                $"{handler.Name}.{name} is patched by this module. Both ProxyHandler instantiations "
                + "share one canonical method, so such a patch is applied twice and only the last "
                + "one survives - which is exactly why 2.1.0 never bound anything.");
        }
    }
}

new Harmony(ModsQol.Code.EntryPoint.HarmonyId).UnpatchAll(ModsQol.Code.EntryPoint.HarmonyId);
Console.WriteLine(
    "PASS: sentinel targets resolved, all four non-generic proxy-owner patches installed, and no "
    + "shared canonical ProxyHandler method is patched; sentinel resolution is prefix-scoped, so "
    + "the module stays inert without Mods QoL.");
