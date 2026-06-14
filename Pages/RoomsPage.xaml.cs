using HomeGlass.Models;
using HomeGlass.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HomeGlass.Pages;

public sealed partial class RoomsPage : Page
{
    public RoomsPage()
    {
        InitializeComponent();
        Loaded += RoomsPage_Loaded;
    }

    private async void RoomsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadRoomsAsync();
    }

    private async Task LoadRoomsAsync()
    {
        RoomsInfoBar.IsOpen = false;

        if (AppServices.HomeAssistantAuth.CurrentConnection is null)
        {
            RoomGroupsListView.ItemsSource = Array.Empty<RoomGroupViewModel>();
            RoomsSummaryText.Text = "Connect to Home Assistant in Settings to load rooms.";
            return;
        }

        RoomsSummaryText.Text = "Loading rooms from Home Assistant...";

        try
        {
            var areasTask = AppServices.HomeAssistantWebSocket.GetAreasAsync();
            var floorsTask = TryLoadAsync(AppServices.HomeAssistantWebSocket.GetFloorsAsync);
            var devicesTask = AppServices.HomeAssistantWebSocket.GetDevicesAsync();
            var entitiesTask = AppServices.HomeAssistantWebSocket.GetEntityRegistryAsync();
            var statesTask = AppServices.HomeAssistantApi.GetStatesAsync();

            await Task.WhenAll(areasTask, floorsTask, devicesTask, entitiesTask, statesTask);

            var groups = BuildRoomGroups(
                areasTask.Result,
                floorsTask.Result,
                devicesTask.Result,
                entitiesTask.Result,
                statesTask.Result);

            var roomCount = groups.Sum(group => group.Rooms.Count);
            RoomGroupsListView.ItemsSource = groups;
            RoomsSummaryText.Text = roomCount == 1
                ? "1 room loaded from Home Assistant."
                : $"{roomCount} rooms loaded from Home Assistant.";

            if (roomCount == 0)
            {
                ShowInfo(InfoBarSeverity.Informational, "No rooms found", "Home Assistant did not report any areas yet.");
            }
        }
        catch (Exception ex)
        {
            RoomGroupsListView.ItemsSource = Array.Empty<RoomGroupViewModel>();
            RoomsSummaryText.Text = "Rooms could not be loaded.";
            ShowInfo(InfoBarSeverity.Error, "Home Assistant error", ex.Message);
        }
    }

    private static IReadOnlyList<RoomGroupViewModel> BuildRoomGroups(
        IReadOnlyList<HomeAssistantArea> areas,
        IReadOnlyList<HomeAssistantFloor> floors,
        IReadOnlyList<HomeAssistantDevice> devices,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyList<HomeAssistantEntityState> states)
    {
        var floorLookup = floors.ToDictionary(floor => floor.FloorId, floor => floor.Name, StringComparer.Ordinal);
        var devicesByArea = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.AreaId))
            .GroupBy(device => device.AreaId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var deviceAreaLookup = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.AreaId))
            .ToDictionary(device => device.Id, device => device.AreaId!, StringComparer.Ordinal);
        var statesByEntityId = states.ToDictionary(state => state.EntityId, StringComparer.Ordinal);
        var entitiesByArea = entities
            .Where(entity => entity.DisabledBy is null && entity.HiddenBy is null)
            .Select(entity => new
            {
                Entity = entity,
                AreaId = ResolveAreaId(entity, deviceAreaLookup)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.AreaId))
            .GroupBy(item => item.AreaId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Entity).ToList(), StringComparer.Ordinal);

        return areas
            .GroupBy(area => GetFloorName(area, floorLookup), StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key == "Rooms" ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var rooms = group
                    .OrderBy(area => area.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(area =>
                    {
                        devicesByArea.TryGetValue(area.AreaId, out var areaDevices);
                        entitiesByArea.TryGetValue(area.AreaId, out var areaEntities);

                        return RoomCardViewModel.FromArea(
                            area,
                            areaDevices ?? [],
                            areaEntities ?? [],
                            statesByEntityId);
                    })
                    .ToList();

                return new RoomGroupViewModel(group.Key, BuildGroupSummary(rooms), rooms);
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<HomeAssistantFloor>> TryLoadAsync(Func<CancellationToken, Task<IReadOnlyList<HomeAssistantFloor>>> loadAsync)
    {
        try
        {
            return await loadAsync(CancellationToken.None);
        }
        catch
        {
            return Array.Empty<HomeAssistantFloor>();
        }
    }

    private static string? ResolveAreaId(HomeAssistantEntityRegistryEntry entity, IReadOnlyDictionary<string, string> deviceAreaLookup)
    {
        if (!string.IsNullOrWhiteSpace(entity.AreaId))
        {
            return entity.AreaId;
        }

        return !string.IsNullOrWhiteSpace(entity.DeviceId) && deviceAreaLookup.TryGetValue(entity.DeviceId, out var areaId)
            ? areaId
            : null;
    }

    private static string GetFloorName(HomeAssistantArea area, IReadOnlyDictionary<string, string> floorLookup)
    {
        return !string.IsNullOrWhiteSpace(area.FloorId) && floorLookup.TryGetValue(area.FloorId, out var floorName)
            ? floorName
            : "Rooms";
    }

    private static string BuildGroupSummary(IReadOnlyList<RoomCardViewModel> rooms)
    {
        var deviceCount = rooms.Sum(room => room.DeviceCount);

        return $"{rooms.Count} {Pluralize(rooms.Count, "room")}, {deviceCount} {Pluralize(deviceCount, "device")}";
    }

    private static string Pluralize(int count, string noun)
    {
        return count == 1 ? noun : $"{noun}s";
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        RoomsInfoBar.Severity = severity;
        RoomsInfoBar.Title = title;
        RoomsInfoBar.Message = message;
        RoomsInfoBar.IsOpen = true;
    }
}

public sealed record RoomGroupViewModel(string Name, string Summary, IReadOnlyList<RoomCardViewModel> Rooms);

public sealed record RoomCardViewModel(
    string Name,
    string Detail,
    string IconGlyph,
    IReadOnlyList<string> StatusChips,
    int DeviceCount)
{
    public static RoomCardViewModel FromArea(
        HomeAssistantArea area,
        IReadOnlyList<HomeAssistantDevice> devices,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId)
    {
        var availableEntities = entities
            .Select(entity => statesByEntityId.TryGetValue(entity.EntityId, out var state) ? state : null)
            .OfType<HomeAssistantEntityState>()
            .ToList();

        var chips = BuildStatusChips(availableEntities, devices.Count);
        var detail = $"{devices.Count} {Pluralize(devices.Count, "device")}";

        return new RoomCardViewModel(
            area.Name,
            detail,
            GetIconGlyph(area.Icon),
            chips,
            devices.Count);
    }

    private static IReadOnlyList<string> BuildStatusChips(
        IReadOnlyList<HomeAssistantEntityState> states,
        int deviceCount)
    {
        var chips = new List<string>();

        var lightStates = states
            .Where(state => GetDomain(state.EntityId) == "light")
            .Where(IsPrimaryLight)
            .ToList();
        var lightsOn = lightStates.Count(state => state.State == "on");
        if (lightsOn > 0)
        {
            chips.Add($"{lightsOn} {Pluralize(lightsOn, "light")} on");
        }
        else if (lightStates.Count > 0)
        {
            chips.Add("Lights off");
        }

        var fans = states.Where(state => GetDomain(state.EntityId) == "fan").ToList();
        var fansOn = fans.Count(state => state.State == "on");
        if (fansOn > 0)
        {
            chips.Add($"{fansOn} {Pluralize(fansOn, "fan")} on");
        }
        else if (fans.Count > 0)
        {
            chips.Add("Fans off");
        }

        var climateChip = BuildClimateChip(states);
        if (!string.IsNullOrWhiteSpace(climateChip))
        {
            chips.Add(climateChip);
        }

        var presenceChip = BuildPresenceChip(states);
        if (!string.IsNullOrWhiteSpace(presenceChip))
        {
            chips.Add(presenceChip);
        }

        var lockChip = BuildLockChip(states);
        if (!string.IsNullOrWhiteSpace(lockChip))
        {
            chips.Add(lockChip);
        }

        var contactChip = BuildContactChip(states);
        if (!string.IsNullOrWhiteSpace(contactChip))
        {
            chips.Add(contactChip);
        }

        if (chips.Count == 0)
        {
            chips.Add(deviceCount > 0 ? "Ready" : "Empty");
        }

        return chips;
    }

    private static string GetDomain(string entityId)
    {
        var separatorIndex = entityId.IndexOf('.');
        return separatorIndex > 0 ? entityId[..separatorIndex] : string.Empty;
    }

    private static string? TryGetDeviceClass(HomeAssistantEntityState state)
    {
        return state.Attributes.TryGetProperty("device_class", out var deviceClass)
            ? deviceClass.GetString()
            : null;
    }

    private static bool IsPrimaryLight(HomeAssistantEntityState state)
    {
        if (!state.Attributes.TryGetProperty("entity_id", out var groupedEntities))
        {
            return true;
        }

        return groupedEntities.ValueKind != System.Text.Json.JsonValueKind.Array;
    }

    private static string? BuildClimateChip(IReadOnlyList<HomeAssistantEntityState> states)
    {
        var climate = states.FirstOrDefault(state => GetDomain(state.EntityId) == "climate");
        if (climate is null)
        {
            return null;
        }

        var temperature = TryGetTemperature(climate);
        if (string.IsNullOrWhiteSpace(temperature))
        {
            return NormalizeState(climate.State);
        }

        return climate.State is "off" or "unavailable" or "unknown"
            ? temperature
            : $"{temperature} {NormalizeState(climate.State)}";
    }

    private static string? BuildPresenceChip(IReadOnlyList<HomeAssistantEntityState> states)
    {
        var presenceStates = states
            .Where(state =>
                GetDomain(state.EntityId) == "binary_sensor" &&
                TryGetDeviceClass(state) is "motion" or "occupancy" or "presence")
            .ToList();

        if (presenceStates.Count == 0)
        {
            return null;
        }

        return presenceStates.Any(state => state.State == "on")
            ? "Presence detected"
            : "No presence";
    }

    private static string? BuildLockChip(IReadOnlyList<HomeAssistantEntityState> states)
    {
        var locks = states.Where(state => GetDomain(state.EntityId) == "lock").ToList();
        if (locks.Count == 0)
        {
            return null;
        }

        var unlocked = locks.Count(state => state.State is "unlocked" or "open");
        if (unlocked > 0)
        {
            return unlocked == 1 ? "Unlocked" : $"{unlocked} unlocked";
        }

        var locked = locks.Count(state => state.State == "locked");
        return locked == locks.Count ? "Locked" : null;
    }

    private static string? BuildContactChip(IReadOnlyList<HomeAssistantEntityState> states)
    {
        var contacts = states
            .Where(state =>
                GetDomain(state.EntityId) == "binary_sensor" &&
                TryGetDeviceClass(state) is "door" or "garage_door" or "opening" or "window")
            .ToList();

        if (contacts.Count == 0)
        {
            return null;
        }

        var open = contacts.Count(state => state.State == "on");
        if (open > 0)
        {
            return open == 1 ? "Open" : $"{open} open";
        }

        return "Closed";
    }

    private static string? TryGetTemperature(HomeAssistantEntityState climate)
    {
        if (!climate.Attributes.TryGetProperty("current_temperature", out var temperature))
        {
            return null;
        }

        return temperature.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when temperature.TryGetDouble(out var value) => $"{Math.Round(value)}°",
            System.Text.Json.JsonValueKind.String => temperature.GetString(),
            _ => null
        };
    }

    private static string NormalizeState(string state)
    {
        return state.Replace('_', ' ');
    }

    private static string Pluralize(int count, string noun)
    {
        return count == 1 ? noun : $"{noun}s";
    }

    private static string GetIconGlyph(string? homeAssistantIcon)
    {
        return homeAssistantIcon switch
        {
            "mdi:bed" or "mdi:bed-king" => "\uE708",
            "mdi:sofa" => "\uE7C8",
            "mdi:silverware-fork-knife" or "mdi:stove" => "\uE719",
            "mdi:desk" or "mdi:laptop" => "\uE7F8",
            "mdi:garage" => "\uE74F",
            "mdi:home" or null or "" => "\uE80F",
            _ => "\uE825"
        };
    }
}
