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
            RoomsGridView.ItemsSource = Array.Empty<RoomCardViewModel>();
            RoomsSummaryText.Text = "Connect to Home Assistant in Settings to load rooms.";
            return;
        }

        RoomsSummaryText.Text = "Loading rooms from Home Assistant...";

        try
        {
            var areas = await AppServices.HomeAssistantWebSocket.GetAreasAsync();
            var rooms = areas
                .OrderBy(area => area.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(RoomCardViewModel.FromArea)
                .ToList();

            RoomsGridView.ItemsSource = rooms;
            RoomsSummaryText.Text = rooms.Count == 1
                ? "1 room loaded from Home Assistant."
                : $"{rooms.Count} rooms loaded from Home Assistant.";

            if (rooms.Count == 0)
            {
                ShowInfo(InfoBarSeverity.Informational, "No rooms found", "Home Assistant did not report any areas yet.");
            }
        }
        catch (Exception ex)
        {
            RoomsGridView.ItemsSource = Array.Empty<RoomCardViewModel>();
            RoomsSummaryText.Text = "Rooms could not be loaded.";
            ShowInfo(InfoBarSeverity.Error, "Home Assistant error", ex.Message);
        }
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        RoomsInfoBar.Severity = severity;
        RoomsInfoBar.Title = title;
        RoomsInfoBar.Message = message;
        RoomsInfoBar.IsOpen = true;
    }
}

public sealed record RoomCardViewModel(string Name, string Detail, string IconGlyph)
{
    public static RoomCardViewModel FromArea(HomeAssistantArea area)
    {
        var detail = string.IsNullOrWhiteSpace(area.FloorId)
            ? "Home Assistant area"
            : $"Floor: {area.FloorId}";

        return new RoomCardViewModel(
            area.Name,
            detail,
            GetIconGlyph(area.Icon));
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
