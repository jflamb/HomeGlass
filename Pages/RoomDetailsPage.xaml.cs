using HomeGlass.Models;
using HomeGlass.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace HomeGlass.Pages;

public sealed partial class RoomDetailsPage : Page
{
    private RoomCardViewModel? _room;

    public RoomDetailsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not RoomCardViewModel room)
        {
            return;
        }

        _room = room;
        RoomNameTextBlock.Text = room.Name;
        StatusItemsControl.ItemsSource = room.StatusChips;
        _ = LoadDevicesAsync(room);
    }

    private async Task LoadDevicesAsync(RoomCardViewModel room)
    {
        try
        {
            var devicesTask = AppServices.HomeAssistantWebSocket.GetDevicesAsync();
            var entitiesTask = AppServices.HomeAssistantWebSocket.GetEntityRegistryAsync();
            var statesTask = AppServices.HomeAssistantApi.GetStatesAsync();

            await Task.WhenAll(devicesTask, entitiesTask, statesTask);

            var groups = BuildDeviceGroups(
                room.AreaId,
                devicesTask.Result,
                entitiesTask.Result,
                statesTask.Result);

            DeviceGroupsListView.ItemsSource = groups;
        }
        catch
        {
            DeviceGroupsListView.ItemsSource = Array.Empty<DeviceGroupViewModel>();
        }
    }

    private static IReadOnlyList<DeviceGroupViewModel> BuildDeviceGroups(
        string areaId,
        IReadOnlyList<HomeAssistantDevice> devices,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyList<HomeAssistantEntityState> states)
    {
        var statesByEntityId = states.ToDictionary(state => state.EntityId, StringComparer.Ordinal);
        var roomDevices = devices
            .Where(device => device.AreaId == areaId)
            .ToDictionary(device => device.Id, StringComparer.Ordinal);
        var roomEntities = entities
            .Where(entity => entity.DisabledBy is null && entity.HiddenBy is null)
            .Where(entity => entity.AreaId == areaId || (!string.IsNullOrWhiteSpace(entity.DeviceId) && roomDevices.ContainsKey(entity.DeviceId)))
            .ToList();
        var entitiesByDevice = roomEntities
            .Where(entity => !string.IsNullOrWhiteSpace(entity.DeviceId))
            .GroupBy(entity => entity.DeviceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        return roomDevices.Values
            .Select(device =>
            {
                entitiesByDevice.TryGetValue(device.Id, out var deviceEntities);
                return DeviceCardViewModel.FromDevice(device, deviceEntities ?? [], statesByEntityId);
            })
            .GroupBy(device => device.Type, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => GetTypeOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var cards = group.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                return new DeviceGroupViewModel(group.Key, $"{cards.Count} {Pluralize(cards.Count, "device")}", cards);
            })
            .ToList();
    }

    private static int GetTypeOrder(string type)
    {
        return type switch
        {
            "Lights" => 0,
            "Fans" => 1,
            "Climate" => 2,
            "Locks" => 3,
            "Sensors" => 4,
            "Media" => 5,
            _ => 99
        };
    }

    private static string Pluralize(int count, string noun)
    {
        return count == 1 ? noun : $"{noun}s";
    }
}

public sealed record DeviceGroupViewModel(string Name, string Summary, IReadOnlyList<DeviceCardViewModel> Devices);

public sealed record DeviceCardViewModel(
    string Name,
    string Detail,
    string Type,
    string IconGlyph,
    IReadOnlyList<StatusChipViewModel> StatusChips,
    Brush CardBackground,
    Brush CardBorderBrush,
    Microsoft.UI.Xaml.Thickness CardBorderThickness)
{
    public static DeviceCardViewModel FromDevice(
        HomeAssistantDevice device,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId)
    {
        var states = entities
            .Select(entity => statesByEntityId.TryGetValue(entity.EntityId, out var state) ? state : null)
            .OfType<HomeAssistantEntityState>()
            .ToList();
        var primaryDomain = GetPrimaryDomain(states, entities);
        var active = IsActive(primaryDomain, states);
        var chips = BuildStatusChips(primaryDomain, states);
        var accent = active ? GetAccent(primaryDomain) : Colors.Transparent;

        return new DeviceCardViewModel(
            device.NameByUser ?? device.Name ?? device.Model ?? "Device",
            BuildDetail(device, states),
            GetTypeName(primaryDomain),
            GetIconGlyph(primaryDomain),
            chips,
            active ? new SolidColorBrush(Color.FromArgb(44, accent.R, accent.G, accent.B)) : new SolidColorBrush(Color.FromArgb(255, 48, 48, 48)),
            active ? new SolidColorBrush(Color.FromArgb(220, accent.R, accent.G, accent.B)) : new SolidColorBrush(Color.FromArgb(255, 58, 58, 58)),
            new Microsoft.UI.Xaml.Thickness(active ? 2 : 1));
    }

    private static string GetPrimaryDomain(IReadOnlyList<HomeAssistantEntityState> states, IReadOnlyList<HomeAssistantEntityRegistryEntry> entities)
    {
        var domains = states.Select(state => GetDomain(state.EntityId)).Concat(entities.Select(entity => GetDomain(entity.EntityId))).ToList();
        foreach (var domain in new[] { "light", "fan", "climate", "lock", "cover", "binary_sensor", "sensor", "media_player" })
        {
            if (domains.Contains(domain))
            {
                return domain;
            }
        }

        return domains.FirstOrDefault(domain => !string.IsNullOrWhiteSpace(domain)) ?? "device";
    }

    private static IReadOnlyList<StatusChipViewModel> BuildStatusChips(string primaryDomain, IReadOnlyList<HomeAssistantEntityState> states)
    {
        var chips = new List<StatusChipViewModel>();
        var primaryStates = states.Where(state => GetDomain(state.EntityId) == primaryDomain).ToList();

        switch (primaryDomain)
        {
            case "light":
            case "fan":
                var on = primaryStates.Count(state => state.State == "on");
                chips.Add(on > 0
                    ? StatusChipViewModel.Active($"{on} on", $"{on} {primaryDomain} entities are on.")
                    : StatusChipViewModel.Neutral("Off", $"All {primaryDomain} entities are off."));
                break;
            case "lock":
                chips.Add(primaryStates.Any(state => state.State is "unlocked" or "open")
                    ? StatusChipViewModel.Warning("Unlocked", "At least one lock entity is unlocked or open.")
                    : StatusChipViewModel.Neutral("Locked", "Lock entities are locked."));
                break;
            case "binary_sensor":
                var active = primaryStates.Count(state => state.State == "on");
                chips.Add(active > 0
                    ? StatusChipViewModel.Warning($"{active} active", $"{active} binary sensor entities are active.")
                    : StatusChipViewModel.Neutral("Clear", "Binary sensor entities are inactive."));
                break;
            case "climate":
                var climate = primaryStates.FirstOrDefault();
                chips.Add(StatusChipViewModel.Neutral(climate?.State ?? "Unknown", "Current climate state."));
                break;
            default:
                var state = primaryStates.FirstOrDefault()?.State ?? states.FirstOrDefault()?.State ?? "Unknown";
                chips.Add(StatusChipViewModel.Neutral(NormalizeState(state), "Current Home Assistant state."));
                break;
        }

        return chips;
    }

    private static bool IsActive(string primaryDomain, IReadOnlyList<HomeAssistantEntityState> states)
    {
        var primaryStates = states.Where(state => GetDomain(state.EntityId) == primaryDomain).ToList();
        return primaryDomain switch
        {
            "light" or "fan" => primaryStates.Any(state => state.State == "on"),
            "lock" => primaryStates.Any(state => state.State is "unlocked" or "open"),
            "binary_sensor" => primaryStates.Any(state => state.State == "on"),
            _ => false
        };
    }

    private static string BuildDetail(HomeAssistantDevice device, IReadOnlyList<HomeAssistantEntityState> states)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(device.Manufacturer))
        {
            parts.Add(device.Manufacturer);
        }

        if (!string.IsNullOrWhiteSpace(device.Model))
        {
            parts.Add(device.Model);
        }

        parts.Add($"{states.Count} {Pluralize(states.Count, "entity")}");
        return string.Join(" • ", parts);
    }

    private static string GetTypeName(string domain)
    {
        return domain switch
        {
            "light" => "Lights",
            "fan" => "Fans",
            "climate" => "Climate",
            "lock" => "Locks",
            "binary_sensor" or "sensor" => "Sensors",
            "media_player" => "Media",
            _ => "Other"
        };
    }

    private static string GetIconGlyph(string domain)
    {
        return domain switch
        {
            "light" => "\uE706",
            "fan" => "\uE9CA",
            "climate" => "\uE7A3",
            "lock" => "\uE72E",
            "binary_sensor" or "sensor" => "\uE9D9",
            "media_player" => "\uE8B2",
            _ => "\uE950"
        };
    }

    private static Color GetAccent(string domain)
    {
        return domain switch
        {
            "light" => Color.FromArgb(255, 255, 210, 96),
            "fan" => Color.FromArgb(255, 94, 234, 212),
            "lock" or "binary_sensor" => Color.FromArgb(255, 255, 159, 67),
            _ => Color.FromArgb(255, 96, 165, 250)
        };
    }

    private static string GetDomain(string entityId)
    {
        var separatorIndex = entityId.IndexOf('.');
        return separatorIndex > 0 ? entityId[..separatorIndex] : string.Empty;
    }

    private static string NormalizeState(string state)
    {
        return state.Replace('_', ' ');
    }

    private static string Pluralize(int count, string noun)
    {
        return count == 1 ? noun : $"{noun}s";
    }
}
