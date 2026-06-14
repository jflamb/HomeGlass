using HomeGlass.Models;
using HomeGlass.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Text.Json;
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
            "Media" => 4,
            "Sensors" => 5,
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
    Microsoft.UI.Xaml.Thickness CardBorderThickness,
    double CardOpacity)
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
        var unavailable = IsUnavailable(primaryDomain, states);
        var active = !unavailable && IsActive(primaryDomain, states);
        var chips = BuildStatusChips(primaryDomain, states);
        var accent = active ? GetAccent(primaryDomain) : Colors.Transparent;

        return new DeviceCardViewModel(
            device.NameByUser ?? device.Name ?? device.Model ?? "Device",
            BuildDetail(device, entities, states, primaryDomain),
            GetTypeName(primaryDomain),
            GetIconGlyph(primaryDomain),
            chips,
            unavailable
                ? new SolidColorBrush(Color.FromArgb(255, 38, 38, 38))
                : active ? new SolidColorBrush(Color.FromArgb(44, accent.R, accent.G, accent.B)) : new SolidColorBrush(Color.FromArgb(255, 48, 48, 48)),
            unavailable
                ? new SolidColorBrush(Color.FromArgb(255, 50, 50, 50))
                : active ? new SolidColorBrush(Color.FromArgb(220, accent.R, accent.G, accent.B)) : new SolidColorBrush(Color.FromArgb(255, 58, 58, 58)),
            new Microsoft.UI.Xaml.Thickness(active ? 2 : 1),
            unavailable ? 0.58 : 1);
    }

    private static string GetPrimaryDomain(IReadOnlyList<HomeAssistantEntityState> states, IReadOnlyList<HomeAssistantEntityRegistryEntry> entities)
    {
        var domains = states.Select(state => GetDomain(state.EntityId)).Concat(entities.Select(entity => GetDomain(entity.EntityId))).ToList();
        foreach (var domain in new[] { "light", "fan", "climate", "lock", "cover", "media_player", "binary_sensor", "sensor" })
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
        if (IsUnavailable(primaryDomain, states))
        {
            chips.Add(UnavailableChip());
            return chips;
        }

        switch (primaryDomain)
        {
            case "light":
            case "fan":
                var on = primaryStates.Count(state => state.State == "on");
                chips.Add(on > 0
                    ? StatusChipViewModel.Active($"{on} on", $"{on} {GetFriendlyDomainName(primaryDomain, on)} on.")
                    : StatusChipViewModel.Neutral("Off", $"All {GetFriendlyDomainName(primaryDomain, 2)} off."));
                break;
            case "lock":
                chips.Add(primaryStates.Any(state => state.State is "unlocked" or "open")
                    ? StatusChipViewModel.Warning("Unlocked", "At least one lock is unlocked or open.")
                    : StatusChipViewModel.Neutral("Locked", "Locks are locked."));
                break;
            case "media_player":
                var media = primaryStates.FirstOrDefault(state => state.State is "playing" or "paused")
                    ?? primaryStates.FirstOrDefault();
                chips.Add(media is null
                    ? StatusChipViewModel.Neutral("Unknown", "No media state is available.")
                    : StatusChipViewModel.Neutral(NormalizeState(media.State), "Current media state."));
                break;
            case "binary_sensor":
                var active = primaryStates.Count(state => state.State == "on");
                chips.Add(active > 0
                    ? StatusChipViewModel.Warning($"{active} active", $"{active} sensor {Pluralize(active, "state")} active.")
                    : StatusChipViewModel.Neutral("Clear", "Sensors are inactive."));
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
            "media_player" => primaryStates.Any(state => state.State == "playing"),
            "binary_sensor" => primaryStates.Any(state => state.State == "on"),
            _ => false
        };
    }

    private static bool IsUnavailable(string primaryDomain, IReadOnlyList<HomeAssistantEntityState> states)
    {
        if (states.Count == 0)
        {
            return false;
        }

        var primaryStates = states.Where(state => GetDomain(state.EntityId) == primaryDomain).ToList();
        var relevantStates = primaryStates.Count > 0 ? primaryStates : states;
        return relevantStates.All(state => state.State is "unavailable" or "unknown");
    }

    private static StatusChipViewModel UnavailableChip()
    {
        return new StatusChipViewModel(
            "Unavailable",
            "Home Assistant is reporting this device as unavailable, so it cannot be controlled right now.",
            new SolidColorBrush(Color.FromArgb(255, 52, 52, 52)),
            new SolidColorBrush(Color.FromArgb(255, 190, 190, 190)),
            false,
            "unavailable");
    }

    private static string BuildDetail(
        HomeAssistantDevice device,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyList<HomeAssistantEntityState> states,
        string primaryDomain)
    {
        if (primaryDomain == "light" && TryGetLightGroupDescription(device, entities, states, out var groupDescription))
        {
            return groupDescription;
        }

        var manufacturer = NormalizeManufacturer(device.Manufacturer);
        var model = NormalizeModel(device.Model);
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return model ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(model) || StartsWithNormalized(model, manufacturer))
        {
            return model ?? manufacturer;
        }

        return $"{manufacturer} {model}";
    }

    private static bool TryGetLightGroupDescription(
        HomeAssistantDevice device,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyList<HomeAssistantEntityState> states,
        out string description)
    {
        var model = device.Model ?? string.Empty;
        var manufacturer = NormalizeManufacturer(device.Manufacturer);

        if (model.Contains("Hue Room", StringComparison.OrdinalIgnoreCase))
        {
            description = "Philips Hue room";
            return true;
        }

        if (model.Contains("Hue Zone", StringComparison.OrdinalIgnoreCase))
        {
            description = "Philips Hue zone";
            return true;
        }

        var isHomeAssistantGroup = entities.Any(entity => string.Equals(entity.Platform, "group", StringComparison.OrdinalIgnoreCase))
            || states.Any(HasGroupedEntityList);
        if (isHomeAssistantGroup)
        {
            description = string.Equals(manufacturer, "Philips", StringComparison.OrdinalIgnoreCase)
                ? "Philips Hue group"
                : "Light group";
            return true;
        }

        description = string.Empty;
        return false;
    }

    private static bool HasGroupedEntityList(HomeAssistantEntityState state)
    {
        return state.Attributes.ValueKind == JsonValueKind.Object
            && state.Attributes.TryGetProperty("entity_id", out var entityIds)
            && entityIds.ValueKind == JsonValueKind.Array
            && entityIds.GetArrayLength() > 0;
    }

    private static string? NormalizeManufacturer(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)
            || manufacturer.Contains("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return manufacturer.Trim() switch
        {
            var value when value.Contains("Signify", StringComparison.OrdinalIgnoreCase) => "Philips",
            "Ubiquiti Networks" => "Ubiquiti",
            _ => manufacturer.Trim()
        };
    }

    private static string? NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)
            || model.Contains("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return model.Trim();
    }

    private static bool StartsWithNormalized(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (value.Length == prefix.Length || !char.IsLetterOrDigit(value[prefix.Length]));
    }

    private static string GetTypeName(string domain)
    {
        return domain switch
        {
            "light" => "Lights",
            "fan" => "Fans",
            "climate" => "Climate",
            "lock" => "Locks",
            "media_player" => "Media",
            "binary_sensor" or "sensor" => "Sensors",
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
            "media_player" => "\uE8B2",
            "binary_sensor" or "sensor" => "\uE9D9",
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

    private static string GetFriendlyDomainName(string domain, int count)
    {
        return domain switch
        {
            "light" => Pluralize(count, "light"),
            "fan" => Pluralize(count, "fan"),
            _ => Pluralize(count, "device")
        };
    }

    private static string Pluralize(int count, string noun)
    {
        return count == 1 ? noun : $"{noun}s";
    }
}
