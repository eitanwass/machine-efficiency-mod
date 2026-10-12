using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace MachineEfficiency
{
    /// <summary>
    /// The tint's keybinding (default M), added to the game's own input system so it can be rebound in the settings
    /// and is ignored while typing in a text box. H is taken by "center on base"; M is the only unused letter key.
    /// </summary>
    /// <remarks>
    /// The game builds its <see cref="Keybindings"/> once at boot, before mods load, so we add a layer to the live
    /// instance. That works because the game reads it live: <c>GameInputManager</c> polls <c>Layers</c> every frame
    /// and its input context shares the id dictionary.
    /// </remarks>
    internal sealed class Hotkey
    {
        private const string LayerId = "machine-efficiency";
        private const string PartialId = "toggle-tint";
        private const string Id = LayerId + "." + PartialId;
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly ILogger Logger;
        private bool Ready;
        private bool GaveUp;

        public Hotkey(ILogger logger)
        {
            Logger = logger;
        }

        /// <summary>
        /// Makes sure the binding exists. Cheap once it does, so it is safe to call every frame.
        /// </summary>
        public void Ensure(IKeybindings keybindings)
        {
            if (Ready || GaveUp || keybindings == null)
            {
                return;
            }

            Ready = TryInject(keybindings);

            // A failure is structural (the game's types changed), not timing, and has been logged once.
            GaveUp = !Ready;
        }

        public bool WasPressed(InputDownstreamContext context)
        {
            if (!Ready)
            {
                return false;
            }

            try
            {
                return context.ConsumeWasActivated(Id);
            }
            catch (KeyNotFoundException)
            {
                // The game rebuilt its keybindings; Ensure() will add ours again.
                Ready = false;
                return false;
            }
        }

        private bool TryInject(IKeybindings keybindings)
        {
            if (keybindings.TryGet(Id, out Keybinding _))
            {
                return true;
            }

            try
            {
                if (!(keybindings is Keybindings concrete))
                {
                    Logger.Warning?.Log($"[MachineEfficiency] No hotkey: unexpected keybindings {keybindings.GetType()}.");
                    return false;
                }

                FieldInfo layersField = typeof(Keybindings).GetField("_Layers", Private);
                FieldInfo byIdField = typeof(Keybindings).GetField("_KeybindingsById", Private);
                FieldInfo changedField = typeof(Keybindings).GetField("_Changed", Private);
                if (layersField == null || byIdField == null)
                {
                    Logger.Warning?.Log("[MachineEfficiency] No hotkey: the game's Keybindings layout changed.");
                    return false;
                }

                // The layer copies the binding, assigns its full id and loads any key the player rebound earlier.
                var layer = new KeybindingsLayer(
                    LayerId,
                    new List<Keybinding> { new Keybinding(PartialId, new KeySet(KeyCode.M)) });

                var layers = (KeybindingsLayer[]) layersField.GetValue(concrete);
                layersField.SetValue(concrete, layers.Append(layer).ToArray());

                var byId = (Dictionary<string, Keybinding>) byIdField.GetValue(concrete);
                foreach (Keybinding binding in layer.Bindings)
                {
                    byId[binding.Id] = binding;
                }

                // Like the Keybindings constructor, forward layer changes so the settings screens notice rebinding.
                object changedEvent = changedField?.GetValue(concrete);
                MethodInfo invoke = changedEvent?.GetType().GetMethod("Invoke", Type.EmptyTypes);
                if (invoke != null)
                {
                    layer.Changed.Register((Action) Delegate.CreateDelegate(typeof(Action), changedEvent, invoke));
                }

                return true;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Log($"[MachineEfficiency] Could not add the hotkey: {exception}");
                return false;
            }
        }
    }
}
