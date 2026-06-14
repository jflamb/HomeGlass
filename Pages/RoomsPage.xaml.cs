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
        var entityCount = rooms.Sum(room => room.EntityCount);

        return $"{rooms.Count} {Pluralize(rooms.Count, "room")}, {deviceCount} {Pluralize(deviceCount, "device")}, {entityCount} {Pluralize(entityCount, "entity")}";
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
    int DeviceCount,
    int EntityCount)
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

        var chips = BuildStatusChips(availableEntities, devices.Count, entities.Count);
        var detail = $"{devices.Count} {Pluralize(devices.Count, "device")} • {entities.Count} {Pluralize(entities.Count, "entity")}";

        return new RoomCardViewModel(
            area.Name,
            detail,
            GetIconGlyph(area.Icon),
            chips,
            devices.Count,
            entities.Count);
    }

    private static IReadOnlyList<string> BuildStatusChips(
        IReadOnlyList<HomeAssistantEntityState> states,
        int deviceCount,
        int entityCount)
    {
        var chips = new List<string>();

        var lightsOn = states.Count(state => GetDomain(state.EntityId) == "light" && state.State == "on");
        if (lightsOn > 0)
        {
            chips.Add($"{lightsOn} {Pluralize(lightsOn, "light")} on");
        }

        var unlocked = states.Count(state =>
            GetDomain(state.EntityId) == "lock" &&
            (state.State == "unlocked" || state.State == "open"));
        if (unlocked > 0)
        {
            chips.Add($"{unlocked} unlocked");
        }

        var activeMotion = states.Count(state =>
            GetDomain(state.EntityId) == "binary_sensor" &&
            state.State == "on" &&
            TryGetDeviceClass(state) is "motion" or "occupancy" or "presence");
        if (activeMotion > 0)
        {
            chips.Add("Motion detected");
        }

        var climate = states.FirstOrDefault(state => GetDomain(state.EntityId) == "climate");
        var temperature = TryGetTemperature(climate);
        if (!string.IsNullOrWhiteSpace(temperature))
        {
            chips.Add(temperature);
        }

        var unavailable = states.Count(state => state.State is "unavailable" or "unknown");
        if (unavailable > 0)
        {
            chips.Add($"{unavailable} unavailable");
        }

        if (chips.Count == 0)
        {
            chips.Add(entityCount > 0 || deviceCount > 0 ? "Ready" : "Empty");
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

    private static string? TryGetTemperature(HomeAssistantEntityState? climate)
    {
        if (climate is null || !climate.Attributes.TryGetProperty("current_temperature", out var temperature))
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
