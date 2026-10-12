using Core.Localization;
using UnityEngine;

namespace MachineEfficiency
{
    /// <summary>
    /// The tint's on/off toggle, as an entry in the game's Visualizations panel. The base class provides the icon
    /// button, the tooltip and the persisted on/off state.
    /// </summary>
    internal sealed class EfficiencyVisualization : HUDVisualization
    {
        private readonly Hotkey Hotkey;

        // The base class keeps a viewport for subclasses that need one (the island grid does). Ours does not, and the
        // Visualizations panel has none to hand over.
        public EfficiencyVisualization(Player player, Hotkey hotkey)
            : base(player, viewport: null, id: "machine-efficiency", defaultActive: true)
        {
            Hotkey = hotkey;
        }

        public override bool IsAvailable => true;

        public override Sprite GetIcon()
        {
            // The game has no dedicated efficiency sprite in code; this is the processing-speed icon its efficiency
            // panel shows. Globals.Resources is obsolete, but it is how the game's own visualizations get icons.
#pragma warning disable CS0618
            return Globals.Resources.Icons.StatSpeed;
#pragma warning restore CS0618
        }

        public override IText GetTitle()
        {
            return new RawText("Machine efficiency tint");
        }

        public override void OnGameUpdate(InputDownstreamContext context, FrameDrawOptions options)
        {
            base.OnGameUpdate(context, options);

            if (Hotkey.WasPressed(context))
            {
                ToggleEnabled();
            }
        }
    }
}
