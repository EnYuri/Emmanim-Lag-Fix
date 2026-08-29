using System.Reflection;
using System.Reflection.Emit;
using Halfling.Network;
using Halfling.Timing;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Extends Halfling's application-level session timeout from ten seconds to
/// thirty seconds. Cosmoteer's reliable-message acknowledgements are produced
/// by the main-thread network pump, so a long simulation stall can otherwise
/// disconnect a healthy peer even when the underlying Steam/UDP route has no
/// packet loss.
///
/// This changes no packet format, resend cadence, input ordering, or simulation
/// state. It only gives an already-open session longer to recover. Both peers
/// should run the same build so that neither side retains the vanilla timeout.
/// </summary>
[HarmonyPatch]
internal static class MultiplayerSessionTimeoutPatch
{
    private const float ExtendedTimeoutSeconds = 30f;

    internal static int SuccessfulTranspilerCount;

    private static readonly FieldInfo SessionTimeoutField = AccessTools.Field(
        typeof(NetworkMessenger),
        "SESSION_TIMEOUT")
        ?? throw new MissingFieldException(typeof(NetworkMessenger).FullName, "SESSION_TIMEOUT");

    private static readonly MethodInfo GetExtendedTimeoutMethod = AccessTools.Method(
        typeof(MultiplayerSessionTimeoutPatch),
        nameof(GetExtendedTimeout))
        ?? throw new MissingMethodException(typeof(MultiplayerSessionTimeoutPatch).FullName, nameof(GetExtendedTimeout));

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NetworkMessenger), "ProcessUnresponsiveSessions")
            ?? throw new MissingMethodException(typeof(NetworkMessenger).FullName, "ProcessUnresponsiveSessions");
        yield return AccessTools.Method(typeof(NetworkMessenger), "EnqueueOutgoingAcks")
            ?? throw new MissingMethodException(typeof(NetworkMessenger).FullName, "EnqueueOutgoingAcks");
    }

    private static Time GetExtendedTimeout() => ExtendedTimeoutSeconds;

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        var codes = new List<CodeInstruction>(instructions);
        var expectedReferences = __originalMethod.Name switch
        {
            "ProcessUnresponsiveSessions" => 4,
            "EnqueueOutgoingAcks" => 2,
            _ => 0
        };

        var references = codes.Count(code =>
            code.opcode == OpCodes.Ldsfld && Equals(code.operand, SessionTimeoutField));

        if (expectedReferences == 0 || references != expectedReferences)
        {
            Halfling.Logging.Logger.Log(
                $"Emmanim Lag Fix did not extend the multiplayer session timeout in " +
                $"{__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}: " +
                $"expected {expectedReferences} SESSION_TIMEOUT references, found {references}. " +
                "The original ten-second behavior was preserved.");
            return codes;
        }

        foreach (var code in codes)
        {
            if (code.opcode == OpCodes.Ldsfld && Equals(code.operand, SessionTimeoutField))
            {
                code.opcode = OpCodes.Call;
                code.operand = GetExtendedTimeoutMethod;
            }
        }

        Interlocked.Increment(ref SuccessfulTranspilerCount);

        return codes;
    }
}
