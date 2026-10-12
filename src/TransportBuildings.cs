using System.Collections.Generic;
using System.Linq;
using Game.Core.Content.Buildings;

namespace MachineEfficiency
{
    /// <summary>
    /// Buildings that only carry, route or read items. The game gives them an efficiency panel too, but a belt at
    /// "100%" says nothing about a factory, so they are left out. A machine transforms or produces items.
    /// </summary>
    internal static class TransportBuildings
    {
        public static HashSet<BuildingDefinitionId> Collect(GameBuildings buildings)
        {
            IEnumerable<BuildingDefinitionGroupId> groups = new[]
                {
                    buildings.BeltBuildingId,
                    buildings.BeltFilterBuildingId,
                    buildings.BeltReaderBuildingId,
                    buildings.Lift1LayerDefinitionGroupId,
                    buildings.Lift2LayerDefinitionGroupId
                }
                .Concat(buildings.SplitterVariantIds)
                .Concat(buildings.MergerVariantIds);

            var transport = new HashSet<BuildingDefinitionId>();
            foreach (BuildingDefinitionGroupId groupId in groups)
            {
                if (!buildings.TryGetDefinitionGroup(groupId, out IBuildingDefinitionGroup group))
                {
                    continue;
                }

                foreach (IBuildingDefinition definition in group.Definitions)
                {
                    transport.Add(definition.Id);
                }
            }

            // Belt ports are single definitions, not groups.
            transport.Add(buildings.BeltPortSender.Id);
            transport.Add(buildings.BeltPortReceiver.Id);

            return transport;
        }
    }
}
