using HomeGlass.Models;
using HomeGlass.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HomeGlass.Pages;

public sealed partial class RoomsPage : Page
{
    private CancellationTokenSource? _stateSubscriptionCts;
    private CancellationTokenSource? _refreshDebounceCts;
    private bool _isLoadingRooms;

    public RoomsPage()
    {
        InitializeComponent();
        Loaded += RoomsPage_Loaded;
        Unloaded += RoomsPage_Unloaded;
    }

    private async void RoomsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadRoomsAsync();
        StartStateSubscription();
    }

    private void RoomsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopStateSubscription();
    }

    private async Task LoadRoomsAsync()
    {
        if (_isLoadingRooms)
        {
            return;
        }

        _isLoadingRooms = true;
        RoomsInfoBar.IsOpen = false;

        try
        {
            if (AppServices.HomeAssistantAuth.CurrentConnection is null)
            {
                RoomGroupsListView.ItemsSource = Array.Empty<RoomGroupViewModel>();
                RoomsSummaryText.Text = "Connect to Home Assistant in Settings to load rooms.";
                return;
            }

            RoomsSummaryText.Text = "Loading rooms from Home Assistant...";

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
        finally
        {
            _isLoadingRooms = false;
        }
    }

    private void StartStateSubscription()
    {
        if (_stateSubscriptionCts is not null || AppServices.HomeAssistantAuth.CurrentConnection is null)
        {
            return;
        }

        _stateSubscriptionCts = new CancellationTokenSource();
        var token = _stateSubscriptionCts.Token;
        _ = Task.Run(async () =>
        {
            await AppServices.HomeAssistantWebSocket.SubscribeToStateChangesAsync(
                _ =>
                {
                    QueueRoomsRefresh();
                    return Task.CompletedTask;
                },
                token);
        }, token);
    }

    private void StopStateSubscription()
    {
        _refreshDebounceCts?.Cancel();
        _refreshDebounceCts?.Dispose();
        _refreshDebounceCts = null;

        _stateSubscriptionCts?.Cancel();
        _stateSubscriptionCts?.Dispose();
        _stateSubscriptionCts = null;
    }

    private void QueueRoomsRefresh()
    {
        _refreshDebounceCts?.Cancel();
        _refreshDebounceCts?.Dispose();
        _refreshDebounceCts = new CancellationTokenSource();
        var token = _refreshDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(750, token);
                DispatcherQueue.TryEnqueue(async () => await LoadRoomsAsync());
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
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
        return $"{rooms.Count} {Pluralize(rooms.Count, "room")}";
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
    string IconGlyph,
    IReadOnlyList<StatusChipViewModel> StatusChips,
    Brush CardBackground,
    Brush CardBorderBrush,
    Thickness CardBorderThickness,
    int DeviceCount)
{
    public static RoomCardViewModel FromArea(
        HomeAssistantArea area,
        IReadOnlyList<HomeAssistantDevice> devices,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId)
    {
        var availableEntities = entities
            .Select(entity => statesByEntityId.TryGetValue(entity.EntityId, out var state)
                ? new RoomEntityState(entity, state)
                : null)
            .OfType<RoomEntityState>()
            .ToList();

        var status = BuildStatus(availableEntities, devices.Count);

        return new RoomCardViewModel(
            area.Name,
            GetIconGlyph(area.Icon),
            status.Chips,
            status.CardBackground,
            status.CardBorderBrush,
            status.CardBorderThickness,
            devices.Count);
    }

    private static RoomStatusViewModel BuildStatus(
        IReadOnlyList<RoomEntityState> entities,
        int deviceCount)
    {
        var chips = new List<StatusChipViewModel>();
        var accentColor = Colors.Transparent;
        var hasAccent = false;

        var lightStates = entities
            .Where(entity => GetDomain(entity.State.EntityId) == "light")
            .Where(IsPrimaryLight)
            .ToList();
        var lightsOn = lightStates.Count(entity => entity.State.State == "on");
        if (lightsOn > 0)
        {
            var lightsOnStates = lightStates.Select(entity => entity.State).Where(state => state.State == "on").ToList();
            var lightDetail = BuildLightDetail(lightsOnStates);
            chips.Add(StatusChipViewModel.Active($"{lightsOn} {Pluralize(lightsOn, "light")} on{lightDetail}"));
            accentColor = GetLightAccentColor(lightsOnStates);
            hasAccent = true;
        }
        else if (lightStates.Count > 0)
        {
            chips.Add(StatusChipViewModel.Neutral("Lights off"));
        }

        var fans = entities.Where(entity => GetDomain(entity.State.EntityId) == "fan").ToList();
        var fansOn = fans.Count(entity => entity.State.State == "on");
        if (fansOn > 0)
        {
            chips.Add(StatusChipViewModel.Active($"{fansOn} {Pluralize(fansOn, "fan")} on"));
            if (!hasAccent)
            {
                accentColor = Color.FromArgb(255, 94, 234, 212);
                hasAccent = true;
            }
        }
        else if (fans.Count > 0)
        {
            chips.Add(StatusChipViewModel.Neutral("Fans off"));
        }

        var climateChip = BuildClimateChip(entities);
        if (!string.IsNullOrWhiteSpace(climateChip))
        {
            chips.Add(StatusChipViewModel.Neutral(climateChip));
        }

        var presenceChip = BuildPresenceChip(entities);
        if (presenceChip is not null)
        {
            chips.Add(presenceChip);
        }

        var lockChip = BuildLockChip(entities);
        if (lockChip is not null)
        {
            chips.Add(lockChip);
        }

        var contactChip = BuildContactChip(entities);
        if (contactChip is not null)
        {
            chips.Add(contactChip);
            if (contactChip.IsEmphasized && !hasAccent)
            {
                accentColor = Color.FromArgb(255, 255, 159, 67);
                hasAccent = true;
            }
        }

        if (chips.Count == 0)
        {
            chips.Add(StatusChipViewModel.Neutral(deviceCount > 0 ? "Ready" : "Empty"));
        }

        return hasAccent
            ? RoomStatusViewModel.Highlighted(chips, accentColor)
            : RoomStatusViewModel.Neutral(chips);
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

    private static bool IsPrimaryLight(RoomEntityState entity)
    {
        if (entity.Entity.Platform == "group")
        {
            return false;
        }

        if (entity.State.Attributes.TryGetProperty("entity_id", out var groupedEntities) &&
            groupedEntities.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            return false;
        }

        var searchableName = $"{entity.Entity.Name} {entity.Entity.OriginalName} {entity.State.EntityId}";
        if (ContainsAny(searchableName, "rgb indicator", "indicator", "status", "notification", "led"))
        {
            return false;
        }

        return true;
    }

    private static string? BuildClimateChip(IReadOnlyList<RoomEntityState> entities)
    {
        var climate = entities
            .Select(entity => entity.State)
            .FirstOrDefault(state => GetDomain(state.EntityId) == "climate");
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

    private static StatusChipViewModel? BuildPresenceChip(IReadOnlyList<RoomEntityState> entities)
    {
        var presenceStates = entities
            .Select(entity => entity.State)
            .Where(state =>
                GetDomain(state.EntityId) == "binary_sensor" &&
                TryGetDeviceClass(state) is "motion" or "occupancy" or "presence")
            .ToList();

        if (presenceStates.Count == 0)
        {
            return null;
        }

        return presenceStates.Any(state => state.State == "on")
            ? StatusChipViewModel.Active("Presence")
            : StatusChipViewModel.Neutral("Vacant");
    }

    private static StatusChipViewModel? BuildLockChip(IReadOnlyList<RoomEntityState> entities)
    {
        var locks = entities.Select(entity => entity.State).Where(state => GetDomain(state.EntityId) == "lock").ToList();
        if (locks.Count == 0)
        {
            return null;
        }

        var unlocked = locks.Count(state => state.State is "unlocked" or "open");
        if (unlocked > 0)
        {
            return StatusChipViewModel.Warning(unlocked == 1 ? "Unlocked" : $"{unlocked} unlocked");
        }

        var locked = locks.Count(state => state.State == "locked");
        return locked == locks.Count ? StatusChipViewModel.Neutral("Locked") : null;
    }

    private static StatusChipViewModel? BuildContactChip(IReadOnlyList<RoomEntityState> entities)
    {
        var contacts = entities
            .Select(entity => entity.State)
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
            return StatusChipViewModel.Warning(open == 1 ? "Open" : $"{open} open");
        }

        return StatusChipViewModel.Neutral("Closed");
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

    private static string BuildLightDetail(IReadOnlyList<HomeAssistantEntityState> lightsOn)
    {
        var brightness = lightsOn
            .Select(TryGetBrightnessPercent)
            .OfType<int>()
            .DefaultIfEmpty()
            .Average();
        var colorTemperature = lightsOn
            .Select(TryGetColorTemperature)
            .OfType<int>()
            .FirstOrDefault();

        var parts = new List<string>();
        if (brightness > 0)
        {
            parts.Add($"{Math.Round(brightness)}%");
        }

        if (colorTemperature > 0)
        {
            parts.Add($"{colorTemperature}K");
        }

        return parts.Count > 0 ? $" · {string.Join(" · ", parts)}" : string.Empty;
    }

    private static int? TryGetBrightnessPercent(HomeAssistantEntityState state)
    {
        if (!state.Attributes.TryGetProperty("brightness", out var brightness) ||
            brightness.ValueKind != System.Text.Json.JsonValueKind.Number ||
            !brightness.TryGetInt32(out var value))
        {
            return null;
        }

        return Math.Clamp((int)Math.Round(value / 255d * 100), 1, 100);
    }

    private static int? TryGetColorTemperature(HomeAssistantEntityState state)
    {
        if (state.Attributes.TryGetProperty("color_temp_kelvin", out var kelvin) &&
            kelvin.ValueKind == System.Text.Json.JsonValueKind.Number &&
            kelvin.TryGetInt32(out var kelvinValue))
        {
            return kelvinValue;
        }

        if (state.Attributes.TryGetProperty("color_temp", out var mired) &&
            mired.ValueKind == System.Text.Json.JsonValueKind.Number &&
            mired.TryGetInt32(out var miredValue) &&
            miredValue > 0)
        {
            return 1000000 / miredValue;
        }

        return null;
    }

    private static Color GetLightAccentColor(IReadOnlyList<HomeAssistantEntityState> lightsOn)
    {
        foreach (var light in lightsOn)
        {
            if (light.Attributes.TryGetProperty("rgb_color", out var rgb) &&
                rgb.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var values = rgb.EnumerateArray()
                    .Where(value => value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    .Select(value => value.GetByte())
                    .ToArray();

                if (values.Length >= 3)
                {
                    return Color.FromArgb(255, values[0], values[1], values[2]);
                }
            }
        }

        var kelvin = lightsOn.Select(TryGetColorTemperature).OfType<int>().FirstOrDefault();
        return kelvin > 0 ? ColorFromKelvin(kelvin) : Color.FromArgb(255, 255, 210, 96);
    }

    private static Color ColorFromKelvin(int kelvin)
    {
        return kelvin switch
        {
            < 3000 => Color.FromArgb(255, 255, 178, 92),
            < 4500 => Color.FromArgb(255, 255, 218, 150),
            < 6000 => Color.FromArgb(255, 225, 238, 255),
            _ => Color.FromArgb(255, 190, 218, 255)
        };
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
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

public sealed record RoomEntityState(HomeAssistantEntityRegistryEntry Entity, HomeAssistantEntityState State);

public sealed record RoomStatusViewModel(
    IReadOnlyList<StatusChipViewModel> Chips,
    Brush CardBackground,
    Brush CardBorderBrush,
    Thickness CardBorderThickness)
{
    public static RoomStatusViewModel Neutral(IReadOnlyList<StatusChipViewModel> chips)
    {
        return new RoomStatusViewModel(
            chips,
            new SolidColorBrush(Color.FromArgb(255, 48, 48, 48)),
            new SolidColorBrush(Color.FromArgb(255, 58, 58, 58)),
            new Thickness(1));
    }

    public static RoomStatusViewModel Highlighted(IReadOnlyList<StatusChipViewModel> chips, Color accent)
    {
        return new RoomStatusViewModel(
            chips,
            new SolidColorBrush(Color.FromArgb(44, accent.R, accent.G, accent.B)),
            new SolidColorBrush(Color.FromArgb(220, accent.R, accent.G, accent.B)),
            new Thickness(2));
    }
}

public sealed record StatusChipViewModel(
    string Text,
    Brush Background,
    Brush Foreground,
    bool IsEmphasized)
{
    public static StatusChipViewModel Neutral(string text)
    {
        return new StatusChipViewModel(
            text,
            new SolidColorBrush(Color.FromArgb(255, 62, 62, 62)),
            new SolidColorBrush(Colors.White),
            false);
    }

    public static StatusChipViewModel Active(string text)
    {
        return new StatusChipViewModel(
            text,
            new SolidColorBrush(Color.FromArgb(255, 255, 214, 102)),
            new SolidColorBrush(Color.FromArgb(255, 36, 28, 0)),
            true);
    }

    public static StatusChipViewModel Warning(string text)
    {
        return new StatusChipViewModel(
            text,
            new SolidColorBrush(Color.FromArgb(255, 255, 159, 67)),
            new SolidColorBrush(Color.FromArgb(255, 40, 20, 0)),
            true);
    }
}
