using CommunityToolkit.Mvvm.ComponentModel;

namespace Foretop.App.ViewModels;

/// <summary>Analytics page: "My performance" (your own results) and "Recorded battles" (who you met).</summary>
public sealed partial class AnalyticsViewModel(PersonalAnalyticsViewModel personal, StatisticsViewModel recorded) : ObservableObject
{
    public PersonalAnalyticsViewModel Personal { get; } = personal;
    public StatisticsViewModel Recorded { get; } = recorded;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Current))] private bool _showRecorded;

    public object Current => ShowRecorded ? Recorded : Personal;

    partial void OnShowRecordedChanged(bool value) => _ = EnsureLoadedAsync();

    public async Task EnsureLoadedAsync()
    {
        if (ShowRecorded) await Recorded.EnsureLoadedAsync();
        else await Personal.EnsureLoadedAsync();
    }
}
