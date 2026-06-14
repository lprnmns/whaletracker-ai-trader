using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/hyperliquid-reports")]
public sealed class HyperliquidReportsController : ControllerBase
{
    [HttpGet("runs")]
    public IActionResult ListRuns()
    {
        var root = ResolveReportsRoot();
        if (!root.Exists)
        {
            return Ok(Array.Empty<object>());
        }

        var runs = root.GetDirectories()
            .OrderByDescending(x => Directory.GetLastWriteTimeUtc(x.FullName))
            .Take(30)
            .Select(x => new
            {
                id = x.Name,
                createdAt = Directory.GetCreationTimeUtc(x.FullName),
                traderCount = x.GetDirectories().Count(IsAddressDirectory),
                hasSummary = System.IO.File.Exists(Path.Combine(x.FullName, "trader_summaries.csv")),
                hasHistoricalScoreboard = System.IO.File.Exists(HistoricalScoreboardPath(x))
            });
        return Ok(runs);
    }

    [HttpGet("runs/{runId}/traders")]
    public IActionResult ListTraders(string runId)
    {
        var run = ResolveRun(runId);
        if (run == null)
        {
            return NotFound();
        }

        var summaryCsv = Path.Combine(run.FullName, "trader_summaries.csv");
        if (System.IO.File.Exists(summaryCsv))
        {
            return Ok(MergeHistoricalScores(run, ReadCsv(summaryCsv))
                .OrderByDescending(x => DecimalValue(x, "okx_tradable_net_pnl_usd"))
                .ThenByDescending(x => DecimalValue(x, "net_closed_pnl_usd"))
                .ToList());
        }

        var summaries = MergeHistoricalScores(run, run.GetDirectories()
            .Where(IsAddressDirectory)
            .Select(directory => ReadSummary(directory))
            .Where(row => row.Count > 0))
            .OrderByDescending(row => DecimalValue(row, "okx_tradable_net_pnl_usd"))
            .ThenByDescending(row => DecimalValue(row, "net_closed_pnl_usd"))
            .ToList();
        return Ok(summaries);
    }

    [HttpGet("runs/{runId}/historical-scoreboard")]
    public IActionResult GetHistoricalScoreboard(string runId)
    {
        var run = ResolveRun(runId);
        if (run == null)
        {
            return NotFound();
        }

        var rows = ReadCsv(HistoricalScoreboardPath(run))
            .OrderBy(x => DecimalValue(x, "rank") == 0 ? decimal.MaxValue : DecimalValue(x, "rank"))
            .ThenByDescending(x => DecimalValue(x, "historical_quality_score"))
            .ToList();
        return Ok(rows);
    }

    [HttpGet("runs/{runId}/historical-scoreboard/{address}")]
    public IActionResult GetHistoricalScoreboardTrader(string runId, string address)
    {
        var run = ResolveRun(runId);
        if (run == null || !IsAddress(address))
        {
            return NotFound();
        }

        var row = ReadCsv(HistoricalScoreboardPath(run))
            .FirstOrDefault(x => string.Equals(x.GetValueOrDefault("address"), address, StringComparison.OrdinalIgnoreCase));
        return row == null ? NotFound() : Ok(row);
    }

    [HttpGet("runs/{runId}/traders/{address}")]
    public IActionResult GetTrader(string runId, string address, [FromQuery] int limit = 200)
    {
        var run = ResolveRun(runId);
        if (run == null || !IsAddress(address))
        {
            return NotFound();
        }

        var directory = new DirectoryInfo(Path.Combine(run.FullName, address.ToLowerInvariant()));
        if (!directory.Exists)
        {
            return NotFound();
        }

        limit = Math.Clamp(limit, 20, 1000);
        var summary = MergeHistoricalScores(run, new[] { ReadSummary(directory) }).First();
        return Ok(new
        {
            address = address.ToLowerInvariant(),
            summary,
            activePositions = ReadCsv(Path.Combine(directory.FullName, "active_positions.csv")).Take(limit),
            closedPositions = ReadCsv(Path.Combine(directory.FullName, "closed_positions.csv")).Take(limit),
            positionEvents = ReadCsv(Path.Combine(directory.FullName, "position_events.csv")).Take(limit),
            coinSummary = ReadCsv(Path.Combine(directory.FullName, "coin_summary.csv")).Take(limit),
            fills = ReadCsv(Path.Combine(directory.FullName, "fills.csv")).Take(limit)
        });
    }

    private static DirectoryInfo ResolveReportsRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var reports = new DirectoryInfo(Path.Combine(current.FullName, "data", "reports", "hyperliquid_profiles"));
            if (reports.Exists)
            {
                return reports;
            }
            current = current.Parent;
        }

        return new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "data", "reports", "hyperliquid_profiles"));
    }

    private static DirectoryInfo? ResolveRun(string runId)
    {
        if (runId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-'))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.Combine(ResolveReportsRoot().FullName, runId));
        return directory.Exists ? directory : null;
    }

    private static string HistoricalScoreboardPath(DirectoryInfo run) =>
        Path.Combine(run.FullName, "historical_scoreboard", "historical_scoreboard.csv");

    private static IEnumerable<Dictionary<string, string>> MergeHistoricalScores(
        DirectoryInfo run,
        IEnumerable<Dictionary<string, string>> summaries)
    {
        var scores = ReadCsv(HistoricalScoreboardPath(run))
            .Where(row => row.TryGetValue("address", out var address) && !string.IsNullOrWhiteSpace(address))
            .ToDictionary(row => row["address"], StringComparer.OrdinalIgnoreCase);

        foreach (var summary in summaries)
        {
            if (summary.TryGetValue("address", out var address) &&
                scores.TryGetValue(address, out var score))
            {
                foreach (var item in score.Where(item =>
                    item.Key.StartsWith("okx_", StringComparison.OrdinalIgnoreCase) ||
                    item.Key is "historical_quality_score" or "confidence_score" or "rank"))
                {
                    summary[item.Key] = item.Value;
                }
            }

            yield return summary;
        }
    }

    private static bool IsAddressDirectory(DirectoryInfo directory) => IsAddress(directory.Name);

    private static bool IsAddress(string value)
    {
        return value.Length == 42 &&
            value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            value.Skip(2).All(Uri.IsHexDigit);
    }

    private static Dictionary<string, string> ReadSummary(DirectoryInfo directory)
    {
        var path = Path.Combine(directory.FullName, "summary.json");
        if (!System.IO.File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["address"] = directory.Name
            };
        }

        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(System.IO.File.ReadAllText(path));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json ?? new Dictionary<string, JsonElement>())
        {
            result[item.Key] = item.Value.ValueKind == JsonValueKind.String
                ? item.Value.GetString() ?? string.Empty
                : item.Value.ToString();
        }
        return result;
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return new List<Dictionary<string, string>>();
        }

        var lines = System.IO.File.ReadAllLines(path);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            return new List<Dictionary<string, string>>();
        }

        var headers = ParseCsvLine(lines[0]);
        return lines
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                var values = ParseCsvLine(line);
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < headers.Count; index++)
                {
                    row[headers[index]] = index < values.Count ? values[index] : string.Empty;
                }
                return row;
            })
            .ToList();
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (ch == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        values.Add(current.ToString());
        return values;
    }

    private static decimal DecimalValue(Dictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out var value) &&
            decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
