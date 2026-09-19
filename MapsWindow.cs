using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using SimpleMapTracker;

namespace SimpleMapTrackerPlus;

public class MapsWindow : Window
{
    private List<MapSearchItem> maps = new();
    public MapsWindow() 
        : base("Maps in inventory")
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new System.Numerics.Vector2(300, 200), };
    }

    public void SetMaps(List<MapSearchItem> mapsInInventory) => this.maps = mapsInInventory;
    
    public override void Draw()
    {
        if (maps.Count == 0)
        {
            ImGui.Text("No maps found");
            return;
        }
        foreach (var map in maps)
        {
            ImGui.Text($"Amount: {map.Amount}x {map.Name}");
        }
    }
}