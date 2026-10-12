using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Game.Core.Rendering;
using Game.Core.Rendering.Culling;
using Unity.Mathematics;
using UnityEngine;

namespace MachineEfficiency
{
    /// <summary>
    /// Tints every tracked machine by drawing its own blueprint mesh over it in its band colour, once per frame.
    /// </summary>
    /// <remarks>
    /// This is how the game draws the placement ghost (<c>SmartBuildingBlueprintRenderer</c>). Static buildings are
    /// merged into one cached mesh per chunk, so recolouring them in place would mean re-baking whole chunks.
    /// </remarks>
    internal sealed class EfficiencyTintDrawer : IMapSubDrawer
    {
        // The game draws ghosts only up to this building LOD; further out the meshes are simplified planes.
        private const int MaxBuildingLod = 3;

        // Within this squared camera distance the game also writes depth first, so a ghost's inner faces do not blend
        // through each other.
        private const float DepthPrePassDistanceSq = 8100f;

        // Pulls the tint towards the camera so it does not z-fight with the machine it covers.
        private const float CameraBias = 0.01f;

        private readonly EfficiencyTracker Tracker;
        private readonly VisualThemeBaseResources Theme;
        private readonly Func<bool> IsEnabled;
        private readonly FixedShaderColorProvider[] ColorByBand;
        private readonly Dictionary<GlobalChunkCoordinate, ChunkCullResult> VisibleChunks =
            new Dictionary<GlobalChunkCoordinate, ChunkCullResult>();

        public EfficiencyTintDrawer(EfficiencyTracker tracker, VisualThemeBaseResources theme, Func<bool> isEnabled)
        {
            Tracker = tracker;
            Theme = theme;
            IsEnabled = isEnabled;

            // One property block per band, shared by every machine in it so they batch.
            ColorByBand = EfficiencyBands.Colors.Select(color => new FixedShaderColorProvider(color)).ToArray();
        }

        public void Draw(FrameDrawOptionsNoLOD options, MapCullResult cullResult)
        {
            if (!IsEnabled())
            {
                return;
            }

            CollectVisibleChunks(options, cullResult);
            if (VisibleChunks.Count == 0)
            {
                return;
            }

            // The V toggle hides building layers above the current one. The game does that in its shaders, which do
            // not reach us, so apply the same cutoff. Interpolated, so tints follow the layer-change animation.
            Viewport viewport = options.Viewport;
            bool hideUpperLayers = !viewport.ShowAllBuildingLayers;
            float layerCutoff = viewport.InterpolatedBuildingLayer + 1f;

            foreach (MachineProbe probe in Tracker.ActiveProbes)
            {
                BuildingModel building = probe.Building;
                GlobalTileCoordinate origin = building.Tile_G;

                if (hideUpperLayers && origin.BuildingLayer() >= layerCutoff)
                {
                    continue;
                }

                if (VisibleChunks.TryGetValue(origin.ToChunkCoordinate(), out ChunkCullResult chunk))
                {
                    FixedShaderColorProvider color = ColorByBand[EfficiencyBands.FromEfficiency(probe.Efficiency)];
                    DrawTint(options, building, chunk.LOD.BuildingLOD, color);
                }
            }
        }

        /// <summary>
        /// The chunks the game already frustum- and distance-culled, each with its LOD.
        /// </summary>
        private void CollectVisibleChunks(FrameDrawOptionsNoLOD options, MapCullResult cullResult)
        {
            VisibleChunks.Clear();

            List<ChunkCullResult> chunks = cullResult.Chunks;
            for (int i = 0; i < chunks.Count; i++)
            {
                ChunkCullResult chunk = chunks[i];
                if (chunk.LOD.ShouldRenderBuildings &&
                    chunk.LOD.BuildingLOD <= MaxBuildingLod &&
                    options.ShouldRenderPlatformContentsAtLayer(chunk.Chunk_G.z))
                {
                    VisibleChunks[chunk.Chunk_G] = chunk;
                }
            }
        }

        private void DrawTint(
            FrameDrawOptionsNoLOD options,
            BuildingModel building,
            int buildingLod,
            FixedShaderColorProvider color)
        {
            IBuildingDrawData drawData = building.Definition.CustomData.Get<IBuildingDrawData>();
            if (!drawData.IsolatedBlueprintMesh.TryGet(buildingLod, out var handle))
            {
                return;
            }

            IMeshReference mesh = handle;
            InstancedMeshManager buildings = options.Renderers.Buildings;

            GlobalTilePivot pivot = building.Transform.ToPivot();
            float3 position = pivot.Position.ToCenter_W();
            float3 towardsCamera = options.CameraPosition_W - position;
            float distanceSq = math.lengthsq(towardsCamera);

            Matrix4x4 transform = Matrix4x4.TRS(
                (Vector3) (position + towardsCamera / distanceSq * CameraBias),
                FastMatrix.RotateByDirection(pivot.Direction),
                Vector3.one);

            if (distanceSq < DepthPrePassDistanceSq)
            {
                buildings.Add(mesh, Theme.UXBuildingBlueprintPreWriteDepthMaterial, transform);
            }

            buildings.AddWithProperties(
                mesh,
                Theme.UXBuildingBlueprintMaterial,
                transform,
                color.PropertyBlock,
                color.PropertyBlockKey);
        }
    }
}
