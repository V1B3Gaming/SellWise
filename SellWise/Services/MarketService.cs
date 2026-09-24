using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SellWise.Core;

namespace SellWise.Services;

/// <summary>Fetches and caches Universalis market data.</summary>
public sealed class MarketService : IDisposable
{
    private const string BaseUrl = "https://universalis.app/api/v2/";
    private const int BatchSize = 100;
    private const int ListingsPerItem = 20;
    private const int HistoryPerItem = 40;

    private readonly HttpClient http;
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<uint, ItemMarketData> cache = new();
    private string cacheWorld = "";
    private int version;

    public MarketService()
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SellWise-Dalamud/0.1");
    }

    public int Version => Volatile.Read(ref version);
    private volatile bool busy;
    public bool IsBusy { get => busy; private set => busy = value; }
    public string Status { get; private set; } = "No prices fetched yet.";
    public DateTimeOffset? LastRefresh { get; private set; }

    public ItemMarketData? Get(uint itemId) => cache.TryGetValue(itemId, out var d) ? d : null;

    /// <summary>
    /// Fetches items that aren't cached or are older than <paramref name="maxAge"/>. Returns immediately; work runs in the background.
    /// </summary>
    public void Refresh(IReadOnlyCollection<uint> itemIds, string world, string? dataCenter, TimeSpan maxAge, bool force)
    {
        if (IsBusy || string.IsNullOrEmpty(world)) return;

        if (!string.Equals(world, cacheWorld, StringComparison.OrdinalIgnoreCase))
        {
            cache.Clear();
            cacheWorld = world;
        }

        var now = DateTimeOffset.UtcNow;
        var stale = itemIds.Where(id => force || !cache.TryGetValue(id, out var d) || now - d.FetchedAt > maxAge).Distinct().ToList();
        if (stale.Count == 0) return;

        IsBusy = true;
        Status = $"Fetching prices for {stale.Count} items...";
        _ = Task.Run(() => FetchAll(stale, world, dataCenter, cts.Token));
    }

    private async Task FetchAll(List<uint> ids, string world, string? dataCenter, CancellationToken token)
    {
        var failed = 0;
        try
        {
            foreach (var batch in ids.Chunk(BatchSize))
            {
                var fetchedAt = DateTimeOffset.UtcNow;
                var idList = string.Join(",", batch);

                var json = await Get($"{Uri.EscapeDataString(world)}/{idList}?listings={ListingsPerItem}&entries={HistoryPerItem}", token);
                if (json == null)
                {
                    failed += batch.Length;
                    continue;
                }

                var parsed = UniversalisParser.ParseWorld(json, HistoryPerItem, fetchedAt).ToDictionary(d => d.ItemId);

                Dictionary<uint, DcSummary>? dc = null;
                if (!string.IsNullOrEmpty(dataCenter))
                {
                    var dcJson = await Get($"{Uri.EscapeDataString(dataCenter)}/{idList}?listings=5&entries=0", token);
                    if (dcJson != null) dc = UniversalisParser.ParseDataCenter(dcJson);
                }

                foreach (var id in batch)
                {
                    // Cache a no-data entry for unresolved items so they aren't re-requested every refresh.
                    var data = parsed.TryGetValue(id, out var d) ? d : new ItemMarketData { ItemId = id, FetchedAt = fetchedAt };
                    if (dc != null && dc.TryGetValue(id, out var summary)) data.DataCenter = summary;
                    cache[id] = data;
                }

                Interlocked.Increment(ref version);
                await Task.Delay(150, token); // stay well under Universalis' rate limit
            }

            LastRefresh = DateTimeOffset.UtcNow;
            Status = failed == 0 ? $"Prices updated for {ids.Count} items." : $"Prices updated; {failed} items failed (Universalis unreachable?).";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Universalis refresh failed");
            Status = $"Price refresh failed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
            Interlocked.Increment(ref version);
        }
    }

    private readonly ConcurrentDictionary<uint, (AggregatedPrice Price, DateTimeOffset FetchedAt)> aggregated = new();
    private string aggregatedWorld = "";

    public AggregatedPrice? GetAggregated(uint itemId) => aggregated.TryGetValue(itemId, out var a) ? a.Price : null;

    /// <summary>
    /// Fetches aggregated price summaries (min listing, average sale, daily velocity) for many items, a few requests at a time.
    /// Cached entries younger than <paramref name="maxAge"/> are reused.
    /// </summary>
    public async Task FetchAggregatedAsync(IReadOnlyCollection<uint> itemIds, string world, TimeSpan maxAge, IProgress<(int Done, int Total)>? progress, CancellationToken token)
    {
        if (!string.Equals(world, aggregatedWorld, StringComparison.OrdinalIgnoreCase))
        {
            aggregated.Clear();
            aggregatedWorld = world;
        }

        var now = DateTimeOffset.UtcNow;
        var needed = itemIds.Distinct().Where(id => !aggregated.TryGetValue(id, out var a) || now - a.FetchedAt > maxAge).ToList();
        var batches = needed.Chunk(BatchSize).ToList();
        var done = 0;
        progress?.Report((0, batches.Count));

        using var gate = new SemaphoreSlim(4); // Universalis allows 8 concurrent connections, but large aggregated batches time out above ~4.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
        var tasks = batches.Select(async batch =>
        {
            await gate.WaitAsync(linked.Token);
            try
            {
                var json = await Get($"aggregated/{Uri.EscapeDataString(world)}/{string.Join(",", batch)}", linked.Token);
                var fetchedAt = DateTimeOffset.UtcNow;
                var parsed = json != null ? UniversalisParser.ParseAggregated(json) : [];
                foreach (var id in batch)
                {
                    // Remember misses too, so an unsold item isn't re-requested on every scan.
                    var price = parsed.TryGetValue(id, out var p) ? p : new AggregatedPrice(id, QualityPrices.None, QualityPrices.None);
                    if (json != null) aggregated[id] = (price, fetchedAt);
                }
            }
            finally
            {
                gate.Release();
                progress?.Report((Interlocked.Increment(ref done), batches.Count));
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task<string?> Get(string relative, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(BaseUrl + relative, token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), token);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Plugin.Log.Warning($"Universalis returned {(int)response.StatusCode} for {relative}");
                    return null;
                }

                return await response.Content.ReadAsStringAsync(token);
            }
            catch (HttpRequestException e)
            {
                Plugin.Log.Warning($"Universalis request failed ({e.Message}), attempt {attempt + 1}");
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), token);
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                Plugin.Log.Warning($"Universalis request timed out, attempt {attempt + 1}");
            }
        }

        return null;
    }

    public void Dispose()
    {
        cts.Cancel();
        http.Dispose();
        cts.Dispose();
    }
}
