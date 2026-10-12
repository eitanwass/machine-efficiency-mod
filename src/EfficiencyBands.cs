using System;
using UnityEngine;

namespace MachineEfficiency
{
    /// <summary>
    /// Efficiency is shown in five steps: the nearest of 0%, 25%, 50%, 75% and 100%. A band is an index into
    /// <see cref="Colors"/>.
    /// </summary>
    internal static class EfficiencyBands
    {
        public static readonly Color[] Colors =
        {
            new Color(0.90f, 0.20f, 0.20f), // 0%    red
            new Color(0.95f, 0.55f, 0.15f), // 25%   orange
            new Color(0.95f, 0.85f, 0.20f), // 50%   yellow
            new Color(0.60f, 0.85f, 0.25f), // 75%   light green
            new Color(0.20f, 0.75f, 0.30f)  // 100%  green
        };

        public static int FromEfficiency(float efficiency)
        {
            float steps = Mathf.Clamp01(efficiency) * (Colors.Length - 1);
            return (int) Math.Round(steps, MidpointRounding.AwayFromZero);
        }
    }
}
