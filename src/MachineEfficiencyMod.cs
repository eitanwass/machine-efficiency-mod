using System;
using System.Collections.Generic;
using System.Linq;
using Core.Events;
using Core.Factory;
using Game.Core.Rendering;
using MonoMod.RuntimeDetour;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace MachineEfficiency
{
    /// <summary>
    /// Tints machines from red to green by how efficiently they run, with a toggle in the Visualizations panel.
    /// </summary>
    public class MachineEfficiencyMod : IMod
    {
        private readonly ILogger Logger;
        private readonly EfficiencyTracker Tracker;
        private readonly Hotkey Hotkey;
        private readonly RewirerHandle TrackerHandle;
        private readonly List<Hook> Hooks = new List<Hook>();

        private EfficiencyVisualization Visualization;

        public MachineEfficiencyMod(ILogger logger)
        {
            Logger = logger;
            Tracker = new EfficiencyTracker(logger);
            Hotkey = new Hotkey(logger);

            // Lets the tracker see the game's modules lookup, which tells it each building's lane and duration.
            TrackerHandle = GameRewirers.AddRewirer(Tracker);

            // Hooked directly because the Shifter's tick rewirer only passes deltaTime, and we need the orchestrator.
            Hooks.Add(DetourHelper.CreatePostfixHook<GameSessionOrchestrator, float>(
                original: (orchestrator, time) => orchestrator.Tick(time),
                postfix: OnTick));

            // The game assembles its per-frame drawers here; ours gets a Draw() call every frame.
#pragma warning disable CS0618 // IIslandPlatformDrawer is obsolete, but it is part of the method we must match.
            Hooks.Add(DetourHelper.CreatePostfixHook<
                GameSessionOrchestrator,
                IEnumerable<ISimulationRenderer>,
                Dictionary<IslandDefinitionId, IIslandPlatformDrawer>,
                CombinedIslandsMeshCache,
                IEnumerable<IMapSubDrawer>>(
                original: (orchestrator, renderers, platformDrawers, meshCache) =>
                    orchestrator.CreateMapSubDrawers(renderers, platformDrawers, meshCache),
                postfix: (orchestrator, renderers, platformDrawers, meshCache, drawers) =>
                    AddTintDrawer(orchestrator, drawers)));
#pragma warning restore CS0618

            // The Visualizations panel builds its entries in a private [Construct] method; ours is added after it.
            Hooks.Add(DetourHelper.CreatePostfixHook<
                HUDVisualizations,
                IFactory,
                IEventSender,
                ITutorialHighlightProvider,
                IEnumerable<HUDVisualizations.VisualizationInstance>>(
                original: (hud, factory, eventBus, tutorial) => hud.Construct(factory, eventBus, tutorial),
                postfix: (hud, factory, eventBus, tutorial, instances) => AddToggle(hud, instances)));

            logger.Info?.Log("[MachineEfficiency] Loaded.");
        }

        public void Dispose()
        {
            foreach (Hook hook in Hooks)
            {
                hook.Dispose();
            }

            GameRewirers.RemoveRewirer(TrackerHandle);
            Tracker.Dispose();
        }

        /// <summary>
        /// On until the toggle exists, so a failure to add the button never leaves the tint stuck off.
        /// </summary>
        private bool TintEnabled => Visualization == null || Visualization.IsUserEnabled;

        private void OnTick(GameSessionOrchestrator orchestrator, float deltaTime)
        {
            Hotkey.Ensure(orchestrator.InputManager?.Keybindings);
            Tracker.OnTick(orchestrator, deltaTime);
        }

        private IEnumerable<IMapSubDrawer> AddTintDrawer(
            GameSessionOrchestrator orchestrator,
            IEnumerable<IMapSubDrawer> drawers)
        {
            var tintDrawer = new EfficiencyTintDrawer(Tracker, orchestrator.Theme.BaseResources, () => TintEnabled);
            return drawers.Append(tintDrawer).ToList();
        }

        private IEnumerable<HUDVisualizations.VisualizationInstance> AddToggle(
            HUDVisualizations hud,
            IEnumerable<HUDVisualizations.VisualizationInstance> instances)
        {
            try
            {
                Visualization = new EfficiencyVisualization(hud.Player, Hotkey);
                HUDIconButton button = hud.RequestChildView(hud.UIIconButtonPrefab).PlaceAt(hud.UIVisualizationsParent);

                // Registers the icon, tooltip and click handler, the same as the panel does for its own entries.
                hud.CreateVisualization(Visualization, button);

                // Directly under the machine-conflict button.
                HUDVisualizations.VisualizationInstance conflict = instances.FirstOrDefault(
                    instance => instance.Visualization is HUDBuildingConflictIndicatorsVisualization);
                if (conflict != null)
                {
                    button.transform.SetSiblingIndex(conflict.Button.transform.GetSiblingIndex() + 1);
                }
            }
            catch (Exception exception)
            {
                Logger.Error?.Log($"[MachineEfficiency] Could not add the toggle button: {exception}");
            }

            return instances;
        }
    }
}
