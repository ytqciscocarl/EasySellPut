using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meili.Analytics;
using Meili.Automation;
using Meili.Core.Models;

namespace Meili.Shared.ViewModels;

public partial class StrategyViewModel
{
    public StrategyGreeks? WorkbenchGreeks { get; private set; }
    private long _workbenchChainRevision;
    private string? _workbenchDraftSymbol;
    public string WorkbenchDraftSymbol => _workbenchDraftSymbol ?? _loadedSymbol ?? SymbolText;
    public bool WorkbenchSymbolMatches => Legs.Count == 0 || string.Equals(WorkbenchDraftSymbol, _hdrChain?.Underlying ?? SymbolText, StringComparison.OrdinalIgnoreCase);
    public OptionChain? WorkbenchChain => _hdrChain;
    public string WorkbenchMessage { get; private set; } = "";
    public string WorkbenchKey
    {
        get
        {
            var text = $"{_workbenchChainRevision}|{_loadedSymbol}|{SymbolText}|{SelectedExpiration?.Date}|{_hdrChain?.Source}|{_hdrChain?.AsOf:O}|{IsBusy}|" +
                string.Join(";", Legs.Select(l => $"{l.Id}:{l.Type}:{l.Side}:{l.StrikeText}:{l.ExpiryText}:{l.QtyText}:{l.PremiumText}:{l.IvText}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }

    /// <summary>Append only a real contract in the current chain. Prices are selected in C#, never accepted from HTML.</summary>
    public bool AddWorkbenchContract(string contractSymbol, PositionSide side, int quantity)
    {
        var chain = _hdrChain;
        var contract = chain?.Contracts.FirstOrDefault(c => c.ContractSymbol == contractSymbol && c.Expiration == SelectedExpiration?.Date);
        if (IsBusy || chain is null || contract is null || !string.Equals(chain.Underlying, SymbolText, StringComparison.OrdinalIgnoreCase)
            || !WorkbenchSymbolMatches || !string.Equals(contract.Underlying, chain.Underlying, StringComparison.OrdinalIgnoreCase) || !double.IsFinite(contract.Strike) || contract.Strike <= 0 || !Enum.IsDefined(contract.Right)
            || quantity is < 1 or > 1000 || !Enum.IsDefined(side) || Legs.Count >= 32)
            return RejectWorkbench("Wb_Stale");
        // An observed mid/last is required for a click-to-add. Model-only estimates remain visible, never become a fabricated execution price.
        var premium = OptionEntryReference.From(contract).Price;
        if (premium is not { } price || !double.IsFinite(price)) return RejectWorkbench("Wb_NoQuote");
        var enriched = ToListed(contract, Spot ?? 0, _inputs.Enrichment(DateTimeOffset.Now, chain));
        var leg = contract.Right == Meili.Core.Models.OptionRight.Call
            ? StrategyLeg.Call(side, contract.Strike, price, contract.Expiration, quantity, enriched.ImpliedVolatility)
            : StrategyLeg.Put(side, contract.Strike, price, contract.Expiration, quantity, enriched.ImpliedVolatility);
        _suppress = true;
        try { Legs.Add(StrategyLegRow.From(leg, this)); SelectedLeg = Legs.Last(); }
        finally { _suppress = false; }
        CommitWorkbenchEdit();
        return true;
    }

    public bool EditWorkbenchLeg(Guid id, string field, int quantity = 0, PositionSide side = PositionSide.Long)
    {
        var row = Legs.FirstOrDefault(l => l.Id == id);
        if (row is null || IsBusy || field is not ("quantity" or "side" or "remove")
            || field == "quantity" && (quantity < 1 || quantity > (row.Type == LegType.Stock ? 1000000 : 1000))
            || field == "side" && !Enum.IsDefined(side)) return RejectWorkbench("Wb_Stale");
        _suppress = true;
        try
        {
            if (field == "quantity") row.QtyText = quantity.ToString(CultureInfo.InvariantCulture);
            else if (field == "side") row.Side = side;
            else Legs.Remove(row);
        }
        finally { _suppress = false; }
        CommitWorkbenchEdit();
        return true;
    }
    public void ClearWorkbench()
    {
        if (IsBusy) return;
        Legs.Clear(); SelectedLeg = null;
        CommitWorkbenchEdit();
    }
    public bool RejectWorkbench(string key)
    {
        WorkbenchMessage = _loc[key]; ChartChanged?.Invoke(this, EventArgs.Empty); return false;
    }
    private void CommitWorkbenchEdit()
    {
        WorkbenchMessage = "";
        _workbenchDraftSymbol = Legs.Count == 0 ? null : _hdrChain?.Underlying;
        _manualLegs = true;
        foreach (var row in Legs) row.LockEntryPrice();
        _suppress = true;
        try { SelectedPreset = Presets.FirstOrDefault(p => p.Kind == StrategyKind.Custom) ?? SelectedPreset; }
        finally { _suppress = false; }
        Recompute();
        // A quantity/side edit must not asynchronously replace the entry premiums of unrelated legs.
        ChartChanged?.Invoke(this, EventArgs.Empty);
    }
}
