// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using HomeGlass.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HomeGlass.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        RefreshConnectionState();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithUiStateAsync(async () =>
        {
            var connection = await AppServices.HomeAssistantAuth.ConnectAsync(HomeAssistantUrlTextBox.Text);
            HomeAssistantUrlTextBox.Text = connection.BaseUri.ToString();
            RefreshConnectionState();
            ShowStatus(InfoBarSeverity.Success, "Connected", $"HomeGlass is connected to {connection.BaseUri}.");
        });
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithUiStateAsync(async () =>
        {
            using var status = await AppServices.HomeAssistantApi.GetApiStatusAsync();
            RefreshConnectionState();
            ShowStatus(InfoBarSeverity.Success, "Connection test passed", "Home Assistant accepted the stored credentials.");
        });
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        await RunWithUiStateAsync(async () =>
        {
            await AppServices.HomeAssistantAuth.SignOutAsync();
            RefreshConnectionState();
            ShowStatus(InfoBarSeverity.Informational, "Signed out", "HomeGlass removed the stored Home Assistant credentials.");
        });
    }

    private async Task RunWithUiStateAsync(Func<Task> action)
    {
        SetControlsEnabled(false);

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Home Assistant error", ex.Message);
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void RefreshConnectionState()
    {
        var connection = AppServices.HomeAssistantAuth.CurrentConnection;
        var isConnected = connection is not null;

        HomeAssistantUrlTextBox.Text = connection?.BaseUri.ToString() ?? HomeAssistantUrlTextBox.Text;
        ConnectionSummaryText.Text = isConnected
            ? $"Connected to {connection!.BaseUri}"
            : "Connect HomeGlass to your Home Assistant server to control devices, scenes, and automations.";

        TestConnectionButton.IsEnabled = isConnected;
        SignOutButton.IsEnabled = isConnected;
    }

    private void SetControlsEnabled(bool isEnabled)
    {
        HomeAssistantUrlTextBox.IsEnabled = isEnabled;
        ConnectButton.IsEnabled = isEnabled;

        var isConnected = AppServices.HomeAssistantAuth.CurrentConnection is not null;
        TestConnectionButton.IsEnabled = isEnabled && isConnected;
        SignOutButton.IsEnabled = isEnabled && isConnected;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
