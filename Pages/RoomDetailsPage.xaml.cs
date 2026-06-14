using HomeGlass.Models;
using HomeGlass.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using System.Text.Json;
using Windows.UI;

namespace HomeGlass.Pages;

public sealed partial class RoomDetailsPage : Page
{
    private RoomCardViewModel? _room;
    private readonly ObservableCollection<DeviceGroupViewModel> _deviceGroups = [];
    private IReadOnlyList<HomeAssistantDevice> _devices = [];
    private IReadOnlyList<HomeAssistantEntityRegistryEntry> _entities = [];
    private DeviceCardViewModel? _drawerParent;

    public RoomDetailsPage()
    {
        InitializeComponent();
        DeviceGroupsListView.ItemsSource = _deviceGroups;
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
            _devices = devicesTask.Result;
            _entities = entitiesTask.Result;

            var groups = BuildDeviceGroups(
                room.AreaId,
                room.Name,
                _devices,
                _entities,
                statesTask.Result);

            ApplyDeviceGroups(groups);
        }
        catch
        {
            _deviceGroups.Clear();
        }
    }

    private async void ToggleDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: DeviceCardViewModel device })
        {
            await ToggleDeviceAsync(device);
        }
    }

    private void MoreControlsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            ShowControlDrawer(element);
        }
    }

    private void DeviceCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (sender is FrameworkElement element)
        {
            ShowControlDrawer(element);
        }
    }

    private void ShowControlDrawer(FrameworkElement element)
    {
        var flyoutTarget = element is Border
            ? element
            : FindAncestor<Border>(element);
        if (flyoutTarget?.Tag is DeviceCardViewModel { HasSecondaryControls: true } device)
        {
            OpenControlDrawer(device, parent: null);
        }
    }

    private void OpenControlDrawer(DeviceCardViewModel device, DeviceCardViewModel? parent)
    {
        _drawerParent = parent;
        DrawerBackButton.Visibility = parent is null ? Visibility.Collapsed : Visibility.Visible;
        ControlDrawer.DataContext = device;
        ControlDrawer.IsPaneOpen = true;
    }

    private void DrawerMember_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: DeviceCardViewModel member } &&
            ControlDrawer.DataContext is DeviceCardViewModel parent)
        {
            OpenControlDrawer(member, parent);
        }
    }

    private void DrawerBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_drawerParent is not null)
        {
            OpenControlDrawer(_drawerParent, parent: null);
        }
    }

    private void CloseDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        ControlDrawer.IsPaneOpen = false;
        ControlDrawer.DataContext = null;
        _drawerParent = null;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        return FindAncestor<Button>(source) is not null;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void PowerToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: DeviceCardViewModel device } toggleSwitch ||
            !toggleSwitch.IsLoaded ||
            toggleSwitch.IsOn == device.IsOn)
        {
            return;
        }

        await CallDeviceServiceAsync(device, toggleSwitch.IsOn ? "turn_on" : "turn_off");
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

        var expectedState = GetExpectedPowerState(device, service);
        device.IsBusy = true;

        try
        {
            await AppServices.HomeAssistantApi.CallServiceAsync(
                device.PrimaryDomain,
                service,
                payload ?? new { entity_id = device.PrimaryEntityId });

            if (_room is not null)
            {
                var states = expectedState is null
                    ? await AppServices.HomeAssistantApi.GetStatesAsync()
                    : await WaitForDeviceStateAsync(device, expectedState);
                RefreshDeviceStates(_room, states);
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

    private async Task<IReadOnlyList<HomeAssistantEntityState>> WaitForDeviceStateAsync(
        DeviceCardViewModel device,
        string expectedState)
    {
        IReadOnlyList<HomeAssistantEntityState> latestStates = [];

        for (var attempt = 0; attempt < 12; attempt++)
        {
            latestStates = await AppServices.HomeAssistantApi.GetStatesAsync();
            if (HasReachedExpectedState(device, latestStates, expectedState))
            {
                return latestStates;
            }

            await Task.Delay(250);
        }

        return latestStates.Count > 0
            ? latestStates
            : await AppServices.HomeAssistantApi.GetStatesAsync();
    }

    private static bool HasReachedExpectedState(
        DeviceCardViewModel device,
        IReadOnlyList<HomeAssistantEntityState> states,
        string expectedState)
    {
        if (device.MemberEntityIds.Count > 0)
        {
            var memberEntityIds = device.MemberEntityIds.ToHashSet(StringComparer.Ordinal);
            var memberStates = states
                .Where(state => memberEntityIds.Contains(state.EntityId))
                .Where(state => state.State is not "unavailable" and not "unknown")
                .ToList();

            return memberStates.Count > 0 &&
                memberStates.All(state => string.Equals(state.State, expectedState, StringComparison.Ordinal));
        }

        return states.Any(state =>
            string.Equals(state.EntityId, device.PrimaryEntityId, StringComparison.Ordinal) &&
            string.Equals(state.State, expectedState, StringComparison.Ordinal));
    }

    private static string? GetExpectedPowerState(DeviceCardViewModel device, string service)
    {
        return service switch
        {
            "turn_on" => "on",
            "turn_off" => "off",
            "toggle" => device.IsOn ? "off" : "on",
            _ => null
        };
    }

    private async Task RefreshDeviceStatesAsync(RoomCardViewModel room)
    {
        var states = await AppServices.HomeAssistantApi.GetStatesAsync();
        RefreshDeviceStates(room, states);
    }

    private void RefreshDeviceStates(RoomCardViewModel room, IReadOnlyList<HomeAssistantEntityState> states)
    {
        var groups = BuildDeviceGroups(room.AreaId, room.Name, _devices, _entities, states);
        ApplyDeviceGroups(groups);
    }

    private void ApplyDeviceGroups(IReadOnlyList<DeviceGroupViewModel> groups)
    {
        for (var index = _deviceGroups.Count - 1; index >= 0; index--)
        {
            if (!groups.Any(group => group.Name == _deviceGroups[index].Name))
            {
                _deviceGroups.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < groups.Count; targetIndex++)
        {
            var incomingGroup = groups[targetIndex];
            var existingGroup = _deviceGroups.FirstOrDefault(group => group.Name == incomingGroup.Name);

            if (existingGroup is null)
            {
                _deviceGroups.Insert(targetIndex, incomingGroup);
            }
            else
            {
                existingGroup.Summary = incomingGroup.Summary;
                existingGroup.ApplyDevices(incomingGroup.Devices);

                var existingIndex = _deviceGroups.IndexOf(existingGroup);
                if (existingIndex != targetIndex)
                {
                    _deviceGroups.Move(existingIndex, targetIndex);
                }
            }
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

        var deviceCards = roomDevices.Values
            .Select(device =>
            {
                entitiesByDevice.TryGetValue(device.Id, out var deviceEntities);
                return DeviceCardViewModel.FromDevice(device, roomName, deviceEntities ?? [], roomEntities, statesByEntityId);
            })
            .ToList();
        var renderedDeviceIds = roomDevices.Keys.ToHashSet(StringComparer.Ordinal);
        var entityBackedGroupCards = roomEntities
            .Where(entity => IsEntityBackedLightGroup(entity, renderedDeviceIds, roomEntities, statesByEntityId))
            .Select(entity => DeviceCardViewModel.FromLightGroupEntity(entity, roomName, roomEntities, statesByEntityId))
            .ToList();
        var lightGroupCards = deviceCards
            .Concat(entityBackedGroupCards)
            .Where(card => card.Type == "Light Groups")
            .ToList();
        var broadestLightGroupMemberIds = GetBroadestLightGroupMemberIds(lightGroupCards);

        return deviceCards
            .Concat(entityBackedGroupCards)
            .Where(device => device.Type != "Lights" ||
                string.IsNullOrWhiteSpace(device.PrimaryEntityId) ||
                !broadestLightGroupMemberIds.Contains(device.PrimaryEntityId))
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

    private static ISet<string> GetBroadestLightGroupMemberIds(IReadOnlyList<DeviceCardViewModel> lightGroupCards)
    {
        var groupsWithMembers = lightGroupCards
            .Where(group => group.MemberEntityIds.Count > 0)
            .ToList();
        var broadestGroups = groupsWithMembers
            .Where(group => !groupsWithMembers.Any(other =>
                other != group &&
                other.MemberEntityIds.Count > group.MemberEntityIds.Count &&
                group.MemberEntityIds.All(other.MemberEntityIds.Contains)))
            .ToList();

        return broadestGroups
            .SelectMany(group => group.MemberEntityIds)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsEntityBackedLightGroup(
        HomeAssistantEntityRegistryEntry entity,
        ISet<string> renderedDeviceIds,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId)
    {
        if (DeviceCardViewModel.GetDomainName(entity.EntityId) != "light" ||
            DeviceCardViewModel.IsIndicatorLightEntity(entity) ||
            (!string.IsNullOrWhiteSpace(entity.DeviceId) && renderedDeviceIds.Contains(entity.DeviceId)))
        {
            return false;
        }

        if (!statesByEntityId.TryGetValue(entity.EntityId, out var state))
        {
            return false;
        }

        return DeviceCardViewModel.HasGroupedEntityList(state)
            || DeviceCardViewModel.LooksLikeLightGroupEntity(entity, state)
            || HasNumberedSiblingLights(entity, state, roomEntities);
    }

    private static bool HasNumberedSiblingLights(
        HomeAssistantEntityRegistryEntry entity,
        HomeAssistantEntityState state,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities)
    {
        var name = DeviceCardViewModel.GetEntityDisplayName(entity, state).Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsDigit))
        {
            return false;
        }

        return roomEntities.Any(candidate =>
        {
            if (candidate.EntityId == entity.EntityId ||
                DeviceCardViewModel.GetDomainName(candidate.EntityId) != "light")
            {
                return false;
            }

            var candidateName = DeviceCardViewModel.GetEntityDisplayName(candidate, null).Trim();
            return candidateName.StartsWith($"{name} ", StringComparison.CurrentCultureIgnoreCase)
                && candidateName[name.Length..].Any(char.IsDigit);
        });
    }

    private static int GetTypeOrder(string type)
    {
        return type switch
        {
            "Light Groups" => 0,
            "Lights" => 1,
            "Fans" => 2,
            "Climate" => 3,
            "Locks" => 4,
            "Media" => 5,
            "Sensors" => 6,
            "Controls" => 7,
            _ => 99
        };
    }

    private static string Pluralize(int count, string noun)
    {
        return count == 1 ? noun : $"{noun}s";
    }
}

public sealed class DeviceGroupViewModel : ObservableObject
{
    private string _summary;

    public DeviceGroupViewModel(string name, string summary, IReadOnlyList<DeviceCardViewModel> devices)
    {
        Name = name;
        _summary = summary;
        Devices = new ObservableCollection<DeviceCardViewModel>(devices);
    }

    public string Name { get; }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; }

    public void ApplyDevices(IReadOnlyList<DeviceCardViewModel> devices)
    {
        for (var index = Devices.Count - 1; index >= 0; index--)
        {
            if (!devices.Any(device => device.Key == Devices[index].Key))
            {
                Devices.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < devices.Count; targetIndex++)
        {
            var incomingDevice = devices[targetIndex];
            var existingDevice = Devices.FirstOrDefault(device => device.Key == incomingDevice.Key);

            if (existingDevice is null)
            {
                Devices.Insert(targetIndex, incomingDevice);
            }
            else
            {
                var existingIndex = Devices.IndexOf(existingDevice);
                existingDevice.ApplyFrom(incomingDevice);
                if (existingIndex != targetIndex)
                {
                    Devices.Move(existingIndex, targetIndex);
                }
            }
        }
    }
}

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
    private string _name;
    private string _detail;
    private string _type;
    private string _iconGlyph;
    private IReadOnlyList<StatusChipViewModel> _statusChips;
    private Brush _cardBackground;
    private Brush _cardBorderBrush;
    private Thickness _cardBorderThickness;
    private double _cardOpacity;
    private bool _isBusy;
    private double _brightnessPercent;
    private double _colorTemperatureKelvin;
    private bool _isOn;

    private DeviceCardViewModel(
        string key,
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
        bool isOn,
        IReadOnlyList<DeviceCardViewModel>? members = null,
        IReadOnlyList<string>? memberEntityIds = null)
    {
        Key = key;
        _name = name;
        _detail = detail;
        _type = type;
        _iconGlyph = iconGlyph;
        _statusChips = statusChips;
        _cardBackground = cardBackground;
        _cardBorderBrush = cardBorderBrush;
        _cardBorderThickness = cardBorderThickness;
        _cardOpacity = cardOpacity;
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
        _isOn = isOn;
        Members = new ObservableCollection<DeviceCardViewModel>(members ?? []);
        MemberEntityIds = memberEntityIds ?? [];
    }

    public string Key { get; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public string Type
    {
        get => _type;
        private set => SetProperty(ref _type, value);
    }

    public string IconGlyph
    {
        get => _iconGlyph;
        private set => SetProperty(ref _iconGlyph, value);
    }

    public IReadOnlyList<StatusChipViewModel> StatusChips
    {
        get => _statusChips;
        private set => SetProperty(ref _statusChips, value);
    }

    public Brush CardBackground
    {
        get => _cardBackground;
        private set => SetProperty(ref _cardBackground, value);
    }

    public Brush CardBorderBrush
    {
        get => _cardBorderBrush;
        private set => SetProperty(ref _cardBorderBrush, value);
    }

    public Thickness CardBorderThickness
    {
        get => _cardBorderThickness;
        private set => SetProperty(ref _cardBorderThickness, value);
    }

    public double CardOpacity
    {
        get => _cardOpacity;
        private set => SetProperty(ref _cardOpacity, value);
    }

    public string PrimaryDomain { get; }

    public string? PrimaryEntityId { get; }

    public bool IsControllable { get; }

    public bool SupportsBrightness { get; }

    public bool SupportsColorTemperature { get; }

    public bool SupportsColor { get; }

    public int MinColorTemperatureKelvin { get; }

    public int MaxColorTemperatureKelvin { get; }

    public bool IsOn
    {
        get => _isOn;
        private set
        {
            SetProperty(ref _isOn, value);
            OnPropertyChanged(nameof(ToggleToolTip));
        }
    }

    public ObservableCollection<DeviceCardViewModel> Members { get; }

    public IReadOnlyList<string> MemberEntityIds { get; private set; }

    public Visibility MembersVisibility => Members.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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

    public bool HasSecondaryControls => IsControllable;

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

    public void ApplyFrom(DeviceCardViewModel incomingDevice)
    {
        Name = incomingDevice.Name;
        Detail = incomingDevice.Detail;
        Type = incomingDevice.Type;
        IconGlyph = incomingDevice.IconGlyph;
        StatusChips = incomingDevice.StatusChips;
        CardBackground = incomingDevice.CardBackground;
        CardBorderBrush = incomingDevice.CardBorderBrush;
        CardBorderThickness = incomingDevice.CardBorderThickness;
        CardOpacity = incomingDevice.CardOpacity;
        BrightnessPercent = incomingDevice.BrightnessPercent;
        ColorTemperatureKelvin = incomingDevice.ColorTemperatureKelvin;
        IsOn = incomingDevice.IsOn;
        MemberEntityIds = incomingDevice.MemberEntityIds;
        ApplyMembers(incomingDevice.Members);
        OnPropertyChanged(nameof(MembersVisibility));
    }

    private void ApplyMembers(IReadOnlyList<DeviceCardViewModel> members)
    {
        for (var index = Members.Count - 1; index >= 0; index--)
        {
            if (!members.Any(member => member.Key == Members[index].Key))
            {
                Members.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < members.Count; targetIndex++)
        {
            var incomingMember = members[targetIndex];
            var existingMember = Members.FirstOrDefault(member => member.Key == incomingMember.Key);
            if (existingMember is null)
            {
                Members.Insert(targetIndex, incomingMember);
            }
            else
            {
                var existingIndex = Members.IndexOf(existingMember);
                existingMember.ApplyFrom(incomingMember);
                if (existingIndex != targetIndex)
                {
                    Members.Move(existingIndex, targetIndex);
                }
            }
        }
    }

    public void ApplyOptimisticPowerState(string service)
    {
        switch (service)
        {
            case "turn_on":
                IsOn = true;
                break;
            case "turn_off":
                IsOn = false;
                break;
            case "toggle":
                IsOn = !IsOn;
                break;
        }
    }

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
        var isLightGroup = IsLightGroupDevice(device, entities, states);
        var primaryDomain = GetPrimaryDomain(device, states, entities);
        var serviceDomain = primaryDomain == "light_group" ? "light" : primaryDomain;
        var primaryStates = GetPrimaryStates(primaryDomain, states);
        var primaryState = primaryDomain == "light_group"
            ? GetLightGroupPrimaryState(primaryStates, entities)
            : primaryStates.FirstOrDefault(state => state.State == "on") ?? primaryStates.FirstOrDefault();
        var unavailable = IsUnavailable(primaryDomain, states);
        var memberEntityIds = isLightGroup
            ? GetGroupMemberEntityIds(primaryStates, groupEntity: null, roomEntities)
            : [];
        var memberStates = memberEntityIds
            .Select(entityId => statesByEntityId.TryGetValue(entityId, out var state) ? state : null)
            .OfType<HomeAssistantEntityState>()
            .Where(state => state.State is not "unavailable" and not "unknown")
            .ToList();
        var active = !unavailable && isLightGroup && memberStates.Count > 0
            ? memberStates.Any(state => state.State == "on")
            : !unavailable && IsActive(primaryDomain, states);
        var chips = BuildStatusChips(primaryDomain, states, roomEntities, statesByEntityId, isLightGroup);
        var accent = active ? GetAccent(primaryDomain) : Colors.Transparent;
        var isControllable = !unavailable
            && primaryState is not null
            && serviceDomain is "light" or "fan";
        var lightFeatures = GetLightFeatures(primaryStates);
        var members = memberEntityIds
            .Select(entityId => FromLightMemberEntity(entityId, roomName, roomEntities, statesByEntityId))
            .OfType<DeviceCardViewModel>()
            .OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new DeviceCardViewModel(
            device.Id,
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
            serviceDomain,
            primaryState?.EntityId,
            isControllable,
            lightFeatures.SupportsBrightness,
            lightFeatures.SupportsColorTemperature,
            lightFeatures.SupportsColor,
            primaryState is null ? 100 : TryGetBrightnessPercent(primaryState) ?? 100,
            primaryState is null ? 3000 : TryGetColorTemperature(primaryState) ?? 3000,
            lightFeatures.MinColorTemperatureKelvin,
            lightFeatures.MaxColorTemperatureKelvin,
            primaryStates.Any(state => state.State == "on"),
            members,
            memberEntityIds);
    }

    public static DeviceCardViewModel FromLightGroupEntity(
        HomeAssistantEntityRegistryEntry entity,
        string roomName,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId)
    {
        var state = statesByEntityId.TryGetValue(entity.EntityId, out var entityState)
            ? entityState
            : null;
        var states = state is null ? [] : new[] { state };
        var unavailable = state is null || state.State is "unavailable" or "unknown";
        var memberEntityIds = GetGroupMemberEntityIds(states, entity, roomEntities);
        var memberStates = memberEntityIds
            .Select(entityId => statesByEntityId.TryGetValue(entityId, out var memberState) ? memberState : null)
            .OfType<HomeAssistantEntityState>()
            .Where(memberState => memberState.State is not "unavailable" and not "unknown")
            .ToList();
        var active = memberStates.Count > 0
            ? memberStates.Any(memberState => memberState.State == "on")
            : state?.State == "on";
        var chips = BuildStatusChips("light_group", states, roomEntities, statesByEntityId, isLightGroup: true);
        var accent = active ? GetAccent("light_group") : Colors.Transparent;
        var lightFeatures = GetLightFeatures(states);
        var name = TrimRoomPrefix(GetEntityDisplayName(entity, state), roomName);
        var members = memberEntityIds
            .Select(entityId => FromLightMemberEntity(entityId, roomName, roomEntities, statesByEntityId))
            .OfType<DeviceCardViewModel>()
            .OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new DeviceCardViewModel(
            $"entity:{entity.EntityId}",
            name,
            "Home Assistant light group",
            GetTypeName("light_group"),
            GetIconGlyph("light_group"),
            chips,
            unavailable
                ? new SolidColorBrush(Color.FromArgb(255, 38, 38, 38))
                : active ? new SolidColorBrush(Color.FromArgb(44, accent.R, accent.G, accent.B)) : new SolidColorBrush(Color.FromArgb(255, 48, 48, 48)),
            unavailable
                ? new SolidColorBrush(Color.FromArgb(255, 50, 50, 50))
                : active ? new SolidColorBrush(Color.FromArgb(220, accent.R, accent.G, accent.B)) : new SolidColorBrush(Color.FromArgb(255, 58, 58, 58)),
            new Thickness(active ? 2 : 1),
            unavailable ? 0.58 : 1,
            "light",
            entity.EntityId,
            !unavailable,
            lightFeatures.SupportsBrightness,
            lightFeatures.SupportsColorTemperature,
            lightFeatures.SupportsColor,
            state is null ? 100 : TryGetBrightnessPercent(state) ?? 100,
            state is null ? 3000 : TryGetColorTemperature(state) ?? 3000,
            lightFeatures.MinColorTemperatureKelvin,
            lightFeatures.MaxColorTemperatureKelvin,
            active,
            members,
            memberEntityIds);
    }

    private static DeviceCardViewModel? FromLightMemberEntity(
        string entityId,
        string roomName,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId)
    {
        var entity = roomEntities.FirstOrDefault(candidate => candidate.EntityId == entityId);
        if (entity is null || !statesByEntityId.TryGetValue(entityId, out var state))
        {
            return null;
        }

        var states = new[] { state };
        var unavailable = state.State is "unavailable" or "unknown";
        var active = state.State == "on";
        var chips = new[] { BuildLightStateChip(states) };
        var accent = active ? GetAccent("light") : Colors.Transparent;
        var lightFeatures = GetLightFeatures(states);

        return new DeviceCardViewModel(
            $"member:{entityId}",
            TrimRoomPrefix(GetEntityDisplayName(entity, state), roomName),
            "Light",
            GetTypeName("light"),
            GetIconGlyph("light"),
            chips,
            unavailable
                ? new SolidColorBrush(Color.FromArgb(255, 38, 38, 38))
                : active ? new SolidColorBrush(Color.FromArgb(44, accent.R, accent.G, accent.B)) : new SolidColorBrush(Color.FromArgb(255, 48, 48, 48)),
            unavailable
                ? new SolidColorBrush(Color.FromArgb(255, 50, 50, 50))
                : active ? new SolidColorBrush(Color.FromArgb(220, accent.R, accent.G, accent.B)) : new SolidColorBrush(Color.FromArgb(255, 58, 58, 58)),
            new Thickness(active ? 2 : 1),
            unavailable ? 0.58 : 1,
            "light",
            entityId,
            !unavailable,
            lightFeatures.SupportsBrightness,
            lightFeatures.SupportsColorTemperature,
            lightFeatures.SupportsColor,
            TryGetBrightnessPercent(state) ?? 100,
            TryGetColorTemperature(state) ?? 3000,
            lightFeatures.MinColorTemperatureKelvin,
            lightFeatures.MaxColorTemperatureKelvin,
            active);
    }

    private static string GetPrimaryDomain(
        HomeAssistantDevice device,
        IReadOnlyList<HomeAssistantEntityState> states,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities)
    {
        var domains = states
            .Where(state => !IsIndicatorLight(state))
            .Select(state => GetDomain(state.EntityId))
            .Concat(entities.Where(entity => !IsIndicatorLightEntity(entity)).Select(entity => GetDomain(entity.EntityId)))
            .ToList();
        if (IsLightGroupDevice(device, entities, states))
        {
            return "light_group";
        }

        if (domains.Contains("fan") || LooksLikeFanDevice(device))
        {
            return "fan";
        }

        if (LooksLikeControlDevice(device, states, entities))
        {
            return "control";
        }

        if (LooksLikeMediaDisplay(device))
        {
            return domains.Contains("media_player") ? "media_player" : "media_display";
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

    private static HomeAssistantEntityState? GetLightGroupPrimaryState(
        IReadOnlyList<HomeAssistantEntityState> primaryStates,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities)
    {
        var groupedMemberIds = primaryStates
            .SelectMany(GetGroupedEntityIds)
            .ToHashSet(StringComparer.Ordinal);

        return primaryStates.FirstOrDefault(HasGroupedEntityList)
            ?? primaryStates.FirstOrDefault(state =>
            {
                var entity = entities.FirstOrDefault(candidate => candidate.EntityId == state.EntityId);
                return entity is not null && LooksLikeLightGroupEntity(entity, state);
            })
            ?? primaryStates.FirstOrDefault(state =>
            {
                var friendlyName = TryGetAttributeString(state, "friendly_name") ?? string.Empty;
                return ContainsWord(state.EntityId, "group")
                    || ContainsWord(friendlyName, "group")
                    || ContainsWord(friendlyName, "lights");
            })
            ?? primaryStates.FirstOrDefault(state => !groupedMemberIds.Contains(state.EntityId))
            ?? primaryStates.FirstOrDefault(state => state.State == "on")
            ?? primaryStates.FirstOrDefault();
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

    private static bool LooksLikeControlDevice(
        HomeAssistantDevice device,
        IReadOnlyList<HomeAssistantEntityState> states,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> entities)
    {
        return ContainsWord(device.Model, "dimmer")
            || ContainsWord(device.Model, "button")
            || ContainsWord(device.Model, "switch")
            || ContainsWord(device.Name, "dimmer")
            || ContainsWord(device.Name, "button")
            || ContainsWord(device.Name, "switch")
            || ContainsWord(device.Manufacturer, "Inovelli")
            || entities.Any(entity => entity.EntityId.Contains("button", StringComparison.OrdinalIgnoreCase))
            || states.Any(IsIndicatorLight);
    }

    private static bool LooksLikeMediaDisplay(HomeAssistantDevice device)
    {
        return ContainsWord(device.Name, "echo")
            || ContainsWord(device.Model, "echo")
            || ContainsWord(device.Manufacturer, "Amazon")
            || ContainsWord(device.Name, "nest")
            || ContainsWord(device.Model, "nest")
            || ContainsWord(device.Model, "display")
            || ContainsWord(device.Manufacturer, "Google");
    }

    private static bool IsIndicatorLight(HomeAssistantEntityState state)
    {
        return GetDomain(state.EntityId) == "light"
            && (state.EntityId.Contains("indicator", StringComparison.OrdinalIgnoreCase)
                || (TryGetAttributeString(state, "friendly_name")?.Contains("indicator", StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    public static bool IsIndicatorLightEntity(HomeAssistantEntityRegistryEntry entity)
    {
        return GetDomain(entity.EntityId) == "light"
            && (entity.EntityId.Contains("indicator", StringComparison.OrdinalIgnoreCase)
                || (entity.Name?.Contains("indicator", StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (entity.OriginalName?.Contains("indicator", StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    public static bool LooksLikeLightGroupEntity(HomeAssistantEntityRegistryEntry entity, HomeAssistantEntityState state)
    {
        var name = GetEntityDisplayName(entity, state);
        return ContainsWord(entity.Platform, "group")
            || ContainsWord(entity.EntityId, "group")
            || ContainsWord(name, "group")
            || HasLightGroupAliases(entity, state);
    }

    private static bool HasLightGroupAliases(HomeAssistantEntityRegistryEntry entity, HomeAssistantEntityState state)
    {
        var name = GetEntityDisplayName(entity, state);
        return name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 1;
    }

    private static IReadOnlyList<StatusChipViewModel> BuildStatusChips(
        string primaryDomain,
        IReadOnlyList<HomeAssistantEntityState> states,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities,
        IReadOnlyDictionary<string, HomeAssistantEntityState> statesByEntityId,
        bool isLightGroup)
    {
        var chips = new List<StatusChipViewModel>();
        var primaryStates = GetPrimaryStates(primaryDomain, states);
        if (IsUnavailable(primaryDomain, states))
        {
            chips.Add(UnavailableChip());
            return chips;
        }

        switch (primaryDomain)
        {
            case "light_group":
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

                chips.Add(BuildLightStateChip(primaryStates));
                break;
            case "fan":
                var on = primaryStates.Count(state => state.State == "on");
                chips.Add(on > 0
                    ? StatusChipViewModel.Active(BuildFanStateText(primaryStates), "Fan is on.")
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
                chips.AddRange(BuildEnvironmentalSensorChips(states));
                break;
            case "sensor":
                var battery = primaryStates.FirstOrDefault(IsBatterySensor);
                if (battery is not null && int.TryParse(battery.State, out var batteryLevel))
                {
                    chips.Add(StatusChipViewModel.Neutral($"🔋 {batteryLevel}%", $"Battery level is {batteryLevel}%."));
                }

                chips.AddRange(BuildEnvironmentalSensorChips(primaryStates));
                if (chips.Count == 0)
                {
                    var sensorState = primaryStates.FirstOrDefault()?.State ?? "Unknown";
                    chips.Add(StatusChipViewModel.Neutral(NormalizeState(sensorState), "Current sensor state."));
                }
                break;
            case "control":
                var controlBattery = states.FirstOrDefault(IsBatterySensor);
                if (controlBattery is not null && int.TryParse(controlBattery.State, out var controlBatteryLevel))
                {
                    chips.Add(StatusChipViewModel.Neutral($"🔋 {controlBatteryLevel}%", $"Battery level is {controlBatteryLevel}%."));
                }

                chips.AddRange(BuildEnvironmentalSensorChips(states));
                if (chips.Count == 0)
                {
                    chips.Add(StatusChipViewModel.Neutral("Ready", "Control device is available."));
                }
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

    private static IReadOnlyList<string> GetGroupMemberEntityIds(
        IReadOnlyList<HomeAssistantEntityState> lightStates,
        HomeAssistantEntityRegistryEntry? groupEntity,
        IReadOnlyList<HomeAssistantEntityRegistryEntry> roomEntities)
    {
        var explicitMemberIds = lightStates
            .SelectMany(GetGroupedEntityIds)
            .Distinct(StringComparer.Ordinal)
            .Where(entityId => GetDomain(entityId) == "light")
            .ToList();
        if (explicitMemberIds.Count > 0)
        {
            return explicitMemberIds;
        }

        if (groupEntity is null)
        {
            return [];
        }

        var groupName = GetEntityDisplayName(groupEntity, lightStates.FirstOrDefault());
        var aliases = groupName
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return roomEntities
            .Where(entity => entity.EntityId != groupEntity.EntityId && GetDomain(entity.EntityId) == "light")
            .Where(entity =>
            {
                var entityName = GetEntityDisplayName(entity, null).Trim();
                return aliases.Any(alias =>
                    entityName.StartsWith($"{alias} ", StringComparison.CurrentCultureIgnoreCase) &&
                    entityName[alias.Length..].Any(char.IsDigit));
            })
            .Select(entity => entity.EntityId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static StatusChipViewModel BuildLightStateChip(IReadOnlyList<HomeAssistantEntityState> lightStates)
    {
        var onLight = lightStates.FirstOrDefault(state => state.State == "on");
        if (onLight is null)
        {
            return StatusChipViewModel.Neutral("Off", "Light is off.");
        }

        var details = new List<string>();
        if (TryGetBrightnessPercent(onLight) is { } brightness)
        {
            details.Add($"{brightness}%");
        }

        if (TryGetColorTemperature(onLight) is { } kelvin)
        {
            details.Add($"{kelvin}K");
        }
        else if (TryGetRgbColorDescription(onLight) is { } color)
        {
            details.Add(color);
        }

        return StatusChipViewModel.Active(
            details.Count == 0 ? "On" : $"On · {string.Join(" · ", details)}",
            "Light is on.");
    }

    private static string BuildFanStateText(IReadOnlyList<HomeAssistantEntityState> fanStates)
    {
        var onFan = fanStates.FirstOrDefault(state => state.State == "on");
        if (onFan is null)
        {
            return "Off";
        }

        if (TryGetIntAttribute(onFan, "percentage") is { } percentage)
        {
            return $"On · {percentage}%";
        }

        if (TryGetAttributeString(onFan, "preset_mode") is { } presetMode)
        {
            return $"On · {NormalizeState(presetMode)}";
        }

        return "On";
    }

    private static IReadOnlyList<StatusChipViewModel> BuildEnvironmentalSensorChips(IReadOnlyList<HomeAssistantEntityState> states)
    {
        var chips = new List<StatusChipViewModel>();
        var temperature = states.FirstOrDefault(state => IsSensorDeviceClass(state, "temperature"));
        if (temperature is not null)
        {
            chips.Add(StatusChipViewModel.Neutral(
                $"{FormatSensorValue(temperature)}",
                "Current temperature."));
        }

        var humidity = states.FirstOrDefault(state => IsSensorDeviceClass(state, "humidity"));
        if (humidity is not null)
        {
            chips.Add(StatusChipViewModel.Neutral(
                $"{FormatSensorValue(humidity)}",
                "Current humidity."));
        }

        return chips;
    }

    private static bool IsSensorDeviceClass(HomeAssistantEntityState state, string deviceClass)
    {
        return GetDomain(state.EntityId) == "sensor"
            && string.Equals(TryGetAttributeString(state, "device_class"), deviceClass, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSensorValue(HomeAssistantEntityState state)
    {
        var unit = TryGetAttributeString(state, "unit_of_measurement");
        var value = double.TryParse(state.State, out var numericValue)
            ? $"{Math.Round(numericValue)}"
            : NormalizeState(state.State);
        return string.IsNullOrWhiteSpace(unit) ? value : $"{value}{unit}";
    }

    private static string? TryGetRgbColorDescription(HomeAssistantEntityState state)
    {
        if (state.Attributes.ValueKind != JsonValueKind.Object ||
            !state.Attributes.TryGetProperty("rgb_color", out var rgb) ||
            rgb.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = rgb.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.Number)
            .Select(value => value.GetInt32())
            .Take(3)
            .ToArray();
        return values.Length == 3
            ? $"#{values[0]:X2}{values[1]:X2}{values[2]:X2}"
            : null;
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
        var name = registryName ?? GetEntityDisplayName(entityId, state);

        foreach (var prefix in new[] { "Main Bedroom ", "Living Room ", "Bedroom " })
        {
            if (name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    public static string GetEntityDisplayName(HomeAssistantEntityRegistryEntry entity, HomeAssistantEntityState? state)
    {
        return entity.Name
            ?? entity.OriginalName
            ?? GetEntityDisplayName(entity.EntityId, state);
    }

    private static string GetEntityDisplayName(string entityId, HomeAssistantEntityState? state)
    {
        return TryGetAttributeString(state, "friendly_name")
            ?? entityId[(entityId.IndexOf('.') + 1)..].Replace('_', ' ');
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
        var primaryStates = GetPrimaryStates(primaryDomain, states);
        return primaryDomain switch
        {
            "light" or "light_group" or "fan" => primaryStates.Any(state => state.State == "on"),
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
        if (state.Attributes.ValueKind != JsonValueKind.Object ||
            !state.Attributes.TryGetProperty(attributeName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var stringNumber)
            ? stringNumber
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

        var primaryStates = GetPrimaryStates(primaryDomain, states);
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
        if (primaryDomain is "light" or "light_group" && TryGetLightGroupDescription(device, entities, states, out var groupDescription))
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

    public static bool HasGroupedEntityList(HomeAssistantEntityState state)
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
            "light_group" => "Light Groups",
            "fan" => "Fans",
            "climate" => "Climate",
            "lock" => "Locks",
            "media_player" or "media_display" => "Media",
            "binary_sensor" or "sensor" => "Sensors",
            "control" => "Controls",
            _ => "Other"
        };
    }

    private static string GetIconGlyph(string domain)
    {
        return domain switch
        {
            "light" or "light_group" => "\uE706",
            "fan" => "\uE9CA",
            "climate" => "\uE7A3",
            "lock" => "\uE72E",
            "media_player" or "media_display" => "\uE8B2",
            "binary_sensor" or "sensor" => "\uE9D9",
            "control" => "\uE8A7",
            _ => "\uE950"
        };
    }

    private static Color GetAccent(string domain)
    {
        return domain switch
        {
            "light" or "light_group" => Color.FromArgb(255, 255, 210, 96),
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

    public static string GetDomainName(string entityId)
    {
        return GetDomain(entityId);
    }

    private static string NormalizeState(string state)
    {
        return state.Replace('_', ' ');
    }

    private static string GetFriendlyDomainName(string domain, int count)
    {
        return domain switch
        {
            "light" or "light_group" => Pluralize(count, "light"),
            "fan" => Pluralize(count, "fan"),
            _ => Pluralize(count, "device")
        };
    }

    private static IReadOnlyList<HomeAssistantEntityState> GetPrimaryStates(
        string primaryDomain,
        IReadOnlyList<HomeAssistantEntityState> states)
    {
        var stateDomain = primaryDomain == "light_group" ? "light" : primaryDomain;
        return states.Where(state => GetDomain(state.EntityId) == stateDomain).ToList();
    }

    private static string Pluralize(int count, string noun)
    {
        return count == 1 ? noun : $"{noun}s";
    }
}
