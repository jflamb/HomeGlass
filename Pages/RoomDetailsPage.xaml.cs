using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HomeGlass.Pages;

public sealed partial class RoomDetailsPage : Page
{
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

        RoomNameTextBlock.Text = room.Name;
        StatusItemsControl.ItemsSource = room.StatusChips;
    }
}
