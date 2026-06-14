using HomeGlass.Models;
using HomeGlass.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Text.Json;
using Windows.UI;

namespace HomeGlass.Pages;

public sealed partial class RoomDetailsPage : Page
{
    private RoomCardViewModel? _room;
    private IReadOnlyList<DeviceGroupViewModel> _deviceGroups = [];

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
                room.Name,
                devicesTask.Result,
                entitiesTask.Result,
                statesTask.Result);

            _deviceGroups = groups;
            DeviceGroupsListView.ItemsSource = groups;
        }
        catch
        {
            _deviceGroups = [];
            DeviceGroupsListView.ItemsSource = Array.Empty<DeviceGroupViewModel>();
        }
    }

    private async void ToggleDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: DeviceCardViewModel device })
        {
            await ToggleDeviceAsync(device);
        }
    }

    private async void SetDevicePowerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: DeviceCardViewModel device, Tag: string service })
        {
            return;
        }

        await CallDeviceServiceAsync(device, service);
    }

    private async void ApplyLightControlsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: DeviceCardViewModel device } || device.PrimaryDomain != "light")
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["entity_id"] = device.PrimaryEntityId,
            ["brightness_pct"] = (int)Math.Round(device.BrightnessPercent)
        };

        if (device.SupportsColorTemperature)
        {
            payload["color_temp_kelvin"] = (int)Math.Round(device.ColorTemperatureKelvin);
        }

        await CallDeviceServiceAsync(device, "turn_on", payload);
    }

    private async void SetLightColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: DeviceCardViewModel device, Tag: string rgb } ||
            device.PrimaryDomain != "light")
        {
            return;
        }

        var values = rgb
            .Split(',')
            .Select(value => int.TryParse(value, out var number) ? number : -1)
            .ToArray();
        if (values.Length != 3 || values.Any(value => value is < 0 or > 255))
        {
            return;
        }

        await CallDeviceServiceAsync(
            device,
            "turn_on",
            new
            {
                entity_id = device.PrimaryEntityId,
                rgb_color = values,
                brightness_pct = (int)Math.Round(device.BrightnessPercent)
            });
    }

    private async Task ToggleDeviceAsync(DeviceCardViewModel device)
    {
        if (!device.IsEnabled || !device.IsControllable)
        {
            return;
        }

        await CallDeviceServiceAsync(device, "toggle");
    }

    private async Task CallDeviceServiceAsync(
        DeviceCardViewModel device,
        string service,
        object? payload = null)
    {
        if (string.IsNullOrWhiteSpace(device.PrimaryEntityId))
        {
            return;
        }

        device.IsBusy = true;

        try
        {
            await AppServices.HomeAssistantApi.CallServiceAsync(
                device.PrimaryDomain,
                service,
                payload ?? new { entity_id = device.PrimaryEntityId });

            if (_room is not null)
            {
                await LoadDevicesAsync(_room);
            }
        }
        catch
        {
            // Keep the current card stable; the next live state refresh can recover.
        }
        finally
        {
            device.IsBusy = false;
        }
    }

    private static IReadOnlyList<DeviceGroupViewModel> BuildDeviceGroups(
        string areaId,
        string roomName,
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
                return DeviceCardViewModel.FromDevice(device, roomName, deviceEntities ?? [], roomEntities, statesByEntityId);
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

public sealed record LightFeatureSet(
    bool SupportsBrightness,
    bool SupportsColorTemperature,
    bool SupportsColor,
    int MinColorTemperatureKelvin,
    int MaxColorTemperatureKelvin)
{
    public static LightFeatureSet None { get; } = new(false, false, false, 2000, 6500);
}

public sealed class DeviceCardViewModel : ObservableObject
{
    private bool _isBusy;
    private double _brightnessPercent;
    private double _colorTemperatureKelvin;

    private DeviceCardViewModel(
        string name,
        string detail,
        string type,
        string iconGlyph,
        IReadOnlyList<StatusChipViewModel> statusChips,
        Brush cardBackground,
        Brush cardBorderBrush,
        Thickness cardBorderThickness,
        double cardOpacity,
        string primaryDomain,
        string? primaryEntityId,
        bool isControllable,
        bool supportsBrightness,
        bool supportsColorTemperature,
        bool supportsColor,
        int brightnessPercent,
        int colorTemperatureKelvin,
        int minColorTemperatureKelvin,
        int maxColorTemperatureKelvin,
        bool isOn)
    {
        Name = name;
        Detail = detail;
        Type = type;
        IconGlyph = iconGlyph;
        StatusChips = statusChips;
        CardBackground = cardBackground;
        CardBorderBrush = cardBorderBrush;
        CardBorderThickness = cardBorderThickness;
        CardOpacity = cardOpacity;
        PrimaryDomain = primaryDomain;
        PrimaryEntityId = primaryEntityId;
        IsControllable = isControllable;
        SupportsBrightness = supportsBrightness;
        SupportsColorTemperature = supportsColorTemperature;
        SupportsColor = supportsColor;
        _brightnessPercent = brightnessPercent;
        _colorTemperatureKelvin = colorTemperatureKelvin;
        MinColorTemperatureKelvin = minColorTemperatureKelvin;
        MaxColorTemperatureKelvin = maxColorTemperatureKelvin;
        IsOn = isOn;
    }

    public string Name { get; }

    public string Detail { get; }

    public string Type { get; }

    public string IconGlyph { get; }

    public IReadOnlyList<StatusChipViewModel> StatusChips { get; }

    public Brush CardBackground { get; }

    public Brush CardBorderBrush { get; }

    public Thickness CardBorderThickness { get; }

    public double CardOpacity { get; }

    public string PrimaryDomain { get; }

    public string? PrimaryEntityId { get; }

    public bool IsControllable { get; }

    public bool SupportsBrightness { get; }

    public bool SupportsColorTemperature { get; }

    public bool SupportsColor { get; }

    public int MinColorTemperatureKelvin { get; }

    public int MaxColorTemperatureKelvin { get; }

    public bool IsOn { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(BusyVisibility));
            OnPropertyChanged(nameof(IconVisibility));
            OnPropertyChanged(nameof(ToggleToolTip));
        }
    }

    public bool IsEnabled => IsControllable && !IsBusy;

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IconVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DetailControlsVisibility => IsControllable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LightControlsVisibility => PrimaryDomain == "light" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BrightnessVisibility => SupportsBrightness ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ColorTemperatureVisibility => SupportsColorTemperature ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ColorVisibility => SupportsColor ? Visibility.Visible : Visibility.Collapsed;

    public string ToggleToolTip => IsBusy
        ? "Sending command..."
        : PrimaryDomain switch
        {
            "light" => IsOn ? "Turn off light" : "Turn on light",
            "fan" => IsOn ? "Turn off fan" : "Turn on fan",
            _ => Type
        };

    public string ControlSummary => PrimaryDomain switch
    {
        "light" => "Adjust power, brightness, and color temperature for this light.",
        "fan" => "Turn this fan on or off.",
        _ => "Control this device."
    };

    public double BrightnessPercent
    {
        get => _brightnessPercent;
        set
        {
            SetProperty(ref _brightnessPercent, value);
            OnPropertyChanged(nameof(BrightnessLabel));
        }
    }

    public string BrightnessLabel => $"Brightness {Math.Round(BrightnessPercent)}%";

    public double ColorTemperatureKelvin
    {
        get => _colorTemperatureKelvin;
        set
        {
            SetProperty(ref _colorTemperatureKelvin, value);
            OnPropertyChanged(nameof(ColorTemperatureLabel));
        }
    }

    public string ColorTemperatureLabel => $"Color temperature {Math.Round(ColorTemperatureKelvin)}K";

    public static DeviceCardViewModel FromDevice(
        HomeAssistantDevice device,
        string roomName,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId)
    {
        var states = entities
            .Select(entity => statesByEntityId.TryGetValue(entity.EntityId, out var state) ? state : null)
            .OfType<HomeAssistantEntityState>()
            .ToList();
        var primaryDomain = GetPrimaryDomain(device, states, entities);
        var primaryStates = states.Where(state => GetDomain(state.EntityId) == primaryDomain).ToList();
        var primaryState = primaryStates.FirstOrDefault(state => state.State == "on")
            ?? primaryStates.FirstOrDefault();
        var unavailable = IsUnavailable(primaryDomain, states);
        var active = !unavailable && IsActive(primaryDomain, states);
        var isLightGroup = primaryDomain == "light" && IsLightGroupDevice(device, entities, states);
        var chips = BuildStatusChips(primaryDomain, states, roomEntities, statesByEntityId, isLightGroup);
        var accent = active ? GetAccent(primaryDomain) : Colors.Transparent;
        var isControllable = !unavailable
            && primaryState is not null
            && primaryDomain is "light" or "fan";
        var lightFeatures = GetLightFeatures(primaryStates);

        return new DeviceCardViewModel(
            TrimRoomPrefix(device.NameByUser ?? device.Name ?? device.Model ?? "Device", roomName),
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
            new Thickness(active ? 2 : 1),
            unavailable ? 0.58 : 1,
            primaryDomain,
            primaryState?.EntityId,
            isControllable,
            lightFeatures.SupportsBrightness,
            lightFeatures.SupportsColorTemperature,
            lightFeatures.SupportsColor,
            primaryState is null ? 100 : TryGetBrightnessPercent(primaryState) ?? 100,
            primaryState is null ? 3000 : TryGetColorTemperature(primaryState) ?? 3000,
            lightFeatures.MinColorTemperatureKelvin,
            lightFeatures.MaxColorTemperatureKelvin,
            primaryStates.Any(state => state.State == "on"));
    }

    private static string GetPrimaryDomain(
        HomeAssistantDevice device,
        IReadOnlyList<HomeAssistantEntityState> states,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities)
    {
        var domains = states.Select(state => GetDomain(state.EntityId)).Concat(entities.Select(entity => GetDomain(entity.EntityId))).ToList();
        if (domains.Contains("fan") || LooksLikeFanDevice(device))
        {
            return "fan";
        }

        foreach (var domain in new[] { "light", "fan", "climate", "lock", "cover", "media_player", "binary_sensor", "sensor" })
        {
            if (domains.Contains(domain))
            {
                return domain;
            }
        }

        return domains.FirstOrDefault(domain => !string.IsNullOrWhiteSpace(domain)) ?? "device";
    }

    private static bool LooksLikeFanDevice(HomeAssistantDevice device)
    {
        return ContainsWord(device.NameByUser, "fan")
            || ContainsWord(device.Name, "fan")
            || ContainsWord(device.Model, "fan")
            || ContainsWord(device.Manufacturer, "Big Ass Fans")
            || ContainsWord(device.Model, "Haiku");
    }

    private static bool ContainsWord(string? value, string word)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(word, StringComparison.CurrentCultureIgnoreCase);
    }

    private static IReadOnlyList<StatusChipViewModel> BuildStatusChips(
        string primaryDomain,
        IReadOnlyList<HomeAssistantEntityState> states,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId,
        bool isLightGroup)
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
                var memberChips = BuildGroupedLightChips(primaryStates, roomEntities, statesByEntityId);
                if (memberChips.Count > 0)
                {
                    chips.AddRange(memberChips);
                    break;
                }

                if (isLightGroup)
                {
                    var groupOn = primaryStates.Any(state => state.State == "on");
                    chips.Add(groupOn
                        ? StatusChipViewModel.Active("Group on", "This grouped light is on.")
                        : StatusChipViewModel.Neutral("Group off", "This grouped light is off."));
                    break;
                }

                goto case "fan";
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
            case "sensor":
                var battery = primaryStates.FirstOrDefault(IsBatterySensor);
                if (battery is not null && int.TryParse(battery.State, out var batteryLevel))
                {
                    chips.Add(StatusChipViewModel.Neutral($"🔋 {batteryLevel}%", $"Battery level is {batteryLevel}%."));
                    break;
                }

                var sensorState = primaryStates.FirstOrDefault()?.State ?? "Unknown";
                chips.Add(StatusChipViewModel.Neutral(NormalizeState(sensorState), "Current sensor state."));
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

    private static IReadOnlyList<StatusChipViewModel> BuildGroupedLightChips(
        IReadOnlyList<HomeAssistantEntityState> lightStates,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId)
    {
        var groupedEntityIds = lightStates
            .SelectMany(GetGroupedEntityIds)
            .Distinct(StringComparer.Ordinal)
            .Where(entityId => GetDomain(entityId) == "light")
            .ToList();

        if (groupedEntityIds.Count == 0)
        {
            return [];
        }

        var roomEntityIds = roomEntities
            .Select(entity => entity.EntityId)
            .ToHashSet(StringComparer.Ordinal);
        var visibleMemberIds = groupedEntityIds
            .Where(roomEntityIds.Contains)
            .ToList();

        if (visibleMemberIds.Count == 0)
        {
            return [];
        }

        return visibleMemberIds
            .Select(entityId =>
            {
                statesByEntityId.TryGetValue(entityId, out var state);
                var label = GetFriendlyEntityName(entityId, roomEntities, state);
                return state?.State == "on"
                    ? StatusChipViewModel.Active($"{label} on", $"{label} is on.")
                    : StatusChipViewModel.Neutral($"{label} off", $"{label} is off.");
            })
            .ToList();
    }

    private static IEnumerable<string> GetGroupedEntityIds(HomeAssistantEntityState state)
    {
        if (state.Attributes.ValueKind != JsonValueKind.Object ||
            !state.Attributes.TryGetProperty("entity_id", out var entityIds) ||
            entityIds.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entityId in entityIds.EnumerateArray())
        {
            var value = entityId.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static string GetFriendlyEntityName(
        string entityId,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities,
        HomeAssistantEntityState? state)
    {
        var registryName = roomEntities.FirstOrDefault(entity => entity.EntityId == entityId)?.Name
            ?? roomEntities.FirstOrDefault(entity => entity.EntityId == entityId)?.OriginalName;
        var friendlyName = TryGetAttributeString(state, "friendly_name");
        var name = registryName ?? friendlyName ?? entityId[(entityId.IndexOf('.') + 1)..].Replace('_', ' ');

        foreach (var prefix in new[] { "Main Bedroom ", "Living Room ", "Bedroom " })
        {
            if (name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    private static bool IsBatterySensor(HomeAssistantEntityState state)
    {
        return GetDomain(state.EntityId) == "sensor"
            && (state.EntityId.Contains("battery", StringComparison.OrdinalIgnoreCase)
                || string.Equals(TryGetAttributeString(state, "device_class"), "battery", StringComparison.OrdinalIgnoreCase)
                || (TryGetAttributeString(state, "friendly_name")?.Contains("battery", StringComparison.CurrentCultureIgnoreCase) ?? false));
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

    private static LightFeatureSet GetLightFeatures(IReadOnlyList<HomeAssistantEntityState> lightStates)
    {
        var state = lightStates.FirstOrDefault();
        if (state is null || state.Attributes.ValueKind != JsonValueKind.Object)
        {
            return LightFeatureSet.None;
        }

        var supportedColorModes = state.Attributes.TryGetProperty("supported_color_modes", out var modes)
            && modes.ValueKind == JsonValueKind.Array
                ? modes.EnumerateArray()
                    .Select(mode => mode.GetString())
                    .Where(mode => !string.IsNullOrWhiteSpace(mode))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];

        var supportsBrightness = supportedColorModes.Contains("brightness")
            || supportedColorModes.Contains("color_temp")
            || supportedColorModes.Contains("hs")
            || supportedColorModes.Contains("rgb")
            || supportedColorModes.Contains("xy")
            || state.Attributes.TryGetProperty("brightness", out _);
        var supportsColorTemperature = supportedColorModes.Contains("color_temp")
            || state.Attributes.TryGetProperty("color_temp_kelvin", out _)
            || state.Attributes.TryGetProperty("color_temp", out _);
        var supportsColor = supportedColorModes.Contains("hs")
            || supportedColorModes.Contains("rgb")
            || supportedColorModes.Contains("xy");

        return new LightFeatureSet(
            supportsBrightness,
            supportsColorTemperature,
            supportsColor,
            TryGetIntAttribute(state, "min_color_temp_kelvin") ?? 2000,
            TryGetIntAttribute(state, "max_color_temp_kelvin") ?? 6500);
    }

    private static int? TryGetBrightnessPercent(HomeAssistantEntityState state)
    {
        var brightness = TryGetIntAttribute(state, "brightness");
        return brightness is null ? null : Math.Clamp((int)Math.Round(brightness.Value / 255d * 100), 1, 100);
    }

    private static int? TryGetColorTemperature(HomeAssistantEntityState state)
    {
        var kelvin = TryGetIntAttribute(state, "color_temp_kelvin");
        if (kelvin is not null)
        {
            return kelvin;
        }

        var mired = TryGetIntAttribute(state, "color_temp");
        return mired is > 0 ? 1000000 / mired.Value : null;
    }

    private static int? TryGetIntAttribute(HomeAssistantEntityState state, string attributeName)
    {
        return state.Attributes.ValueKind == JsonValueKind.Object
            && state.Attributes.TryGetProperty(attributeName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
    }

    private static string? TryGetAttributeString(HomeAssistantEntityState? state, string attributeName)
    {
        return state is not null
            && state.Attributes.ValueKind == JsonValueKind.Object
            && state.Attributes.TryGetProperty(attributeName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
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

    private static string TrimRoomPrefix(string name, string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
        {
            return name;
        }

        var trimmedName = name.Trim();
        var trimmedRoomName = roomName.Trim();
        if (!trimmedName.StartsWith(trimmedRoomName, StringComparison.CurrentCultureIgnoreCase))
        {
            return trimmedName;
        }

        if (trimmedName.Length == trimmedRoomName.Length)
        {
            return trimmedName;
        }

        var separator = trimmedName[trimmedRoomName.Length];
        if (separator is not (' ' or '-' or '_' or ':' or '/'))
        {
            return trimmedName;
        }

        var withoutPrefix = trimmedName[(trimmedRoomName.Length + 1)..].Trim();
        return string.IsNullOrWhiteSpace(withoutPrefix) ? trimmedName : withoutPrefix;
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

        if (IsLightGroupDevice(device, entities, states))
        {
            description = string.Equals(manufacturer, "Philips", StringComparison.OrdinalIgnoreCase)
                ? "Philips Hue group"
                : "Light group";
            return true;
        }

        description = string.Empty;
        return false;
    }

    private static bool IsLightGroupDevice(
        HomeAssistantDevice device,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities,
        IReadOnlyList<HomeAssistantEntityState> states)
    {
        var model = device.Model ?? string.Empty;
        return model.Contains("Hue Group", StringComparison.OrdinalIgnoreCase)
            || model.Contains("Hue Room", StringComparison.OrdinalIgnoreCase)
            || model.Contains("Hue Zone", StringComparison.OrdinalIgnoreCase)
            || entities.Any(entity => string.Equals(entity.Platform, "group", StringComparison.OrdinalIgnoreCase))
            || states.Any(HasGroupedEntityList);
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
