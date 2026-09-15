using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.Interop;
using Lumina.Excel.Sheets;

namespace SimpleMapTracker;

public unsafe class Plugin : Window, IDalamudPlugin {
    public static Config Config { get; set; } = new();
    public static IDataManager DataManager { get; private set; } = null!;
    public static GameFunction GameFunction { get; private set; } = null!;
    public static IPluginLog Log { get; private set; } = null!;
    public static Share Share { get; set; } = new();

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem;
    private readonly ConfigWindow configWindow;
    private readonly IPartyList partyList;
    private readonly IGameInventory  gameInventory;
    private const int MinimumPartySizeForConnection = 3;
    
    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, IDataManager dataManager, IGameInteropProvider gameInteropProvider, IPluginLog pluginLog, IGameGui gameGui, IGameInventory gameInventory, ICommandManager commandManager, IPartyList partyList) : base("Simple Map Tracker", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize) {
        Config = pluginInterface.GetPluginConfig() as Config ?? new Config();
        configWindow = new ConfigWindow(Config, pluginInterface);
        RespectCloseHotkey = false;
        this.pluginInterface = pluginInterface;
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        DataManager = dataManager;
        this.partyList = partyList;
        this.gameInventory = gameInventory;
        Log = pluginLog;
        
        GameFunction = new GameFunction(gameInteropProvider);

        windowSystem = new WindowSystem(nameof(SimpleMapTracker));
        windowSystem.AddWindow(this);
        windowSystem.AddWindow(configWindow);
        
        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;

        commandManager.AddHandler("/maps", new CommandInfo((_, _) => Toggle()) { ShowInHelp = true, HelpMessage = "Open the Simple Map Tracker window" });
        framework.Update += FrameworkUpdate;
        IsOpen = Config.WindowOpen;
        TitleBarButtons = [ new TitleBarButton {
            Icon = FontAwesomeIcon.Cogs,
            Click = _ => configWindow.Toggle(),
            ShowTooltip = () => ImGui.SetTooltip("Open Config")
        }];
        
        if (Config.ConnectOnStartup && partyList.Length >= MinimumPartySizeForConnection) 
            Share.Setup();
        
        resetMapMarkersHook = gameInteropProvider.HookFromAddress<AgentMap.Delegates.ResetMapMarkers>(AgentMap.Addresses.ResetMapMarkers.Value, ResetMapMarkersDetour);
        resetMapMarkersHook.Enable();
        
        resetMiniMapMarkersHook = gameInteropProvider.HookFromAddress<AgentMap.Delegates.ResetMiniMapMarkers>(AgentMap.Addresses.ResetMiniMapMarkers.Value, ResetMiniMapMarkersDetour);
        resetMiniMapMarkersHook.Enable();
    }

    private readonly Hook<AgentMap.Delegates.ResetMapMarkers> resetMapMarkersHook;
    private readonly Hook<AgentMap.Delegates.ResetMiniMapMarkers> resetMiniMapMarkersHook;
    
    
    private void ResetMapMarkersDetour(AgentMap* agentMap) {
        resetMapMarkersHook.Original(agentMap);
        if (!Config.ShowOnMap) return;
        
        Log.Debug("Adding Map Markers");

        var i = 0;
        foreach (var m in mapMarkers) {
            var strPtr = mapMarkerStrings[i++];
            MemoryHelper.WriteString(strPtr, $"{m.Label}\0", Encoding.UTF8);
            agentMap->AddMapMarker(m.Position, 60355, 0, (byte*) strPtr, 1, 5);
        }

    }


    private void ResetMiniMapMarkersDetour(AgentMap* agentMap) {
        
        resetMiniMapMarkersHook.Original(agentMap);
        if (!Config.ShowOnMiniMap) return;
        Log.Debug("Adding Mini Map Markers");
        foreach (var m in minimapMarkers) {
            agentMap->AddMiniMapMarker(m, 60355);
        }
    }

    private readonly Stopwatch updateThrottle = Stopwatch.StartNew();
    private readonly Dictionary<(ulong, long, long), string> hashCache = new();

    private string MakeId(ulong contentId, long groupId, long groupId2) {
        if (hashCache.TryGetValue((contentId, groupId, groupId2), out var h)) return h;
        var join = new List<byte>();
        join.AddRange(BitConverter.GetBytes(contentId));
        join.AddRange(BitConverter.GetBytes(groupId));
        join.AddRange(BitConverter.GetBytes(groupId2));
        h = Convert.ToHexString(SHA256.HashData(join.ToArray()));
        hashCache.TryAdd((contentId, groupId, groupId2), h);
        return h;
    }

    private void FrameworkUpdate(IFramework framework) {
        if (updateThrottle.ElapsedMilliseconds > 1000) {
            updateThrottle.Restart();
            var contentId = PlayerState.Instance()->ContentId;
            var groupId = GroupManager.Instance()->MainGroup.PartyId;
            var groupId2 = GroupManager.Instance()->MainGroup.PartyId_2;

            if (contentId == 0 || groupId == 0 || groupId2 == 0) {
                {
                    //Disconnects the client from the server if there are no or not enough players in the party
                    if (partyList.Length is 0 or < MinimumPartySizeForConnection)
                    {
                        Share.Disconnect();
                    }
                    else if(partyList.Length >= MinimumPartySizeForConnection)
                        Share.Update(string.Empty, 0, 0, []);
                }
            } else {
                var party = GroupManager.Instance()->MainGroup.PartyMembers[..GroupManager.Instance()->MainGroup.MemberCount].ToArray()
                    .Where(p => p.ContentId != 0 && p.ContentId != contentId)
                    .Select(p => {
                        var id = MakeId(p.ContentId, groupId, groupId2);
                        var state = GetMapState(p.ContentId, p.NameString);
                        if (Share.Data.Party.TryGetValue(id, out var sharedPartyMember) && sharedPartyMember != null) {
                            state.TreasureHuntRankId = sharedPartyMember.TreasureHuntRankId;
                            state.TreasureSpotId = sharedPartyMember.TreasureSpot;
                        }

                        return id;
                    });
                //Disconnects the client from the server if there are no or not enough players in the party
                if (partyList.Length is 0 or < MinimumPartySizeForConnection)
                {
                    Share.Disconnect();
                }
                else if(partyList.Length >= MinimumPartySizeForConnection)
                    Share.Update(MakeId(contentId, groupId, groupId2), LocalPlayerMapState.Instance.TreasureHuntRankId, LocalPlayerMapState.Instance.TreasureSpotId, party);
            }
            
            UpdateMapIcons();
        }
    }

    public sealed class MultiLabelMapMarker(Vector3 position) {
        public readonly Vector3 Position = position;
        public HashSet<string> Labels = [];
        public string Label => string.Join('\n', Labels);
        public override bool Equals(object? obj) {
            return obj is MultiLabelMapMarker other && Position.Equals(other.Position);
        }

        public override int GetHashCode() {
            return Position.GetHashCode();
        }
    }
    
    private HashSet<MultiLabelMapMarker> mapMarkers = [];
    private HashSet<Vector3> minimapMarkers = [];
    
    
    private void UpdateMapIcons() {
        var oldMarkers = mapMarkers;
        var oldMinimapMarkers = minimapMarkers;
        mapMarkers = [];
        minimapMarkers = [];
        
        void AddMapForPlayer(PlayerMapState state) {

            if (state.TerritoryId != AgentMap.Instance()->CurrentTerritoryId && state.TerritoryId != AgentMap.Instance()->SelectedTerritoryId) {
                return;
            }
            
            var position = state.ActualPosition;
            if (position == null) return;
            
            var markerPosition = new Vector3(position.Value.X, position.Value.Y, position.Value.Z);

            if (state.TerritoryId == AgentMap.Instance()->CurrentTerritoryId) {
                minimapMarkers.Add(markerPosition);
            }

            if (state.TerritoryId == AgentMap.Instance()->SelectedTerritoryId && AgentMap.Instance()->IsAgentActive() && AgentMap.Instance()->AddonId != 0) {
                
                var labelMarker = new MultiLabelMapMarker(markerPosition);
                if (!mapMarkers.TryGetValue(labelMarker, out var l)) {
                    l = labelMarker;
                    mapMarkers.Add(l);
                }

                l.Labels.Add(state.Name);

            }
        }
        
        AddMapForPlayer(LocalPlayerMapState.Instance);
        for (var i = 0; i < GroupManager.Instance()->MainGroup.MemberCount; i++) {
            var gm = GroupManager.Instance()->MainGroup.PartyMembers.GetPointer(i);
            if (gm == null || gm->ContentId == PlayerState.Instance()->ContentId) continue;
            AddMapForPlayer(GetMapState(gm->ContentId, gm->NameString));
        }
        
        if (!oldMarkers.SetEquals(mapMarkers)) {
            Log.Debug("Redrawing Map Icons");
            AgentMap.Instance()->ResetMapMarkers();
        }

        if (!oldMinimapMarkers.SetEquals(minimapMarkers)) {
            Log.Debug("Redrawing Minimap Icons");
            AgentMap.Instance()->ResetMiniMapMarkers();
        }
    }

    private readonly Dictionary<ulong, PlayerMapState> mapStateCache = new();

    public PlayerMapState GetMapState(ulong contentId, string name) {
        if (mapStateCache.TryGetValue(contentId, out var state)) return state;
        state = new PlayerMapState { ContentId = contentId, Name = name };
        mapStateCache.Add(contentId, state);
        return state;
    }

    public List<PlayerMapState> GetPlayers() {
        var playerList = new List<PlayerMapState> { LocalPlayerMapState.Instance };
        foreach (var pm in GroupManager.Instance()->MainGroup.PartyMembers[..GroupManager.Instance()->MainGroup.MemberCount]) {
            if (pm.EntityId == 0xE0000000) continue;
            if (playerList.Any(p => p.ContentId == pm.ContentId)) continue;
            var partyMemberState = GetMapState(pm.ContentId, pm.NameString);
            playerList.Add(partyMemberState);
        }

        return playerList;
    }

    public override bool DrawConditions() {
        return base.DrawConditions() && PlayerState.Instance()->ContentId != 0;
    }

    public override void Draw() {
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(4))) {
            if (ImGui.BeginTable("partyMembers", 3, ImGuiTableFlags.NoClip | ImGuiTableFlags.Sortable | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit)) {
                ImGui.TableSetupColumn("Party Member", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("###controls", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                var sortSpecs = ImGui.TableGetSortSpecs();
                var players = GetPlayers();
                players.Sort((a, b) => {
                    return sortSpecs.Specs.ColumnIndex switch {
                        1 => string.Compare(a.Name, b.Name, StringComparison.InvariantCultureIgnoreCase),
                        3 => string.Compare(a.ZoneName, b.ZoneName, StringComparison.InvariantCultureIgnoreCase),
                        _ => 0
                    } * (sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending ? 1 : -1);
                });

                foreach (var p in players) {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(p.Name);
#if DEBUG
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Content ID: {p.ContentId:X}");
#endif
                    ImGui.TableNextColumn();

                    using (ImRaii.PushFont(UiBuilder.IconFont)) {
                        using (ImRaii.Disabled(p.TreasureSpot == null)) {
                            ImGui.Text(FontAwesomeIcon.MapMarked.ToIconString());
                        }

                        
                        if (ImGui.IsItemClicked() && p.TreasureSpot != null) {
                            p.OpenMapToLocation();
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(p.ZoneName);
                }

                ImGui.EndTable();
            }
        }

        using (ImRaii.PushColor(ImGuiCol.Button, 0, Share.IsSetup))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, 0, Share.IsSetup))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, 0, Share.IsSetup)) {
            var buttonSize = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeightWithSpacing());
            if (!Share.IsSetup) {
                if (ImGui.Button("Connect", buttonSize) && partyList.Length >= MinimumPartySizeForConnection)
                    Share.Setup();
            } else if (!Share.IsConnected) 
                ImGui.Button("Disconnected", buttonSize);
        }
    }
    
    public override void OnClose() {
        Config.WindowOpen = false;
        base.OnClose();
    }

    public override void OnOpen() {
        Config.WindowOpen = true;
        base.OnOpen();
    }

    private readonly List<nint> mapMarkerStrings = [
        Marshal.AllocHGlobal(512),
        Marshal.AllocHGlobal(512),
        Marshal.AllocHGlobal(512),
        Marshal.AllocHGlobal(512),
        Marshal.AllocHGlobal(512),
        Marshal.AllocHGlobal(512),
        Marshal.AllocHGlobal(512),
        Marshal.AllocHGlobal(512),
    ];
    
    public void Dispose() {
        Share.Dispose();
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        windowSystem.RemoveAllWindows();
        commandManager.RemoveHandler("/maps");
        pluginInterface.SavePluginConfig(Config);
        
        resetMapMarkersHook.Disable();
        resetMapMarkersHook.Dispose();
        resetMiniMapMarkersHook.Disable();
        resetMiniMapMarkersHook.Dispose();
        
        AgentMap.Instance()->ResetMapMarkers();
        AgentMap.Instance()->ResetMiniMapMarkers();
        
        foreach (var strPtr in mapMarkerStrings) {
            Marshal.FreeHGlobal(strPtr);
        }
    }
}
