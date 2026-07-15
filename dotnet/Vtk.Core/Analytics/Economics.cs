// Dollarizes byte savings from the invocation log (#58, decision in
// docs/Architecture.md registry via #46): a local checked-in model→$ price
// table (updated by PR — the no-network non-goal stands) and a bytes/4 token
// heuristic. Saved bytes are priced at the model's *input* $/MTok rate: tool
// output enters the agent's context as input tokens.
namespace Vtk.Core.Analytics;

public static class Economics
{
    /// <summary>Model assumed when the caller does not name one.</summary>
    public const string DefaultModel = "claude-sonnet-4.5";

    /// <summary>
    /// Checked-in input-token prices in USD per million tokens. Approximate
    /// by design (decision #46/#58); update by PR when list prices move.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> UsdPerMTokInput =
        new Dictionary<string, double>
        {
            ["claude-opus-4.1"] = 15.00,
            ["claude-sonnet-4.5"] = 3.00,
            ["claude-haiku-4.5"] = 1.00,
        };

    /// <summary>bytes/4 heuristic (decision #46/#58): approximate tokens for a byte count.</summary>
    public static long TokensFromBytes(long bytes) => bytes / 4;

    /// <summary>The input-token price for a model, falling back to the default model for unknown names.</summary>
    public static double PriceFor(string model) =>
        UsdPerMTokInput.TryGetValue(model, out var p) ? p : UsdPerMTokInput[DefaultModel];

    /// <summary>Approximate USD cost of <paramref name="tokens"/> input tokens under <paramref name="model"/>.</summary>
    public static double CostUsd(long tokens, string model = DefaultModel) =>
        tokens / 1_000_000.0 * PriceFor(model);

    /// <summary>Convenience: approximate USD value of a saved byte count under the default model.</summary>
    public static double SavedUsd(long bytes, string model = DefaultModel) =>
        CostUsd(TokensFromBytes(bytes), model);
}
