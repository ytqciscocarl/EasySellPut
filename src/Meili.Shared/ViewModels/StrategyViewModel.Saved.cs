using Meili.Core.Time;
using Meili.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meili.Analytics;
using Meili.Shared.Services;
using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Core.Settings;
using Meili.Data.Routing;
using ARight = Meili.Analytics.OptionRight;
using CRight = Meili.Core.Models.OptionRight;

namespace Meili.Shared.ViewModels;

/// <summary>B7 (split out in H1): saving, loading and deleting named strategies.</summary>
public partial class StrategyViewModel
{
    /// <summary>B7: save the current legs under <see cref="SaveName"/> (or "SYMBOL strategy date").</summary>
    [RelayCommand]
    private void SaveStrategy()
    {
        if (!TryCompile(out var strategy, out var err))
        {
            StatusText = err ?? _loc["St_NoLegs"];
            return;
        }
        var symbol = WorkbenchDraftSymbol;
        var name = SaveName.Trim();
        if (name.Length == 0) name = $"{symbol} {strategy!.Name} {DateTime.Now:yyyy-MM-dd HH:mm}";
        var saved = new SavedStrategy(name, symbol, strategy!.Kind, strategy.Legs.Select(SavedLeg.From).ToList(), DateTimeOffset.Now);
        _store.Save(saved);
        Saved.Clear();
        foreach (var x in _store.All) Saved.Add(x);
        SelectedSaved = Saved[0];
        SaveName = "";
        StatusText = string.Format(_loc["St_SavedOk"], name);
    }

    /// <summary>B7: load a saved strategy: its symbol's chain first, then its own legs (custom, so later refreshes keep them).</summary>
    [RelayCommand]
    private async Task LoadSavedAsync()
    {
        if (SelectedSaved is not { } saved) return;
        List<StrategyLeg> legs;
        try { legs = saved.Legs.Select(l => l.ToLeg()).ToList(); }
        catch (ArgumentException ex)   // strategies.json edited by hand
        {
            StatusText = string.Format(_loc["St_SavedBad"], saved.Name, ex.Message);
            return;
        }
        SymbolText = saved.Symbol;
        _manualLegs = true;
        _suppress = true;
        try { SelectedPreset = Presets.FirstOrDefault(p => p.Kind == StrategyKind.Custom) ?? SelectedPreset; }
        finally { _suppress = false; }
        await LoadAsync();
        ReplaceLegs(legs);
        foreach (var row in Legs) row.LockEntryPrice();
        Recompute();
        ChartChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DeleteSaved()
    {
        if (SelectedSaved is not { } saved) return;
        _store.Delete(saved.Name);
        Saved.Remove(saved);
        SelectedSaved = Saved.FirstOrDefault();
    }
}
