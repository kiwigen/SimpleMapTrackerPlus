using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using SimpleMapTrackerPlus;
using MapType = Lumina.Excel.Sheets.MapType;

namespace SimpleMapTracker;

public class PlayerMapState {
    public virtual ulong ContentId { get; init; }
    public virtual string Name { get; init; } = string.Empty;
    public virtual uint TreasureHuntRankId { get; set; }
    public virtual ushort TreasureSpotId { get; set; }

    public List<MapSearchItem> Maps { get; set; } = new();
    
    public TreasureHuntRank? TreasureHuntRank =>
        TreasureHuntRankId == 0
            ? null
            : Plugin.DataManager.GetExcelSheet<TreasureHuntRank>()
                .GetRowOrDefault(TreasureHuntRankId);

    public TreasureSpot? TreasureSpot =>
        TreasureHuntRankId == 0
            ? null
            : Plugin.DataManager.GetSubrowExcelSheet<TreasureSpot>()
                .GetSubrowOrDefault(TreasureHuntRankId, TreasureSpotId);

    public string ZoneName => TreasureSpot?.Location.ValueNullable?.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText() ?? "No Open Map";

    public uint? TerritoryId => TreasureSpot?.Location.ValueNullable?.Territory.RowId;
    
    public Vector3? ActualPosition {
        get {
            var location = TreasureSpot?.Location.ValueNullable;
            if (location == null) return null;
            return new Vector3(location.Value.X, location.Value.Y, location.Value.Z);
        }
    }
    
    public unsafe void OpenMapToLocation() {
        var location = TreasureSpot?.Location.ValueNullable;
        var map = location?.Map.ValueNullable;
        var territory = map?.TerritoryType.ValueNullable;
        if (territory == null || location == null || map == null) return;
        AgentMap.Instance()->FlagMarkerCount = 0; // Clear marker
        AgentMap.Instance()->SetFlagMapMarker(territory.Value.RowId, map.Value.RowId, location.Value.X, location.Value.Z, 60354);
        AgentMap.Instance()->OpenMap(map.Value.RowId, territory.Value.RowId, $"{Name}'s Map");
    }
}
