using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Statuses;
using Cosmoteer.Ships.Statuses.Subhandlers;
using Halfling;
using Halfling.Geometry;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Vanilla heat diffusion allocates and scans every cell in the rectangular
/// bounds around all active heat statuses. Two distant hot cells on a very
/// large ship therefore make every cell between them participate in every
/// physics tick, even though cells at the diffusion basis with only basis-valued
/// neighbours cannot produce a delta.
///
/// For large heat bounds, calculate the identical single-tick stencil only for
/// active status cells and their four direct neighbours. Inputs are snapshotted
/// before any output is applied, and outputs are applied in vanilla row-major
/// order, preserving the diffusion rate and deterministic event ordering.
/// Other status types and small heat bounds retain the vanilla implementation.
/// </summary>
[HarmonyPatch(typeof(StatusDiffuser), nameof(StatusDiffuser.PerformDiffusion))]
internal static class SparseHeatDiffusionPatch
{
    private const string HeatStatusId = "cosmoteer.heat";
    // The density guard below still sends dense fields through vanilla. A
    // 64x64 lower bound also catches the medium-sized sparse heat networks
    // that remained visible in post-resource-patch traces.
    private const int MinimumSparseBoundsArea = 64 * 64;

    private static readonly AccessTools.FieldRef<StatusDiffuser, EventHandler<StatusDiffusionArgs>?>
        StatusesDiffused = AccessTools.FieldRefAccess<StatusDiffuser, EventHandler<StatusDiffusionArgs>?>(
            nameof(StatusDiffuser.StatusesDiffused));

    [ThreadStatic]
    private static Buffers? s_buffers;

    private sealed class Buffers
    {
        public readonly HashSet<IntVector2> CandidateSet = new();
        public readonly List<IntVector2> Candidates = new();
        public readonly List<CellOutput> Outputs = new();
        public readonly List<StatusDiffusionArgs.ModifiedStatus> Modified = new();
        public readonly List<StatusDiffusionArgs.CreatedStatus> Created = new();
        public bool InUse;

        public void Clear()
        {
            CandidateSet.Clear();
            Candidates.Clear();
            Outputs.Clear();
            Modified.Clear();
            Created.Clear();
        }
    }

    private readonly struct InputCell
    {
        public readonly StatusList<IntVector2>? List;
        public readonly Part? Part;
        public readonly StatusType.DiffusionSpeedFactors SpeedFactors;
        public readonly float Value;

        public InputCell(
            StatusList<IntVector2>? list,
            Part? part,
            StatusType.DiffusionSpeedFactors speedFactors,
            float basis)
        {
            List = list;
            Part = part;
            SpeedFactors = speedFactors;
            Value = list is { Count: 1 } ? list[0].Value : basis;
        }
    }

    private readonly struct CellOutput
    {
        public readonly IntVector2 Cell;
        public readonly InputCell Input;
        public readonly float Delta;

        public CellOutput(IntVector2 cell, in InputCell input, float delta)
        {
            Cell = cell;
            Input = input;
            Delta = delta;
        }
    }

    private sealed class RowMajorComparer : IComparer<IntVector2>
    {
        public static readonly RowMajorComparer Instance = new();

        public int Compare(IntVector2 left, IntVector2 right)
        {
            var y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.X.CompareTo(right.X);
        }
    }

    private static bool Prefix(StatusDiffuser __instance)
    {
        if (!string.Equals(__instance.StatusType.ID.ToString(), HeatStatusId, StringComparison.Ordinal) ||
            __instance._boundsTracker.Bounds.Area < MinimumSparseBoundsArea)
        {
            return true;
        }

        var buffers = s_buffers ??= new Buffers();
        if (buffers.InUse)
        {
            // Diffusion is not expected to recurse, but vanilla is safer than
            // corrupting the shared per-thread scratch buffers if another mod
            // introduces a re-entrant status callback.
            return true;
        }

        buffers.InUse = true;
        try
        {
            return !TryPerformSparseDiffusion(__instance, buffers);
        }
        finally
        {
            buffers.Clear();
            buffers.InUse = false;
        }
    }

    private static bool TryPerformSparseDiffusion(StatusDiffuser diffuser, Buffers buffers)
    {
        foreach (var status in diffuser._statusStore)
        {
            AddCandidateAndNeighbours(status.Location, buffers);
        }

        if (buffers.CandidateSet.Count == 0)
        {
            return true;
        }

        // Hashing and sorting are advantageous only when the active frontier is
        // genuinely sparse. Dense heat fields stay on vanilla's array-based,
        // parallel implementation.
        var vanillaCellCount = diffuser._boundsTracker.Bounds.Inflate(1).Area;
        if ((long)buffers.CandidateSet.Count * 4 >= vanillaCellCount)
        {
            return false;
        }

        buffers.Candidates.AddRange(buffers.CandidateSet);
        buffers.Candidates.Sort(RowMajorComparer.Instance);

        foreach (var cell in buffers.Candidates)
        {
            var input = GetInput(diffuser, cell);
            if (input.Part == null)
            {
                continue;
            }

            var delta = GetNeighbourDelta(diffuser, in input, cell + new IntVector2(-1, 0));
            delta += GetNeighbourDelta(diffuser, in input, cell + new IntVector2(1, 0));
            delta += GetNeighbourDelta(diffuser, in input, cell + new IntVector2(0, -1));
            delta += GetNeighbourDelta(diffuser, in input, cell + new IntVector2(0, 1));
            delta += GetSelfOccupancyDelta(diffuser, in input, cell);
            buffers.Outputs.Add(new CellOutput(cell, in input, delta));
        }

        ApplyDiffusedValues(diffuser, buffers);
        return true;
    }

    private static void AddCandidateAndNeighbours(IntVector2 cell, Buffers buffers)
    {
        buffers.CandidateSet.Add(cell);
        buffers.CandidateSet.Add(cell + new IntVector2(-1, 0));
        buffers.CandidateSet.Add(cell + new IntVector2(1, 0));
        buffers.CandidateSet.Add(cell + new IntVector2(0, -1));
        buffers.CandidateSet.Add(cell + new IntVector2(0, 1));
    }

    private static InputCell GetInput(StatusDiffuser diffuser, IntVector2 cell)
    {
        var diffusion = diffuser.StatusType.Diffusion!;
        var part = diffuser._boundsTracker.Ship.Parts[cell, PartRectType.Normal];
        var factors = StatusType.DiffusionSpeedFactors.Default;
        if (part != null &&
            !diffusion.PartSpeedFactors.TryGetValue(part.Rules.ID, out factors))
        {
            factors = StatusType.DiffusionSpeedFactors.Default;
        }

        return new InputCell(
            diffuser._statusStore.GetStatusList(cell),
            part,
            factors,
            diffusion.Basis);
    }

    private static float GetNeighbourDelta(
        StatusDiffuser diffuser,
        in InputCell input,
        IntVector2 neighbourCell)
    {
        var diffusion = diffuser.StatusType.Diffusion!;
        var neighbour = GetInput(diffuser, neighbourCell);
        var rawDelta = (neighbour.Value - input.Value) / 5f;
        float speed;
        float minimumDelta;
        if (neighbour.Part == null)
        {
            speed = diffusion.EmptyCellSpeedFactor * input.SpeedFactors.Empty;
            minimumDelta = 0f;
        }
        else
        {
            speed = diffusion.SpeedFactor *
                ((input.SpeedFactors.Occupied + neighbour.SpeedFactors.Occupied) / 2f);
            minimumDelta = diffusion.MinDeltaThreshold;
        }

        var delta = rawDelta * Mathx.Clamp01(speed);
        return Mathx.Abs(delta) < minimumDelta ? 0f : delta;
    }

    private static float GetSelfOccupancyDelta(
        StatusDiffuser diffuser,
        in InputCell input,
        IntVector2 cell)
    {
        var diffusion = diffuser.StatusType.Diffusion!;
        var occupancy = input.Part!.Rules.GetPhysicalOccupancyInCell(
            cell,
            input.Part.Location,
            input.Part.Rotation,
            input.Part.FlipX);
        if (occupancy <= 0f)
        {
            return 0f;
        }

        var emptyFraction = 1f - occupancy;
        var speed = diffusion.EmptyCellSpeedFactor * emptyFraction;
        var rawDelta = (diffusion.Basis - input.Value) / 5f;
        return rawDelta * speed;
    }

    private static void ApplyDiffusedValues(StatusDiffuser diffuser, Buffers buffers)
    {
        foreach (var output in buffers.Outputs)
        {
            if (output.Delta.Equals(0f))
            {
                continue;
            }

            var list = output.Input.List;
            var listCreated = list == null;
            if (listCreated &&
                !diffuser.StatusType.Filter.Validate(
                    diffuser._boundsTracker.Ship,
                    output.Cell,
                    output.Input.Part))
            {
                continue;
            }

            list ??= diffuser._statusStore.GetOrCreateStatusList(output.Cell);
            var value = output.Input.Value + output.Delta;
            if (list.Count == 0)
            {
                if (diffuser.StatusType.RemoveAtMinValue &&
                    value.Equals(diffuser.StatusType.ValueClampRange.Min))
                {
                    continue;
                }

                var status = new Status<IntVector2>(
                    output.Cell,
                    diffuser._boundsTracker.Ship.Sim!.LogicalTime,
                    -1f,
                    value);
                list.Add(status);
                buffers.Created.Add(new StatusDiffusionArgs.CreatedStatus(list, status, listCreated));
            }
            else
            {
                var status = list[0];
                var oldValue = status.Value;
                status.Value = value;
                buffers.Modified.Add(new StatusDiffusionArgs.ModifiedStatus(list, status, oldValue));
            }
        }

        StatusesDiffused(diffuser)?.Invoke(
            diffuser,
            new StatusDiffusionArgs(buffers.Modified, buffers.Created));
    }
}
