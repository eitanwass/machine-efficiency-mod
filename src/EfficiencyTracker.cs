using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace MachineEfficiency
{
    /// <summary>
    /// Keeps one <see cref="MachineProbe"/> per machine on the local player's map and their efficiency up to date.
    /// </summary>
    /// <remarks>
    /// Also acts as the modules rewirer: the game's <see cref="BuildingsModulesLookup"/> is how we ask a building for
    /// the lane and duration its own efficiency panel uses, so every building type is handled the same way.
    /// </remarks>
    internal sealed class EfficiencyTracker : IBuildingModulesRewirer, IDisposable
    {
        private const float RecomputeIntervalSeconds = 0.5f;
        private const int MaxAttachesPerTick = 32;
        private const int MaxAttachAttempts = 240;

        private readonly ILogger Logger;
        private readonly Dictionary<BuildingId, MachineProbe> Probes = new Dictionary<BuildingId, MachineProbe>();
        private readonly Dictionary<BuildingId, PendingBuilding> Pending = new Dictionary<BuildingId, PendingBuilding>();
        private readonly List<BuildingId> PendingSnapshot = new List<BuildingId>();

        private BuildingsModulesLookup Lookup;
        private IMapModel Map;
        private HashSet<BuildingDefinitionId> Transport = new HashSet<BuildingDefinitionId>();
        private float SinceRecompute;

        private struct PendingBuilding
        {
            public BuildingModel Building;
            public int Attempts;
        }

        private enum AttachResult
        {
            NotReady,
            Attached,
            NotApplicable
        }

        public EfficiencyTracker(ILogger logger)
        {
            Logger = logger;
        }

        /// <summary>
        /// The machines currently measured. Exposed as the struct enumerator's collection to avoid a per-frame
        /// allocation in the drawer.
        /// </summary>
        public Dictionary<BuildingId, MachineProbe>.ValueCollection ActiveProbes => Probes.Values;

        public void AddModules(BuildingsModulesLookup modulesLookup)
        {
            Lookup = modulesLookup;
        }

        public void OnTick(GameSessionOrchestrator orchestrator, float deltaTime)
        {
            IMapModel map = orchestrator.LocalPlayer?.CurrentMap;
            if (!ReferenceEquals(map, Map))
            {
                SwitchMap(map, orchestrator);
            }

            if (Map == null || Lookup == null)
            {
                return;
            }

            AttachPending();

            SinceRecompute += deltaTime;
            if (SinceRecompute < RecomputeIntervalSeconds)
            {
                return;
            }

            SinceRecompute = 0f;
            ISimulationSpeedsProvider speeds = orchestrator.SimulationSpeedsOrchestrator;
            if (speeds == null)
            {
                return;
            }

            foreach (MachineProbe probe in Probes.Values)
            {
                probe.Recompute(speeds);
            }
        }

        public void Dispose()
        {
            DetachMap();
            ClearProbes();
        }

        private void SwitchMap(IMapModel map, GameSessionOrchestrator orchestrator)
        {
            DetachMap();
            ClearProbes();

            try
            {
                Transport = TransportBuildings.Collect(orchestrator.Mode.Buildings);
            }
            catch (Exception exception)
            {
                // Without the set belts would be tinted too; say so instead of failing silently.
                Transport.Clear();
                Logger.Warning?.Log($"[MachineEfficiency] Could not list transport buildings: {exception.Message}");
            }

            Map = map;
            if (Map == null)
            {
                return;
            }

            Map.OnBuildingAdded.Register(OnBuildingAdded);
            Map.OnBeforeBuildingRemoved.Register(OnBuildingRemoved);

            foreach (BuildingModel building in Map.Buildings)
            {
                OnBuildingAdded(building);
            }

            Logger.Info?.Log($"[MachineEfficiency] Attached to map: {Pending.Count} buildings, " +
                             $"{Transport.Count} transport definitions excluded.");
        }

        private void DetachMap()
        {
            if (Map == null)
            {
                return;
            }

            Map.OnBuildingAdded.Unregister(OnBuildingAdded);
            Map.OnBeforeBuildingRemoved.Unregister(OnBuildingRemoved);
            Map = null;
        }

        private void ClearProbes()
        {
            foreach (MachineProbe probe in Probes.Values)
            {
                probe.Detach();
            }

            Probes.Clear();
            Pending.Clear();
        }

        private void OnBuildingAdded(BuildingModel building)
        {
            Pending[building.Id] = new PendingBuilding { Building = building };
        }

        private void OnBuildingRemoved(BuildingModel building)
        {
            Pending.Remove(building.Id);

            if (Probes.TryGetValue(building.Id, out MachineProbe probe))
            {
                probe.Detach();
                Probes.Remove(building.Id);
            }
        }

        /// <summary>
        /// A building's simulation appears some time after the building itself, so attaching is retried for a
        /// while. Buildings that will never have a probe (decorations, transport) are dropped on the first try.
        /// </summary>
        private void AttachPending()
        {
            if (Pending.Count == 0)
            {
                return;
            }

            PendingSnapshot.Clear();
            PendingSnapshot.AddRange(Pending.Keys);

            int attached = 0;
            foreach (BuildingId id in PendingSnapshot)
            {
                if (attached >= MaxAttachesPerTick)
                {
                    break;
                }

                PendingBuilding pending = Pending[id];
                AttachResult result = TryAttach(pending.Building);

                if (result == AttachResult.NotReady && ++pending.Attempts < MaxAttachAttempts)
                {
                    Pending[id] = pending;
                    continue;
                }

                Pending.Remove(id);
                if (result == AttachResult.Attached)
                {
                    attached++;
                }
            }
        }

        private AttachResult TryAttach(BuildingModel building)
        {
            if (Transport.Contains(building.Definition.Id) || !building.IsValid)
            {
                return AttachResult.NotApplicable;
            }

            GlobalTileCoordinate tile = building.Tile_G;
            if (!Map.Simulator.TryFindTileSimulation(in tile, out ILocalizedTileSimulation _))
            {
                return AttachResult.NotReady;
            }

            HUDSidePanelModuleBuildingEfficiency.Data data;
            try
            {
                data = Lookup.GetInfoModules(Map, building)
                             .OfType<HUDSidePanelModuleBuildingEfficiency.Data>()
                             .FirstOrDefault();
            }
            catch (Exception exception)
            {
                Logger.Warning?.Log($"[MachineEfficiency] Could not read modules of {building}: {exception.Message}");
                return AttachResult.NotApplicable;
            }

            if (data == null || !(data.TargetLane is IHookableItemReceiver lane))
            {
                return AttachResult.NotApplicable;
            }

            Probes[building.Id] = new MachineProbe(
                building,
                Map.Simulator,
                data.Simulation,
                lane,
                data.BeltBuildingSpeed,
                data.BeltBuildingDuration);

            return AttachResult.Attached;
        }
    }
}
