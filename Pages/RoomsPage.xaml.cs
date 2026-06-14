using HomeGlass.Models;
using HomeGlass.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI;

namespace HomeGlass.Pages;

public sealed partial class RoomsPage : Page
{
    private CancellationTokenSource? _stateSubscriptionCts;
    private CancellationTokenSource? _refreshDebounceCts;
    private bool _isLoadingRooms;
    private bool _hasLoadedRooms;
    private readonly ObservableCollection<RoomGroupViewModel> _roomGroups = [];
    private IReadOnlyList<HomeAssistantArea> _areas = [];
    private IReadOnlyList<HomeAssistantFloor> _floors = [];
    private IReadOnlyList<HomeAssistantDevice> _devices = [];
    private IReadOnlyList<HomeAssistantEntityRegistryEntry> _entities = [];

    public RoomsPage()
    {
        InitializeComponent();
        RoomGroupsListView.ItemsSource = _roomGroups;
        Loaded += RoomsPage_Loaded;
        Unloaded += RoomsPage_Unloaded;
    }

    private async void RoomsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadRoomsAsync(showLoadingState: true);
        StartStateSubscription();
    }

    private void RoomsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopStateSubscription();
    }

    private async Task LoadRoomsAsync(bool showLoadingState)
    {
        if (_isLoadingRooms)
        {
            return;
        }

        _isLoadingRooms = true;
        if (showLoadingState)
        {
            RoomsInfoBar.IsOpen = false;
        }

        try
        {
            if (AppServices.HomeAssistantAuth.CurrentConnection is null)
            {
                if (showLoadingState || !_hasLoadedRooms)
                {
                    _roomGroups.Clear();
                    RoomsSummaryText.Text = "Connect to Home Assistant in Settings to load rooms.";
                }

                return;
            }

            if (showLoadingState || !_hasLoadedRooms)
            {
                RoomsSummaryText.Text = "Loading rooms from Home Assistant...";
            }

            var areasTask = AppServices.HomeAssistantWebSocket.GetAreasAsync();
            var floorsTask = TryLoadAsync(AppServices.HomeAssistantWebSocket.GetFloorsAsync);
            var devicesTask = AppServices.HomeAssistantWebSocket.GetDevicesAsync();
            var entitiesTask = AppServices.HomeAssistantWebSocket.GetEntityRegistryAsync();
            var statesTask = AppServices.HomeAssistantApi.GetStatesAsync();

            await Task.WhenAll(areasTask, floorsTask, devicesTask, entitiesTask, statesTask);
            _areas = areasTask.Result;
            _floors = floorsTask.Result;
            _devices = devicesTask.Result;
            _entities = entitiesTask.Result;

            var groups = BuildRoomGroups(
                _areas,
                _floors,
                _devices,
                _entities,
                statesTask.Result);

            var roomCount = groups.Sum(group => group.Rooms.Count);
            ApplyRoomGroups(groups);
            RoomsSummaryText.Text = roomCount == 1
                ? "1 room loaded from Home Assistant."
                : $"{roomCount} rooms loaded from Home Assistant.";
            _hasLoadedRooms = true;

            if (roomCount == 0)
            {
                ShowInfo(InfoBarSeverity.Informational, "No rooms found", "Home Assistant did not report any areas yet.");
            }
        }
        catch (Exception ex)
        {
            if (showLoadingState || !_hasLoadedRooms)
            {
                _roomGroups.Clear();
                RoomsSummaryText.Text = "Rooms could not be loaded.";
                ShowInfo(InfoBarSeverity.Error, "Home Assistant error", ex.Message);
            }
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
                DispatcherQueue.TryEnqueue(async () => await RefreshRoomStatesAsync());
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void RoomGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RoomCardViewModel room)
        {
            Frame.Navigate(typeof(RoomDetailsPage), room);
        }
    }

    private async void QuickActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: RoomQuickActionViewModel action })
        {
            return;
        }

        action.IsBusy = true;

        try
        {
            await AppServices.HomeAssistantApi.CallServiceAsync(
                action.Domain,
                action.Service,
                new { area_id = action.AreaId });
            QueueRoomsRefresh();
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Home Assistant error", ex.Message);
        }
        finally
        {
            action.IsBusy = false;
        }
    }

    private async Task RefreshRoomStatesAsync()
    {
        if (_isLoadingRooms || !_hasLoadedRooms)
        {
            return;
        }

        _isLoadingRooms = true;

        try
        {
            var states = await AppServices.HomeAssistantApi.GetStatesAsync();
            var groups = BuildRoomGroups(_areas, _floors, _devices, _entities, states);
            ApplyRoomGroups(groups);
        }
        catch
        {
            // Live refresh failures should not disturb a usable room view. The socket reconnect path will retry.
        }
        finally
        {
            _isLoadingRooms = false;
        }
    }

    private void ApplyRoomGroups(IReadOnlyList<RoomGroupViewModel> groups)
    {
        for (var index = _roomGroups.Count - 1; index >= 0; index--)
        {
            if (!groups.Any(group => group.Name == _roomGroups[index].Name))
            {
                _roomGroups.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < groups.Count; targetIndex++)
        {
            var incomingGroup = groups[targetIndex];
            var existingGroup = _roomGroups.FirstOrDefault(group => group.Name == incomingGroup.Name);

            if (existingGroup is null)
            {
                _roomGroups.Insert(targetIndex, incomingGroup);
            }
            else
            {
                existingGroup.Summary = incomingGroup.Summary;
                existingGroup.ApplyRooms(incomingGroup.Rooms);

                var existingIndex = _roomGroups.IndexOf(existingGroup);
                if (existingIndex != targetIndex)
                {
                    _roomGroups.Move(existingIndex, targetIndex);
                }
            }
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

public sealed class RoomGroupViewModel : ObservableObject
{
    private string _summary;

    public RoomGroupViewModel(string name, string summary, IReadOnlyList<RoomCardViewModel> rooms)
    {
        Name = name;
        _summary = summary;
        Rooms = new ObservableCollection<RoomCardViewModel>(rooms);
    }

    public string Name { get; }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<RoomCardViewModel> Rooms { get; }

    public void ApplyRooms(IReadOnlyList<RoomCardViewModel> rooms)
    {
        for (var index = Rooms.Count - 1; index >= 0; index--)
        {
            if (!rooms.Any(room => room.Name == Rooms[index].Name))
            {
                Rooms.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < rooms.Count; targetIndex++)
        {
            var incomingRoom = rooms[targetIndex];
            var existingRoom = Rooms.FirstOrDefault(room => room.Name == incomingRoom.Name);

            if (existingRoom is null)
            {
                Rooms.Insert(targetIndex, incomingRoom);
            }
            else
            {
                existingRoom.Apply(incomingRoom);

                var existingIndex = Rooms.IndexOf(existingRoom);
                if (existingIndex != targetIndex)
                {
                    Rooms.Move(existingIndex, targetIndex);
                }
            }
        }
    }
}

public sealed class RoomCardViewModel : ObservableObject
{
    private RoomQuickActionViewModel _lightAction;
    private RoomQuickActionViewModel _fanAction;
    private RoomQuickActionViewModel _lockAction;
    private string _iconGlyph;
    private IReadOnlyList<StatusChipViewModel> _statusChips;
    private Brush _cardBackground;
    private Brush _cardBorderBrush;
    private Thickness _cardBorderThickness;
    private int _deviceCount;
    private string _semanticKey;

    public RoomCardViewModel(
        string name,
        string areaId,
        string iconGlyph,
        IReadOnlyList<StatusChipViewModel> statusChips,
        Brush cardBackground,
        Brush cardBorderBrush,
        Thickness cardBorderThickness,
        RoomQuickActionViewModel lightAction,
        RoomQuickActionViewModel fanAction,
        RoomQuickActionViewModel lockAction,
        int deviceCount)
    {
        Name = name;
        AreaId = areaId;
        _iconGlyph = iconGlyph;
        _statusChips = statusChips;
        _cardBackground = cardBackground;
        _cardBorderBrush = cardBorderBrush;
        _cardBorderThickness = cardBorderThickness;
        _lightAction = lightAction;
        _fanAction = fanAction;
        _lockAction = lockAction;
        _deviceCount = deviceCount;
        _semanticKey = BuildSemanticKey(statusChips, cardBorderThickness, cardBorderBrush);
    }

    public string Name { get; }

    public string AreaId { get; }

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

    public int DeviceCount
    {
        get => _deviceCount;
        private set => SetProperty(ref _deviceCount, value);
    }

    public RoomQuickActionViewModel LightAction
    {
        get => _lightAction;
        private set => SetProperty(ref _lightAction, value);
    }

    public RoomQuickActionViewModel FanAction
    {
        get => _fanAction;
        private set => SetProperty(ref _fanAction, value);
    }

    public RoomQuickActionViewModel LockAction
    {
        get => _lockAction;
        private set => SetProperty(ref _lockAction, value);
    }

    public Visibility HasQuickActions =>
        LightAction.Visibility == Visibility.Visible ||
        FanAction.Visibility == Visibility.Visible ||
        LockAction.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    public void Apply(RoomCardViewModel room)
    {
        if (_semanticKey == room._semanticKey)
        {
            return;
        }

        _semanticKey = room._semanticKey;
        IconGlyph = room.IconGlyph;
        StatusChips = room.StatusChips;
        CardBackground = room.CardBackground;
        CardBorderBrush = room.CardBorderBrush;
        CardBorderThickness = room.CardBorderThickness;
        LightAction = room.LightAction;
        FanAction = room.FanAction;
        LockAction = room.LockAction;
        DeviceCount = room.DeviceCount;
        OnPropertyChanged(nameof(HasQuickActions));
    }

    private static string BuildSemanticKey(
        IReadOnlyList<StatusChipViewModel> chips,
        Thickness borderThickness,
        Brush borderBrush)
    {
        var chipKey = string.Join("|", chips.Select(chip => $"{chip.Text}:{chip.Kind}"));
        var brushKey = borderBrush is SolidColorBrush solidColorBrush
            ? solidColorBrush.Color.ToString()
            : borderBrush.GetHashCode().ToString();

        return $"{chipKey};{borderThickness.Left};{brushKey}";
    }

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

        var status = BuildStatus(availableEntities, devices.Count, area.AreaId);

        return new RoomCardViewModel(
            area.Name,
            area.AreaId,
            GetIconGlyph(area.Icon),
            status.Chips,
            status.CardBackground,
            status.CardBorderBrush,
            status.CardBorderThickness,
            status.LightAction,
            status.FanAction,
            status.LockAction,
            devices.Count);
    }

    private static RoomStatusViewModel BuildStatus(
        IReadOnlyList<RoomEntityState> entities,
        int deviceCount,
        string areaId)
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
            chips.Add(StatusChipViewModel.Active(
                $"{lightsOn} {Pluralize(lightsOn, "light")} on{lightDetail}",
                $"{lightsOn} primary {Pluralize(lightsOn, "light")} in this room is on. Light groups and indicator LEDs are excluded."));
            accentColor = GetLightAccentColor(lightsOnStates);
            hasAccent = true;
        }
        else if (lightStates.Count > 0)
        {
            chips.Add(StatusChipViewModel.Neutral(
                "Lights off",
                $"{lightStates.Count} primary {Pluralize(lightStates.Count, "light")} in this room is off. Light groups and indicator LEDs are excluded."));
        }

        var fans = entities.Where(entity => GetDomain(entity.State.EntityId) == "fan").ToList();
        var fansOn = fans.Count(entity => entity.State.State == "on");
        if (fansOn > 0)
        {
            chips.Add(StatusChipViewModel.Active(
                $"{fansOn} {Pluralize(fansOn, "fan")} on",
                $"{fansOn} {Pluralize(fansOn, "fan")} in this room is running."));
            if (!hasAccent)
            {
                accentColor = Color.FromArgb(255, 94, 234, 212);
                hasAccent = true;
            }
        }
        else if (fans.Count > 0)
        {
            chips.Add(StatusChipViewModel.Neutral(
                "Fans off",
                $"{fans.Count} {Pluralize(fans.Count, "fan")} in this room is off."));
        }

        var climateChip = BuildClimateChip(entities);
        if (!string.IsNullOrWhiteSpace(climateChip))
        {
            chips.Add(StatusChipViewModel.Neutral(
                climateChip,
                "Current climate reading or HVAC mode reported by Home Assistant."));
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
            if (deviceCount == 0)
            {
                chips.Add(StatusChipViewModel.Neutral("Empty", "Home Assistant has no devices assigned to this room."));
            }
        }

        var lockStates = entities.Select(entity => entity.State).Where(state => GetDomain(state.EntityId) == "lock").ToList();
        var unlocked = lockStates.Any(state => state.State is "unlocked" or "open");

        return hasAccent
            ? RoomStatusViewModel.Highlighted(
                chips,
                accentColor,
                BuildLightAction(areaId, lightStates.Count, lightsOn),
                BuildFanAction(areaId, fans.Count, fansOn),
                BuildLockAction(areaId, unlocked))
            : RoomStatusViewModel.Neutral(
                chips,
                BuildLightAction(areaId, lightStates.Count, lightsOn),
                BuildFanAction(areaId, fans.Count, fansOn),
                BuildLockAction(areaId, unlocked));
    }

    private static RoomQuickActionViewModel BuildLightAction(string areaId, int lightCount, int lightsOn)
    {
        if (lightCount == 0)
        {
            return RoomQuickActionViewModel.Hidden;
        }

        return lightsOn > 0
            ? RoomQuickActionViewModel.Visible(areaId, "light", "turn_off", "Turn off room lights")
            : RoomQuickActionViewModel.Visible(areaId, "light", "turn_on", "Turn on room lights");
    }

    private static RoomQuickActionViewModel BuildFanAction(string areaId, int fanCount, int fansOn)
    {
        if (fanCount == 0)
        {
            return RoomQuickActionViewModel.Hidden;
        }

        return fansOn > 0
            ? RoomQuickActionViewModel.Visible(areaId, "fan", "turn_off", "Turn off room fans")
            : RoomQuickActionViewModel.Visible(areaId, "fan", "turn_on", "Turn on room fans");
    }

    private static RoomQuickActionViewModel BuildLockAction(string areaId, bool unlocked)
    {
        return unlocked
            ? RoomQuickActionViewModel.Visible(areaId, "lock", "lock", "Lock room locks")
            : RoomQuickActionViewModel.Hidden;
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
        if (ContainsAny(searchableName, "rgb indicator", "indicator", "status", "notification", "led", " load ", ".load", "_load"))
        {
            return false;
        }

        if (IsLikelyAggregateLight(entity, searchableName))
        {
            return false;
        }

        return true;
    }

    private static bool IsLikelyAggregateLight(RoomEntityState entity, string searchableName)
    {
        var friendlyName = GetFriendlyName(entity.State);
        var normalizedEntityName = NormalizeEntityName(entity.State.EntityId);
        var names = new[]
        {
            entity.Entity.Name,
            entity.Entity.OriginalName,
            friendlyName,
            normalizedEntityName
        }.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

        if (names.Any(name => name!.EndsWith(" lights", StringComparison.CurrentCultureIgnoreCase)))
        {
            return true;
        }

        if (names.Any(name => name!.EndsWith(" light", StringComparison.CurrentCultureIgnoreCase)) &&
            !ContainsAny(searchableName, " top ", " bottom ", " left ", " right ", " upper ", " lower ", " can ", " lamp ", " strip ", " pendant ", " sconce ") &&
            !names.Any(name => EndsWithNumber(name!)))
        {
            return true;
        }

        return false;
    }

    private static bool EndsWithNumber(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && char.IsDigit(trimmed[^1]);
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
            ? StatusChipViewModel.Active("Presence", "At least one motion, occupancy, or presence sensor is active.")
            : StatusChipViewModel.Neutral("Vacant", "Motion, occupancy, and presence sensors in this room are inactive.");
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
            return StatusChipViewModel.Warning(
                unlocked == 1 ? "Unlocked" : $"{unlocked} unlocked",
                $"{unlocked} {Pluralize(unlocked, "lock")} in this room is unlocked or open.");
        }

        var locked = locks.Count(state => state.State == "locked");
        return locked == locks.Count
            ? StatusChipViewModel.Neutral("Locked", $"{locked} {Pluralize(locked, "lock")} in this room is locked.")
            : null;
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
            return StatusChipViewModel.Warning(
                open == 1 ? "Open" : $"{open} open",
                $"{open} door, window, garage, or opening sensor in this room is open.");
        }

        return StatusChipViewModel.Neutral(
            "Closed",
            $"{contacts.Count} door, window, garage, or opening {Pluralize(contacts.Count, "sensor")} in this room is closed.");
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

    private static string? GetFriendlyName(HomeAssistantEntityState state)
    {
        return state.Attributes.TryGetProperty("friendly_name", out var friendlyName)
            ? friendlyName.GetString()
            : null;
    }

    private static string NormalizeEntityName(string entityId)
    {
        var separatorIndex = entityId.IndexOf('.');
        var name = separatorIndex >= 0 ? entityId[(separatorIndex + 1)..] : entityId;
        return name.Replace('_', ' ');
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
    Thickness CardBorderThickness,
    RoomQuickActionViewModel LightAction,
    RoomQuickActionViewModel FanAction,
    RoomQuickActionViewModel LockAction)
{
    public static RoomStatusViewModel Neutral(
        IReadOnlyList<StatusChipViewModel> chips,
        RoomQuickActionViewModel lightAction,
        RoomQuickActionViewModel fanAction,
        RoomQuickActionViewModel lockAction)
    {
        return new RoomStatusViewModel(
            chips,
            new SolidColorBrush(Color.FromArgb(255, 48, 48, 48)),
            new SolidColorBrush(Color.FromArgb(255, 58, 58, 58)),
            new Thickness(1),
            lightAction,
            fanAction,
            lockAction);
    }

    public static RoomStatusViewModel Highlighted(
        IReadOnlyList<StatusChipViewModel> chips,
        Color accent,
        RoomQuickActionViewModel lightAction,
        RoomQuickActionViewModel fanAction,
        RoomQuickActionViewModel lockAction)
    {
        return new RoomStatusViewModel(
            chips,
            new SolidColorBrush(Color.FromArgb(44, accent.R, accent.G, accent.B)),
            new SolidColorBrush(Color.FromArgb(220, accent.R, accent.G, accent.B)),
            new Thickness(2),
            lightAction,
            fanAction,
            lockAction);
    }
}

public sealed class RoomQuickActionViewModel : ObservableObject
{
    private bool _isBusy;

    private RoomQuickActionViewModel(string areaId, string domain, string service, string toolTip, Visibility visibility)
    {
        AreaId = areaId;
        Domain = domain;
        Service = service;
        _toolTip = toolTip;
        Visibility = visibility;
    }

    private readonly string _toolTip;

    public string AreaId { get; }

    public string Domain { get; }

    public string Service { get; }

    public string ToolTip => IsBusy ? "Sending command..." : _toolTip;

    public Visibility Visibility { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(BusyVisibility));
            OnPropertyChanged(nameof(IconVisibility));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    public bool IsEnabled => !IsBusy;

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IconVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;

    public static RoomQuickActionViewModel Hidden { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, Visibility.Collapsed);

    public static RoomQuickActionViewModel Visible(string areaId, string domain, string service, string toolTip)
    {
        return new RoomQuickActionViewModel(areaId, domain, service, toolTip, Visibility.Visible);
    }
}

public sealed record StatusChipViewModel(
    string Text,
    string ToolTip,
    Brush Background,
    Brush Foreground,
    bool IsEmphasized,
    string Kind)
{
    public static StatusChipViewModel Neutral(string text, string toolTip)
    {
        return new StatusChipViewModel(
            text,
            toolTip,
            new SolidColorBrush(Color.FromArgb(255, 62, 62, 62)),
            new SolidColorBrush(Colors.White),
            false,
            "neutral");
    }

    public static StatusChipViewModel Active(string text, string toolTip)
    {
        return new StatusChipViewModel(
            text,
            toolTip,
            new SolidColorBrush(Color.FromArgb(255, 255, 214, 102)),
            new SolidColorBrush(Color.FromArgb(255, 36, 28, 0)),
            true,
            "active");
    }

    public static StatusChipViewModel Warning(string text, string toolTip)
    {
        return new StatusChipViewModel(
            text,
            toolTip,
            new SolidColorBrush(Color.FromArgb(255, 255, 159, 67)),
            new SolidColorBrush(Color.FromArgb(255, 40, 20, 0)),
            true,
            "warning");
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
