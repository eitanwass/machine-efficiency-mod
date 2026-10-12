using System;
using System.Collections.Generic;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;

namespace MachineEfficiency
{
    /// <summary>
    /// Measures one machine the way the game's efficiency side panel does: it records when the machine's lane accepts
    /// items over the last 60 simulation seconds and compares the average gap to the machine's best-case duration.
    /// </summary>
    /// <remarks>
    /// Unlike the panel, a machine that stops receiving items keeps getting worse: the time since the last item
    /// counts too, so a stalled machine decays to 0% instead of freezing at its last value.
    /// </remarks>
    internal sealed class MachineProbe
    {
        private static readonly Ticks Window = Ticks.FromSeconds(60);

        // The lane hook runs inside the simulation update, which is not necessarily the thread that recomputes.
        private readonly object Gate = new object();
        private readonly Queue<Ticks> Accepted = new Queue<Ticks>();
        private readonly ISimulator Simulator;
        private readonly ILocalizedSimulation Simulation;
        private readonly IHookableItemReceiver Lane;
        private readonly ResearchSpeedId Speed;
        private readonly float BaseDuration;
        private readonly AcceptHookDelegate Previous;
        private readonly AcceptHookDelegate Ours;

        private Ticks Newest;
        private bool Detached;

        public readonly BuildingModel Building;

        /// <summary>
        /// 0..1. Starts at 0 ("has processed nothing yet") and is refined as items arrive.
        /// </summary>
        public float Efficiency { get; private set; }

        public MachineProbe(
            BuildingModel building,
            ISimulator simulator,
            ILocalizedSimulation simulation,
            IHookableItemReceiver lane,
            ResearchSpeedId speed,
            float baseDuration)
        {
            Building = building;
            Simulator = simulator;
            Simulation = simulation;
            Lane = lane;
            Speed = speed;
            BaseDuration = baseDuration;

            // Chain onto the existing hook. The side panel does the same when a building is selected (it saves our
            // hook, calls it, and restores it on close), so installing once and never reassigning keeps both working.
            Previous = lane.AcceptHook;
            Ours = OnAccept;
            lane.AcceptHook = Ours;
        }

        private Ticks Now =>
            Simulator.GetSimulationTimeFor(Simulation) + Simulator.GetSimulationUpdateDeltaTimeFor(Simulation);

        public void Detach()
        {
            Detached = true;

            // If the side panel chained over us, leave the lane alone: our hook is now inert and only forwards.
            if (ReferenceEquals(Lane.AcceptHook, Ours))
            {
                Lane.AcceptHook = Previous;
            }
        }

        public void Recompute(ISimulationSpeedsProvider speeds)
        {
            Ticks now = Now;
            float bestCaseDuration = StructureStatProcessingTime.ComputeEffectiveDuration(speeds, BaseDuration, Speed);

            lock (Gate)
            {
                DropOlderThan(now);

                // Fewer than two items means no gap to measure, which is no throughput.
                if (Accepted.Count < 2)
                {
                    Efficiency = 0f;
                    return;
                }

                double averageGap = Seconds(Newest.Value - Accepted.Peek().Value) / (Accepted.Count - 1);
                double sinceLast = Seconds(now.Value - Newest.Value);
                double gap = Math.Max(averageGap, sinceLast);

                Efficiency = gap > 0d ? (float) Math.Min(1d, bestCaseDuration / gap) : 1f;
            }
        }

        private void OnAccept(IItemReceiver receiver, ref IBeltItem item, ref Ticks remainingTicks)
        {
            if (!Detached)
            {
                Ticks acceptedAt = Now - remainingTicks;

                lock (Gate)
                {
                    DropOlderThan(acceptedAt);
                    Accepted.Enqueue(acceptedAt);
                    Newest = acceptedAt;
                }
            }

            Previous?.Invoke(receiver, ref item, ref remainingTicks);
        }

        private void DropOlderThan(Ticks time)
        {
            Ticks cutoff = time - Window;
            while (Accepted.Count > 0 && Accepted.Peek() < cutoff)
            {
                Accepted.Dequeue();
            }
        }

        private static double Seconds(long ticks)
        {
            return (double) ticks / Ticks.TicksPerSecond;
        }
    }
}
