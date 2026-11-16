using System.Text.Json;
using Meili.Analytics;
using Meili.Automation;
using Meili.Shared.ViewModels;

namespace Meili.Shared.Services;

/// <summary>Local UI protocol for the linked workbench. Valuations are the existing StrategyViewModel/Analytics results.</summary>
public static class StrategyWorkbench
{
    public static string Serialize(StrategyViewModel vm, ChartPalette palette)
    {
        static double? F(double? x) => x is { } n && double.IsFinite(n) ? n : null;
        var chain = vm.WorkbenchChain;
        var valid = vm.Legs.Count > 0 && vm.Legs.All(l => l.TryToLeg(out _, out _)) && vm.WorkbenchSymbolMatches;
        var selected = chain?.Contracts.Where(c => c.Expiration == vm.SelectedExpiration?.Date).ToArray() ?? [];
        var contracts = selected.OrderBy(c => selected.Length > 2000 && vm.Spot is { } spot ? Math.Abs(c.Strike-spot) : c.Strike)
            .ThenBy(c => c.Right).Take(2000).OrderBy(c => c.Strike).ThenBy(c => c.Right).Select(c => new
            {
                id = c.ContractSymbol ?? "", strike = F(c.Strike), right = c.Right.ToString(), expiration = c.Expiration.ToString("yyyy-MM-dd"),
                bid = F(c.Bid), ask = F(c.Ask), last = F(c.Last), iv = F(c.ImpliedVolatility), delta = F(c.Delta),
                volume = F(c.Volume), oi = F(c.OpenInterest), lastTrade = c.LastTradeTime,
                canAdd = !string.IsNullOrWhiteSpace(c.ContractSymbol) && double.IsFinite(c.Strike) && c.Strike > 0 && string.Equals(c.Underlying, chain?.Underlying, StringComparison.OrdinalIgnoreCase) && !vm.IsBusy && vm.WorkbenchSymbolMatches && string.Equals(chain?.Underlying, vm.SymbolText, StringComparison.OrdinalIgnoreCase) && OptionEntryReference.From(c).Price is not null,
                basis = OptionEntryReference.From(c).Basis, entryPrice = OptionEntryReference.From(c).Price,
            }).ToArray();
        var loc = LocalizationService.Current;
        var labels = new Dictionary<string, string>();
        foreach (var k in new[] { "Chain", "Legs", "Buy", "Sell", "Quantity", "Remove", "Clear", "Search", "All", "Near", "Call", "Put", "Strike", "Bid", "Ask", "Iv", "Oi", "Entry", "Empty", "NoChain", "Price", "Pnl", "Fit", "Hint", "Draft", "Mid", "Last", "Missing", "WhatIf", "Curve", "Expiry", "Cost", "Profit", "Loss", "Breakeven", "Greeks", "Loading", "Unavailable", "Margin", "Stock", "Delta", "Add", "Truncated", "Previous", "Next" })
            labels[k] = loc["Wb_" + k];
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, key = vm.WorkbenchKey, language = loc.Language, theme = WebChartSnapshot.Theme(palette),
            symbol = chain?.Underlying ?? vm.SymbolText, draftSymbol = vm.WorkbenchDraftSymbol, busy = vm.IsBusy, expiration = vm.SelectedExpiration?.Date.ToString("yyyy-MM-dd"),
            spot = F(vm.Spot), contracts, totalContracts = selected.Length, truncated = selected.Length > contracts.Length,
            source = chain?.Source, asOf = chain?.AsOf, fetchedAt = chain?.FetchedAt, delayMinutes = chain?.DelayMinutes, offline = (bool?)null,
            headline = vm.Headline, sourceText = vm.SourceText, delayText = vm.DelayText, model = vm.ModelText,
            note = vm.LocalNote, message = vm.WorkbenchSymbolMatches ? vm.WorkbenchMessage : loc["Wb_DifferentSymbol"], status = vm.StatusText,
            legs = vm.Legs.Select(l => new { id = l.Id, type = l.Type.ToString(), side = l.Side.ToString(), quantity = l.QtyText, premium = l.PremiumText, iv = l.IvText, strike = l.StrikeText, expiration = l.ExpiryText }),
            curve = valid && vm.HasCurve && vm.Curve is { } c ? c.Prices.Select((x, i) => new { price = F(x), expiry = F(c.ExpiryPnl[i]), horizon = c.ValuationPnl is { } h ? F(h[i]) : null }).ToArray() : [],
            expiryLabel = vm.ExpiryCurveLabel, horizonLabel = vm.HorizonLabel, horizonDays = vm.HorizonDays,
            metrics = new { cost = valid ? vm.CostText : "—", maxProfit = valid ? vm.MaxProfitText : "—", maxLoss = valid ? vm.MaxLossText : "—", breakeven = valid ? vm.BreakevenText : "—", greeks = valid ? vm.GreeksText : "—", pop = valid ? vm.PopText : "—", margin = valid ? vm.MarginText : "—" },
            greeks = new { delta = valid ? F(vm.WorkbenchGreeks?.Delta) : null, gamma = valid ? F(vm.WorkbenchGreeks?.Gamma) : null, theta = valid ? F(vm.WorkbenchGreeks?.Theta) : null, vega = valid ? F(vm.WorkbenchGreeks?.Vega) : null },
            whatIf = new { price = valid ? F(vm.WhatIfPrice) : null, value = valid ? vm.WhatIfValueText : "—", pnl = valid ? vm.WhatIfPnlText : "—", isProfit = valid && vm.WhatIfPnlIsProfit, isLoss = valid && vm.WhatIfPnlIsLoss }, labels,
        }, WebChartSnapshot.Json);
    }
    private static bool String(JsonElement e, string name, int max = 128) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s && s.Length <= max;
    private static bool Number(JsonElement e, string name, double min, double max) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) && n >= min && n <= max;
    public static bool ValidEvent(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !String(e, "kind") || !String(e, "key")) return false;
        return e.GetProperty("kind").GetString() switch
        {
            "add-contract" => String(e, "contract") && String(e, "side") && Number(e, "quantity", 1, 1000) && e.GetProperty("quantity").TryGetInt32(out _),
            "edit-leg" => String(e, "id") && Guid.TryParse(e.GetProperty("id").GetString(), out _) && String(e, "field")
                && (e.GetProperty("field").GetString() switch { "quantity" => Number(e, "quantity", 1, 1000000) && e.GetProperty("quantity").TryGetInt32(out _), "side" => String(e, "side"), "remove" => true, _ => false }),
            "clear-legs" => true,
            "what-if" => Number(e, "price", 0, 1e9),
            "horizon" => Number(e, "days", 0, 30) && e.GetProperty("days").TryGetInt32(out var d) && d is 0 or 7 or 14 or 30,
            _ => false,
        };
    }
    public static void Dispatch(StrategyViewModel vm, JsonElement e)
    {
        if (e.GetProperty("key").GetString() != vm.WorkbenchKey) { if (e.GetProperty("kind").GetString() == "what-if") return; vm.RejectWorkbench("Wb_Stale"); return; }
        static PositionSide? Side(JsonElement e) => e.GetProperty("side").GetString() switch { "Long" => PositionSide.Long, "Short" => PositionSide.Short, _ => null };
        switch (e.GetProperty("kind").GetString())
        {
            case "add-contract":
                if (Side(e) is { } side) vm.AddWorkbenchContract(e.GetProperty("contract").GetString()!, side, e.GetProperty("quantity").GetInt32());
                else vm.RejectWorkbench("Wb_Stale");
                break;
            case "edit-leg":
                var field = e.GetProperty("field").GetString()!;
                if (field == "side" && Side(e) is null) { vm.RejectWorkbench("Wb_Stale"); break; }
                vm.EditWorkbenchLeg(Guid.Parse(e.GetProperty("id").GetString()!), field,
                    field == "quantity" ? e.GetProperty("quantity").GetInt32() : 0, field == "side" ? Side(e)!.Value : PositionSide.Long);
                break;
            case "clear-legs": vm.ClearWorkbench(); break;
            case "what-if": vm.SetWhatIfPrice(e.GetProperty("price").GetDouble(), snap: false); break;
            case "horizon": vm.SelectHorizonCommand.Execute(e.GetProperty("days").GetInt32()); break;
        }
    }
}
