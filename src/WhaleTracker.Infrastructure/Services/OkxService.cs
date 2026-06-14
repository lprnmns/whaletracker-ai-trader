using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.Infrastructure.Services;

/// <summary>
/// OKX Futures API Servisi
/// İşlem açma/kapatma ve hesap bilgisi
/// 
/// API Dokümantasyonu: https://www.okx.com/docs-v5/en/
/// 
/// ÖNEMLİ: Bu sınıf senin verdiğin pseudo-code mantığını içerecek!
/// </summary>
public class OkxService : IOkxService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OkxService> _logger;
    private readonly OkxSettings _settings;
    private readonly TradingSettings _tradingSettings;

    // ================================================================
    // INSTRUMENT CACHE - Her coin için kontrat bilgisi
    // ================================================================
    private static readonly Dictionary<string, InstrumentInfo> _instrumentCache = new();
    private static readonly object _cacheLock = new();
    private static readonly TimeSpan _instrumentCacheExpiry = TimeSpan.FromHours(1);
    private static readonly TimeSpan _priceCacheExpiry = TimeSpan.FromSeconds(30);

    // OKX SWAP sembol listesi cache
    private static readonly HashSet<string> _supportedSymbolsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _supportedSymbolsLock = new();
    private static readonly TimeSpan _supportedSymbolsCacheExpiry = TimeSpan.FromHours(6);
    private static DateTime _supportedSymbolsUpdatedAt = DateTime.MinValue;
    private static readonly TimeSpan _accountConfigCacheExpiry = TimeSpan.FromMinutes(5);
    private static OkxAccountConfiguration? _accountConfigCache;
    private static DateTime _accountConfigUpdatedAt = DateTime.MinValue;
    private static readonly object _accountConfigLock = new();
    private const string SupportedSymbolsFileName = "data/okx_futures_symbols.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private string MarginMode =>
        string.Equals(_settings.MarginMode, "isolated", StringComparison.OrdinalIgnoreCase)
            ? "isolated"
            : "cross";

    public OkxService(
        HttpClient httpClient,
        ILogger<OkxService> logger,
        IOptions<AppSettings> settings)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings.Value.Okx;
        _tradingSettings = settings.Value.Trading;

        _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
    }

    // ================================================================
    // HESAP BİLGİLERİ
    // ================================================================

    public async Task<UserStats> GetAccountInfoAsync()
    {
        _logger.LogInformation("GetAccountInfoAsync çağrıldı");

        try
        {
            // 1. Hesap bakiyesini çek
            var balanceResponse = await SendGetRequestAsync<OkxBalanceResponse>("/api/v5/account/balance");

            if (balanceResponse?.Code != "0" || balanceResponse.Data == null || !balanceResponse.Data.Any())
            {
                _logger.LogWarning("Bakiye bilgisi alınamadı: {Code} - {Msg}", 
                    balanceResponse?.Code, balanceResponse?.Msg);
                
                return new UserStats
                {
                    TotalUsd = 0,
                    Leverage = _tradingSettings.DefaultLeverage,
                    ActivePositions = new List<Position>()
                };
            }

            var accountData = balanceResponse.Data[0];
            
            // totalEq = Toplam USD değeri
            // InvariantCulture kullanarak parse et (nokta ondalık ayırıcı)
            decimal totalEquity = 0;
            if (decimal.TryParse(accountData.TotalEq, System.Globalization.NumberStyles.Any, 
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                totalEquity = parsed;
            }

            _logger.LogInformation("Hesap bakiyesi: {TotalEq:F2} USD", totalEquity);

            // 2. Açık pozisyonları çek
            var positions = await GetAllPositionsAsync();

            return new UserStats
            {
                TotalUsd = totalEquity,
                Leverage = _tradingSettings.DefaultLeverage,
                ActivePositions = positions
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAccountInfoAsync hatası!");
            throw;
        }
    }

    public async Task<OkxAccountConfiguration> GetAccountConfigurationAsync()
    {
        lock (_accountConfigLock)
        {
            if (_accountConfigCache != null &&
                DateTime.UtcNow - _accountConfigUpdatedAt < _accountConfigCacheExpiry)
            {
                return _accountConfigCache;
            }
        }

        var response = await SendGetRequestAsync<OkxAccountConfigResponse>("/api/v5/account/config");
        var data = response?.Data?.FirstOrDefault();

        var config = new OkxAccountConfiguration
        {
            AccountLevel = data?.AcctLv ?? string.Empty,
            PositionMode = data?.PosMode ?? string.Empty,
            MarginMode = MarginMode,
            IsDemo = _settings.IsDemo
        };

        lock (_accountConfigLock)
        {
            _accountConfigCache = config;
            _accountConfigUpdatedAt = DateTime.UtcNow;
        }

        return config;
    }

    public async Task<Position?> GetPositionAsync(string symbol)
    {
        _logger.LogInformation("GetPositionAsync çağrıldı: {Symbol}", symbol);

        try
        {
            // SWAP formatına çevir: ETH -> ETH-USDT-SWAP
            var instId = $"{symbol.ToUpper()}-USDT-SWAP";
            
            var response = await SendGetRequestAsync<OkxPositionsResponse>(
                $"/api/v5/account/positions?instType=SWAP&instId={instId}");

            if (response?.Code != "0" || response.Data == null || !response.Data.Any())
            {
                _logger.LogInformation("Pozisyon bulunamadı: {Symbol}", symbol);
                return null;
            }

            var pos = response.Data[0];
            
            // pos == "0" ise pozisyon yok demektir
            if (string.IsNullOrEmpty(pos.Pos) || pos.Pos == "0")
            {
                return null;
            }

            return MapToPosition(pos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPositionAsync hatası: {Symbol}", symbol);
            throw;
        }
    }

    public async Task<List<Position>> GetAllPositionsAsync()
    {
        _logger.LogInformation("GetAllPositionsAsync çağrıldı");

        try
        {
            // Sadece SWAP (perpetual futures) pozisyonları çek
            var response = await SendGetRequestAsync<OkxPositionsResponse>(
                "/api/v5/account/positions?instType=SWAP");

            if (response?.Code != "0" || response.Data == null)
            {
                _logger.LogWarning("Pozisyon bilgisi alınamadı: {Code} - {Msg}", 
                    response?.Code, response?.Msg);
                return new List<Position>();
            }

            var positions = new List<Position>();

            foreach (var pos in response.Data)
            {
                // pos == "0" veya boş ise atla
                if (string.IsNullOrEmpty(pos.Pos) || pos.Pos == "0")
                    continue;

                var position = MapToPosition(pos);
                if (position != null)
                {
                    positions.Add(position);
                }
            }

            _logger.LogInformation("Toplam {Count} açık pozisyon bulundu", positions.Count);
            return positions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAllPositionsAsync hatası!");
            throw;
        }
    }

    // ================================================================
    // SUPPORTED SYMBOLS
    // ================================================================

    public async Task<IReadOnlyCollection<string>> GetSupportedSymbolsAsync(bool forceRefresh = false)
    {
        lock (_supportedSymbolsLock)
        {
            if (!forceRefresh &&
                _supportedSymbolsCache.Count > 0 &&
                DateTime.UtcNow - _supportedSymbolsUpdatedAt < _supportedSymbolsCacheExpiry)
            {
                return _supportedSymbolsCache.ToList();
            }
        }

        if (!forceRefresh && TryLoadSupportedSymbolsFromFile(out var fileSymbols))
        {
            lock (_supportedSymbolsLock)
            {
                _supportedSymbolsCache.Clear();
                foreach (var symbol in fileSymbols)
                {
                    _supportedSymbolsCache.Add(symbol);
                }
                _supportedSymbolsUpdatedAt = DateTime.UtcNow;
            }

            return fileSymbols.ToList();
        }

        return await RefreshSupportedSymbolsAsync();
    }

    public async Task<bool> IsSymbolSupportedAsync(string symbol, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var list = await GetSupportedSymbolsAsync(forceRefresh);
        return list.Contains(symbol.ToUpperInvariant());
    }

    private async Task<IReadOnlyCollection<string>> RefreshSupportedSymbolsAsync()
    {
        try
        {
            var response = await SendGetRequestAsync<OkxInstrumentResponse>(
                "/api/v5/public/instruments?instType=SWAP");

            if (response?.Code != "0" || response.Data == null)
            {
                _logger.LogWarning("Supported symbols alınamadı: {Code} - {Msg}",
                    response?.Code, response?.Msg);
                lock (_supportedSymbolsLock)
                {
                    return _supportedSymbolsCache.ToList();
                }
            }

            var symbols = response.Data
                .Select(d => d.InstId?.Split('-')[0])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (_supportedSymbolsLock)
            {
                _supportedSymbolsCache.Clear();
                foreach (var symbol in symbols)
                {
                    _supportedSymbolsCache.Add(symbol);
                }
                _supportedSymbolsUpdatedAt = DateTime.UtcNow;
            }

            TrySaveSupportedSymbolsToFile(symbols);

            _logger.LogInformation("Supported symbols güncellendi. Toplam: {Count}", symbols.Count);
            return symbols;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Supported symbols güncellenemedi");
            lock (_supportedSymbolsLock)
            {
                return _supportedSymbolsCache.ToList();
            }
        }
    }

    private static bool TryLoadSupportedSymbolsFromFile(out HashSet<string> symbols)
    {
        symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var path = GetSupportedSymbolsFilePath();
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();

            foreach (var symbol in list)
            {
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    symbols.Add(symbol.ToUpperInvariant());
                }
            }

            return symbols.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySaveSupportedSymbolsToFile(List<string> symbols)
    {
        try
        {
            var path = GetSupportedSymbolsFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(symbols, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(path, json);
        }
        catch
        {
            // Dosya yazımı kritik değil, sessiz geç
        }
    }

    private static string GetSupportedSymbolsFilePath()
    {
        var root = Directory.GetCurrentDirectory();
        return Path.Combine(root, SupportedSymbolsFileName);
    }

    private Position? MapToPosition(OkxPositionData pos)
    {
        // InstId'den sembol çıkar: ETH-USDT-SWAP -> ETH
        var symbol = pos.InstId?.Split('-')[0] ?? "";

        // Pozisyon yönünü belirle
        string direction;
        if (pos.PosSide == "long")
        {
            direction = "Long";
        }
        else if (pos.PosSide == "short")
        {
            direction = "Short";
        }
        else // net mode
        {
            // pos değeri pozitif ise Long, negatif ise Short
            if (decimal.TryParse(pos.Pos, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var posValue))
            {
                direction = posValue >= 0 ? "Long" : "Short";
            }
            else
            {
                direction = "Long";
            }
        }

        // InvariantCulture ile parse et (nokta = ondalık ayırıcı)
        decimal.TryParse(pos.Margin, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var margin);
        decimal.TryParse(pos.AvgPx, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var avgPx);
        decimal.TryParse(pos.Pos, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var size);
        decimal.TryParse(pos.Upl, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var upl);
        decimal.TryParse(pos.NotionalUsd, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var notionalUsd);
        decimal.TryParse(pos.Lever, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var leverage);

        if (margin <= 0 && notionalUsd != 0 && leverage > 0)
        {
            margin = Math.Abs(notionalUsd) / leverage;
        }

        return new Position
        {
            Symbol = symbol,
            Direction = direction,
            MarginUsd = margin,
            EntryPrice = avgPx,
            Size = Math.Abs(size),
            UnrealizedPnl = upl
        };
    }

    // ================================================================
    // ANA İŞLEM METODU - SENİN PSEUDO-CODE MANTIĞIN BURADA
    // ================================================================

    /// <summary>
    /// İşlem sinyalini çalıştırır
    /// 
    /// KURAL 1: OPEN komutlarında kontrol yapma, direkt borsaya gönder
    /// KURAL 2: CLOSE komutlarında pozisyon var mı bak, %95 kuralını uygula
    /// </summary>
    public async Task<TradeResult> ExecuteTradeAsync(TradeSignal signal)
    {
        _logger.LogInformation(
            "ExecuteTradeAsync başladı: {Action} {Symbol} {Amount} USDT",
            signal.Action, signal.Symbol, signal.MarginAmountUSDT);

        try
        {
            // ================================================================
            // SENARYO 1: POZİSYON AÇMA / EKLEME (Fire and Forget)
            // ================================================================
            if (signal.Action == TradeAction.OPEN_LONG || signal.Action == TradeAction.OPEN_SHORT)
            {
                _logger.LogInformation("OPEN emri işleniyor: {Action}", signal.Action);

                // 1. Kaldıracı ayarla
                await SetLeverageAsync(signal.Symbol, signal.Leverage);

                // 2. Kontrat say?s?n? hesapla
                var calculation = await CalculateOrderAsync(
                    signal.Symbol,
                    signal.MarginAmountUSDT,
                    signal.Leverage,
                    "OPEN");

                if (!calculation.IsValid)
                {
                    _logger.LogWarning("Order invalid: {Message}", calculation.ValidationMessage);
                    return new TradeResult
                    {
                        Success = false,
                        Symbol = signal.Symbol,
                        ErrorMessage = calculation.ValidationMessage
                    };
                }

                var contracts = calculation.Contracts;

                // 4. Emir parametrelerini belirle
                string side, posSide;
                if (signal.Action == TradeAction.OPEN_LONG)
                {
                    side = "buy";
                    posSide = "long";
                }
                else // OPEN_SHORT
                {
                    side = "sell";
                    posSide = "short";
                }

                _logger.LogInformation(
                    "Emir gönderiliyor: {Side} {PosSide} {Contracts} kontrat {Symbol}",
                    side, posSide, contracts, signal.Symbol);

                // 5. Market emri gönder
                return await PlaceMarketOrderAsync(signal.Symbol, side, posSide, contracts);
            }

            // ================================================================
            // SENARYO 2: POZİSYON KAPATMA (Check & Smart Cleanup)
            // ================================================================
            if (signal.Action == TradeAction.CLOSE_LONG || signal.Action == TradeAction.CLOSE_SHORT)
            {
                _logger.LogInformation("CLOSE emri işleniyor: {Action}", signal.Action);

                // 1. Önce pozisyon var mı kontrol et
                string direction = signal.Action == TradeAction.CLOSE_LONG ? "long" : "short";
                var expectedDirection = signal.Action == TradeAction.CLOSE_LONG ? "Long" : "Short";
                var activePosition = (await GetAllPositionsAsync())
                    .FirstOrDefault(position =>
                        string.Equals(position.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(position.Direction, expectedDirection, StringComparison.OrdinalIgnoreCase));

                if (activePosition == null || activePosition.Size <= 0)
                {
                    _logger.LogWarning("SKIP: Kapatılacak pozisyon YOK! Symbol: {Symbol}", signal.Symbol);
                    return new TradeResult
                    {
                        Success = true,
                        Symbol = signal.Symbol,
                        ErrorMessage = "Pozisyon yok, işlem atlandı"
                    };
                }

                // 2. Dust threshold cleanup (percent based)
                var activeMargin = activePosition.MarginUsd;
                var requestedClose = signal.MarginAmountUSDT;

                if (requestedClose <= 0)
                {
                    _logger.LogWarning("SKIP: Close amount <= 0 for {Symbol}", signal.Symbol);
                    return new TradeResult
                    {
                        Success = true,
                        Symbol = signal.Symbol,
                        ErrorMessage = "Close amount <= 0, skipped"
                    };
                }

                if (activeMargin <= 0)
                {
                    _logger.LogWarning(
                        "Active margin could not be estimated for {Symbol}; falling back to full close.",
                        signal.Symbol);

                    return await ClosePositionAsync(signal.Symbol, direction);
                }

                var fullCloseThreshold = activeMargin * (_tradingSettings.DustThresholdPercent / 100m);

                if (requestedClose >= activeMargin || requestedClose >= fullCloseThreshold)
                {
                    _logger.LogInformation(
                        "Smart Cleanup: FULL CLOSE ({Amount} >= {Threshold})",
                        requestedClose, fullCloseThreshold);

                    return await ClosePositionAsync(signal.Symbol, direction);
                }

                var instrument = await GetInstrumentInfoAsync(signal.Symbol);
                if (instrument == null)
                {
                    _logger.LogWarning("Instrument not found: {Symbol}", signal.Symbol);
                    return new TradeResult
                    {
                        Success = false,
                        Symbol = signal.Symbol,
                        ErrorMessage = $"Instrument not found: {signal.Symbol}"
                    };
                }

                var closeRatio = requestedClose / activeMargin;
                var rawContracts = activePosition.Size * closeRatio;
                var lotSz = instrument.LotSz > 0 ? instrument.LotSz : instrument.MinSz;
                var contracts = Math.Floor(rawContracts / lotSz) * lotSz;

                if (contracts <= 0)
                {
                    contracts = instrument.MinSz;
                }

                if (contracts >= activePosition.Size)
                {
                    _logger.LogInformation("Smart Cleanup: FULL CLOSE (contracts >= position size)");
                    return await ClosePositionAsync(signal.Symbol, direction);
                }

                if (contracts < instrument.MinSz)
                {
                    _logger.LogWarning("SKIP: Close contracts below minimum for {Symbol}", signal.Symbol);
                    return new TradeResult
                    {
                        Success = true,
                        Symbol = signal.Symbol,
                        ErrorMessage = "Close contracts below minimum, skipped"
                    };
                }

                _logger.LogInformation(
                    "Partial close: {Contracts} contracts ({Amount} USDT, Active: {Active})",
                    contracts, requestedClose, activeMargin);

                // Partial close uses reduce-only order
                string side = signal.Action == TradeAction.CLOSE_LONG ? "sell" : "buy";

                return await PlaceMarketOrderAsync(signal.Symbol, side, direction, contracts, reduceOnly: true);
            }
            // IGNORE sinyali
            _logger.LogInformation("İşlem IGNORE edildi: {Reason}", signal.Reason);
            return new TradeResult
            {
                Success = true,
                ErrorMessage = "İşlem ignore edildi"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecuteTradeAsync hatası!");
            return new TradeResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    // ================================================================
    // EMİR METODLARI
    // ================================================================

    public async Task<TradeResult> PlaceMarketOrderAsync(string symbol, string side, string posSide, decimal size, bool reduceOnly = false)
    {
        _logger.LogInformation(
            "PlaceMarketOrderAsync: {Symbol} {Side} {PosSide} {Size} (reduceOnly: {ReduceOnly})",
            symbol, side, posSide, size, reduceOnly);

        try
        {
            var instId = $"{symbol.ToUpper()}-USDT-SWAP";
            var clientOrderId = CreateClientOrderId(symbol, side);
            var okxPosSide = await ResolveOkxPosSideAsync(posSide);

            var requestBody = new Dictionary<string, object>
            {
                { "instId", instId },
                { "tdMode", MarginMode },
                { "side", side },           // buy veya sell
                { "posSide", okxPosSide },
                { "ordType", "market" },
                { "sz", size.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "clOrdId", clientOrderId }
            };

            if (reduceOnly)
            {
                requestBody["reduceOnly"] = true;
            }

            var response = await SendPostRequestAsync<OkxOrderResponse>("/api/v5/trade/order", requestBody);

            var detail = response?.Data?.FirstOrDefault();
            if (response?.Code != "0" || (detail?.SCode is not null && detail.SCode != "0"))
            {
                _logger.LogError(
                    "Emir başarısız: {Code} - {Msg} (sCode: {SCode}, sMsg: {SMsg})",
                    response?.Code,
                    response?.Msg,
                    detail?.SCode ?? "n/a",
                    detail?.SMsg ?? "n/a");
                return new TradeResult
                {
                    Success = false,
                    ErrorMessage = $"OKX Error: {response?.Code} - {response?.Msg} (sCode: {detail?.SCode ?? "n/a"}, sMsg: {detail?.SMsg ?? "n/a"})"
                };
            }

            var orderId = detail?.OrdId ?? "";
            _logger.LogInformation("Emir başarılı! OrderId: {OrderId}", orderId);

            return new TradeResult
            {
                Success = true,
                OrderId = orderId,
                Symbol = symbol,
                Side = side,
                Size = size
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaceMarketOrderAsync hatası: {Symbol}", symbol);
            return new TradeResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<TradeResult> ClosePositionAsync(string symbol, string direction)
    {
        _logger.LogInformation("ClosePositionAsync: {Symbol} {Direction}", symbol, direction);

        try
        {
            var instId = $"{symbol.ToUpper()}-USDT-SWAP";

            var requestBody = new
            {
                instId = instId,
                mgnMode = MarginMode,
                posSide = await ResolveOkxPosSideAsync(direction.ToLower())
            };

            var response = await SendPostRequestAsync<OkxBaseResponse>("/api/v5/trade/close-position", requestBody);

            if (response?.Code != "0")
            {
                _logger.LogError("Pozisyon kapatılamadı: {Code} - {Msg}", response?.Code, response?.Msg);
                return new TradeResult
                {
                    Success = false,
                    ErrorMessage = $"OKX Error: {response?.Code} - {response?.Msg}"
                };
            }

            _logger.LogInformation("Pozisyon kapatıldı: {Symbol} {Direction}", symbol, direction);

            return new TradeResult
            {
                Success = true,
                Symbol = symbol,
                Side = direction == "long" ? "sell" : "buy"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClosePositionAsync hatası: {Symbol}", symbol);
            return new TradeResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<bool> SetLeverageAsync(string symbol, int leverage)
    {
        _logger.LogInformation("SetLeverageAsync: {Symbol} -> {Leverage}x", symbol, leverage);

        try
        {
            var instId = $"{symbol.ToUpper()}-USDT-SWAP";

            if (MarginMode == "cross")
            {
                var requestBody = new
                {
                    instId = instId,
                    lever = leverage.ToString(),
                    mgnMode = "cross"
                };

                var response = await SendPostRequestAsync<OkxBaseResponse>("/api/v5/account/set-leverage", requestBody);

                if (response?.Code != "0")
                {
                    _logger.LogWarning("Cross kaldıraç ayarlanamadı: {Code} - {Msg}", response?.Code, response?.Msg);
                    return false;
                }

                _logger.LogInformation("Cross kaldıraç ayarlandı: {Symbol} -> {Leverage}x", symbol, leverage);
                return true;
            }

            // Isolated hedge mode: long ve short için ayrı ayrı kaldıraç ayarla.
            var requestBodyLong = new
            {
                instId = instId,
                lever = leverage.ToString(),
                mgnMode = "isolated",
                posSide = "long"
            };

            var responseLong = await SendPostRequestAsync<OkxBaseResponse>("/api/v5/account/set-leverage", requestBodyLong);

            if (responseLong?.Code != "0")
            {
                _logger.LogWarning("Long kaldıraç ayarlanamadı: {Code} - {Msg}", responseLong?.Code, responseLong?.Msg);
            }

            var requestBodyShort = new
            {
                instId = instId,
                lever = leverage.ToString(),
                mgnMode = "isolated",
                posSide = "short"
            };

            var responseShort = await SendPostRequestAsync<OkxBaseResponse>("/api/v5/account/set-leverage", requestBodyShort);

            if (responseShort?.Code != "0")
            {
                _logger.LogWarning("Short kaldıraç ayarlanamadı: {Code} - {Msg}", responseShort?.Code, responseShort?.Msg);
            }

            var success = responseLong?.Code == "0" && responseShort?.Code == "0";
            _logger.LogInformation("Isolated kaldıraç ayarı: {Symbol} -> {Leverage}x success={Success}", symbol, leverage, success);
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetLeverageAsync hatası: {Symbol}", symbol);
            return false;
        }
    }

    // ================================================================
    // 🏗️ DEMİR GİBİ MİMARİ - INSTRUMENT & ORDER HESAPLAMA
    // ================================================================

    /// <summary>
    /// Instrument bilgisini al (cache'li)
    /// Her coin için ctVal, minSz, lotSz değerlerini döner
    /// </summary>
    public async Task<InstrumentInfo?> GetInstrumentInfoAsync(string symbol, bool forceRefresh = false)
    {
        var cacheKey = symbol.ToUpper();
        var now = DateTime.UtcNow;

        // Cache kontrol
        lock (_cacheLock)
        {
            if (!forceRefresh && _instrumentCache.TryGetValue(cacheKey, out var cached))
            {
                // Instrument bilgisi 1 saat geçerli
                if (now - cached.InfoUpdatedAt < _instrumentCacheExpiry)
                {
                    // Fiyat 30 saniyeden eski ise güncelle
                    if (now - cached.PriceUpdatedAt > _priceCacheExpiry)
                    {
                        // Fiyat güncelleme async yapılacak, şimdilik mevcut dönsün
                        _ = UpdatePriceAsync(cacheKey);
                    }
                    return cached;
                }
            }
        }

        // Cache'de yok veya expire olmuş, API'den çek
        try
        {
            var instId = $"{cacheKey}-USDT-SWAP";

            // Instrument bilgisi
            var instrumentResponse = await SendGetRequestAsync<OkxInstrumentResponse>(
                $"/api/v5/public/instruments?instType=SWAP&instId={instId}");

            if (instrumentResponse?.Code != "0" || instrumentResponse.Data == null || !instrumentResponse.Data.Any())
            {
                _logger.LogWarning("Instrument bulunamadı: {Symbol}", symbol);
                return null;
            }

            var inst = instrumentResponse.Data[0];

            // Parse değerleri
            decimal.TryParse(inst.CtVal, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var ctVal);
            decimal.TryParse(inst.MinSz, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var minSz);
            decimal.TryParse(inst.LotSz, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var lotSz);
            decimal.TryParse(inst.TickSz, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var tickSz);

            // Güncel fiyat
            var tickerResponse = await SendGetRequestAsync<OkxTickerResponse>(
                $"/api/v5/market/ticker?instId={instId}");

            decimal lastPrice = 0;
            if (tickerResponse?.Code == "0" && tickerResponse.Data?.Any() == true)
            {
                decimal.TryParse(tickerResponse.Data[0].Last, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out lastPrice);
            }

            var info = new InstrumentInfo
            {
                InstId = instId,
                Symbol = cacheKey,
                CtVal = ctVal > 0 ? ctVal : 1,
                MinSz = minSz > 0 ? minSz : 0.01m,
                LotSz = lotSz > 0 ? lotSz : 0.01m,
                TickSz = tickSz > 0 ? tickSz : 0.00001m,
                MaxLeverage = 50, // OKX default
                LastPrice = lastPrice,
                PriceUpdatedAt = now,
                InfoUpdatedAt = now
            };

            // Cache'e ekle
            lock (_cacheLock)
            {
                _instrumentCache[cacheKey] = info;
            }

            _logger.LogInformation(
                "📊 Instrument yüklendi: {Symbol} | ctVal={CtVal}, minSz={MinSz}, lotSz={LotSz}, price=${Price}",
                symbol, info.CtVal, info.MinSz, info.LotSz, info.LastPrice);

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetInstrumentInfoAsync hatası: {Symbol}", symbol);
            return null;
        }
    }

    /// <summary>
    /// Cache'deki instrument için fiyatı güncelle
    /// </summary>
    private async Task UpdatePriceAsync(string symbol)
    {
        try
        {
            var instId = $"{symbol.ToUpper()}-USDT-SWAP";
            var tickerResponse = await SendGetRequestAsync<OkxTickerResponse>(
                $"/api/v5/market/ticker?instId={instId}");

            if (tickerResponse?.Code == "0" && tickerResponse.Data?.Any() == true)
            {
                decimal.TryParse(tickerResponse.Data[0].Last, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lastPrice);

                lock (_cacheLock)
                {
                    if (_instrumentCache.TryGetValue(symbol, out var cached))
                    {
                        cached.LastPrice = lastPrice;
                        cached.PriceUpdatedAt = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fiyat güncelleme hatası: {Symbol}", symbol);
        }
    }

    /// <summary>
    /// 🎯 ANA HESAPLAMA METODU
    /// AI'dan gelen sinyali işlemeden önce tüm hesaplamaları yapar
    /// 
    /// Dönen OrderCalculation ile:
    /// - İşlem yapılabilir mi kontrol edilir
    /// - Gerçek margin/coin miktarı gösterilir
    /// - Uyarılar listelenir
    /// </summary>
    public async Task<OrderCalculation> CalculateOrderAsync(string symbol, decimal requestedMarginUSDT, int leverage, string action)
    {
        var result = new OrderCalculation
        {
            Symbol = symbol.ToUpper(),
            RequestedMarginUSDT = requestedMarginUSDT,
            Leverage = leverage,
            Action = action
        };

        try
        {
            // 1. Instrument bilgisini al
            var instrument = await GetInstrumentInfoAsync(symbol);
            
            if (instrument == null)
            {
                result.IsValid = false;
                result.ValidationStatus = OrderValidationStatus.InstrumentNotFound;
                result.ValidationMessage = $"❌ {symbol} için enstrüman bilgisi bulunamadı!";
                return result;
            }

            result.Instrument = instrument;
            result.CalculationSteps.Add($"1️⃣ Instrument: {instrument.InstId}");
            result.CalculationSteps.Add($"   ctVal={instrument.CtVal}, minSz={instrument.MinSz}, lotSz={instrument.LotSz}");

            // 2. Fiyat kontrolü
            if (instrument.LastPrice <= 0)
            {
                result.IsValid = false;
                result.ValidationStatus = OrderValidationStatus.PriceUnavailable;
                result.ValidationMessage = $"❌ {symbol} için fiyat bilgisi alınamadı!";
                return result;
            }

            result.CalculationSteps.Add($"2️⃣ Fiyat: ${instrument.LastPrice}");

            // 3. Kaldıraç kontrolü
            if (leverage > instrument.MaxLeverage)
            {
                result.IsValid = false;
                result.ValidationStatus = OrderValidationStatus.LeverageTooHigh;
                result.ValidationMessage = $"❌ Kaldıraç çok yüksek! Max: {instrument.MaxLeverage}x, İstenen: {leverage}x";
                return result;
            }

            // 4. Notional hesapla
            result.Notional = requestedMarginUSDT * leverage;
            result.CalculationSteps.Add($"3️⃣ Notional: {requestedMarginUSDT} USDT × {leverage}x = {result.Notional} USD");

            // 5. Ham kontrat sayısı
            result.RawContracts = result.Notional / instrument.OneFullContractUsd;
            result.CalculationSteps.Add($"4️⃣ Ham kontrat: {result.Notional} / {instrument.OneFullContractUsd:F4} = {result.RawContracts:F6}");

            // 6. lotSz'ye yuvarla (AŞAĞI)
            result.Contracts = Math.Floor(result.RawContracts / instrument.LotSz) * instrument.LotSz;
            result.CalculationSteps.Add($"5️⃣ Yuvarlanmış ({instrument.LotSz} katları): {result.Contracts}");

            // 7. Minimum kontrol
            if (result.Contracts < instrument.MinSz)
            {
                var minMarginRequired = instrument.GetMinMarginForLeverage(leverage);
                
                // Tolerans: İstenen margin, minimum'un %50'sinden az ise REDDET
                if (requestedMarginUSDT < minMarginRequired * 0.5m)
                {
                    result.IsValid = false;
                    result.ValidationStatus = OrderValidationStatus.InsufficientMargin;
                    result.ValidationMessage = $"❌ Margin yetersiz! Minimum {minMarginRequired:F4} USDT gerekli ({instrument.MinSz} kontrat için)";
                    result.CalculationSteps.Add($"❌ Minimum kontrat ({instrument.MinSz}) için {minMarginRequired:F4} USDT gerekli");
                    return result;
                }

                // Tolerans içinde - minimum kontrat aç ama uyar
                result.Contracts = instrument.MinSz;
                result.Warnings.Add($"⚠️ Minimum kontrat ({instrument.MinSz}) açılacak. Margin farkı olacak!");
                result.CalculationSteps.Add($"⚠️ Minimum kontrata yuvarlandı: {instrument.MinSz}");
            }

            // 8. Sonuçları hesapla
            result.CoinAmount = result.Contracts * instrument.CtVal;
            result.PositionValueUSD = result.Contracts * instrument.OneFullContractUsd;
            result.ActualMarginUSD = result.PositionValueUSD / leverage;
            result.MarginDifference = result.ActualMarginUSD - requestedMarginUSDT;
            result.MarginDeviationPercent = requestedMarginUSDT > 0 
                ? (result.MarginDifference / requestedMarginUSDT) * 100 
                : 0;

            result.CalculationSteps.Add($"6️⃣ Coin miktarı: {result.Contracts} × {instrument.CtVal} = {result.CoinAmount} {symbol}");
            result.CalculationSteps.Add($"7️⃣ Pozisyon değeri: {result.PositionValueUSD:F4} USD");
            result.CalculationSteps.Add($"8️⃣ Gerçek margin: {result.ActualMarginUSD:F4} USD (Fark: {result.MarginDifference:+0.0000;-0.0000} USD)");

            // 9. Validasyon durumu
            var absDeviation = Math.Abs(result.MarginDeviationPercent);
            
            if (absDeviation <= 10)
            {
                result.IsValid = true;
                result.ValidationStatus = OrderValidationStatus.Valid;
                result.ValidationMessage = $"✅ Geçerli - {result.Contracts} kontrat ({result.CoinAmount} {symbol})";
            }
            else if (absDeviation <= 50)
            {
                result.IsValid = true;
                result.ValidationStatus = OrderValidationStatus.ValidWithWarning;
                result.ValidationMessage = $"⚠️ Geçerli (sapma: {result.MarginDeviationPercent:+0.0;-0.0}%) - {result.Contracts} kontrat";
                result.Warnings.Add($"Margin sapması: {result.MarginDeviationPercent:+0.0;-0.0}%");
            }
            else
            {
                result.IsValid = true;
                result.ValidationStatus = OrderValidationStatus.ValidWithWarning;
                result.ValidationMessage = $"⚠️ Yüksek sapma ({result.MarginDeviationPercent:+0.0;-0.0}%) - {result.Contracts} kontrat";
                result.Warnings.Add($"⚠️ Yüksek margin sapması: {result.MarginDeviationPercent:+0.0;-0.0}%");
            }

            _logger.LogInformation(
                "📊 Order hesaplandı: {Symbol} | {Contracts} kontrat = {Coins} coin | Margin: {Actual:F4} USD (istenen: {Requested} USD)",
                symbol, result.Contracts, result.CoinAmount, result.ActualMarginUSD, requestedMarginUSDT);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CalculateOrderAsync hatası: {Symbol}", symbol);
            result.IsValid = false;
            result.ValidationStatus = OrderValidationStatus.Error;
            result.ValidationMessage = $"❌ Hesaplama hatası: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// USDT miktarını kontrat sayısına çevir (Eski uyumluluk için - yeni sistemle)
    /// </summary>
    private async Task<decimal> ConvertToContractsAsync(string symbol, decimal usdtAmount, int leverage = 1)
    {
        var calculation = await CalculateOrderAsync(symbol, usdtAmount, leverage, "CALCULATE");
        
        if (!calculation.IsValid)
        {
            _logger.LogWarning("Kontrat hesaplanamadı: {Message}", calculation.ValidationMessage);
            return 0;
        }

        return calculation.Contracts;
    }

    /// <summary>
    /// Debug için detaylı bilgi döner (eski uyumluluk)
    /// </summary>
    public async Task<(decimal contracts, decimal ctVal, decimal price, decimal notional, decimal minSz, decimal lotSz)> ConvertToContractsDebugAsync(string symbol, decimal usdtAmount, int leverage = 1)
    {
        var instrument = await GetInstrumentInfoAsync(symbol);
        if (instrument == null)
            return (0, 1, 0, 0, 0.01m, 0.01m);

        var calculation = await CalculateOrderAsync(symbol, usdtAmount, leverage, "DEBUG");
        
        return (
            calculation.Contracts,
            instrument.CtVal,
            instrument.LastPrice,
            calculation.Notional,
            instrument.MinSz,
            instrument.LotSz
        );
    }

    // ================================================================
    // YARDIMCI METODLAR
    // ================================================================

    private static string CreateClientOrderId(string symbol, string side)
    {
        var normalizedSymbol = new string(symbol
            .Where(char.IsLetterOrDigit)
            .Take(6)
            .ToArray())
            .ToUpperInvariant();
        var normalizedSide = string.Equals(side, "buy", StringComparison.OrdinalIgnoreCase) ? "B" : "S";
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"WT{normalizedSymbol}{normalizedSide}{stamp[^8..]}{suffix}";
    }

    private async Task<string> ResolveOkxPosSideAsync(string desiredPosSide)
    {
        try
        {
            var config = await GetAccountConfigurationAsync();
            if (string.Equals(config.PositionMode, "net_mode", StringComparison.OrdinalIgnoreCase))
            {
                return "net";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Account config okunamadı, posSide olduğu gibi kullanılacak.");
        }

        return desiredPosSide.ToLowerInvariant();
    }

    /// <summary>
    /// OKX API imza oluştur
    /// </summary>
    private string SignRequest(string timestamp, string method, string requestPath, string body = "")
    {
        // OKX imza formatı: Base64(HMAC-SHA256(timestamp + method + requestPath + body))
        var message = timestamp + method + requestPath + body;
        var keyBytes = Encoding.UTF8.GetBytes(_settings.SecretKey);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(messageBytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// OKX API header'larını ayarla
    /// </summary>
    private void SetAuthHeaders(string timestamp, string sign)
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("OK-ACCESS-KEY", _settings.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("OK-ACCESS-SIGN", sign);
        _httpClient.DefaultRequestHeaders.Add("OK-ACCESS-TIMESTAMP", timestamp);
        _httpClient.DefaultRequestHeaders.Add("OK-ACCESS-PASSPHRASE", _settings.Passphrase);

        if (_settings.IsDemo)
        {
            _httpClient.DefaultRequestHeaders.Add("x-simulated-trading", "1");
        }
    }

    // ================================================================
    // HTTP İSTEK METODLARI
    // ================================================================

    /// <summary>
    /// Authenticated GET isteği gönder
    /// </summary>
    private async Task<T?> SendGetRequestAsync<T>(string requestPath) where T : class
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var sign = SignRequest(timestamp, "GET", requestPath);
        
        SetAuthHeaders(timestamp, sign);

        _logger.LogDebug("GET {Path}", requestPath);

        var response = await _httpClient.GetAsync(requestPath);
        var content = await response.Content.ReadAsStringAsync();

        _logger.LogDebug("Response: {Content}", content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("HTTP Error: {StatusCode} - {Content}", response.StatusCode, content);
            throw new HttpRequestException($"OKX API Error: {response.StatusCode} - {content}");
        }

        return JsonSerializer.Deserialize<T>(content, JsonOptions);
    }

    /// <summary>
    /// Authenticated POST isteği gönder
    /// </summary>
    private async Task<T?> SendPostRequestAsync<T>(string requestPath, object body) where T : class
    {
        var bodyJson = JsonSerializer.Serialize(body);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var sign = SignRequest(timestamp, "POST", requestPath, bodyJson);
        
        SetAuthHeaders(timestamp, sign);

        _logger.LogDebug("POST {Path}: {Body}", requestPath, bodyJson);

        var httpContent = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(requestPath, httpContent);
        var content = await response.Content.ReadAsStringAsync();

        _logger.LogDebug("Response: {Content}", content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("HTTP Error: {StatusCode} - {Content}", response.StatusCode, content);
            throw new HttpRequestException($"OKX API Error: {response.StatusCode} - {content}");
        }

        return JsonSerializer.Deserialize<T>(content, JsonOptions);
    }
}

// ================================================================
// OKX API DTO'LARI
// ================================================================

#region OKX Response DTOs

public class OkxAccountConfigResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public List<OkxAccountConfigData>? Data { get; set; }
}

public class OkxAccountConfigData
{
    [JsonPropertyName("acctLv")]
    public string? AcctLv { get; set; }

    [JsonPropertyName("posMode")]
    public string? PosMode { get; set; }
}

/// <summary>
/// OKX Balance API Response
/// </summary>
public class OkxBalanceResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public List<OkxBalanceData>? Data { get; set; }
}

public class OkxBalanceData
{
    [JsonPropertyName("totalEq")]
    public string? TotalEq { get; set; }

    [JsonPropertyName("adjEq")]
    public string? AdjEq { get; set; }

    [JsonPropertyName("availEq")]
    public string? AvailEq { get; set; }

    [JsonPropertyName("imr")]
    public string? Imr { get; set; }

    [JsonPropertyName("mmr")]
    public string? Mmr { get; set; }

    [JsonPropertyName("mgnRatio")]
    public string? MgnRatio { get; set; }

    [JsonPropertyName("notionalUsd")]
    public string? NotionalUsd { get; set; }

    [JsonPropertyName("upl")]
    public string? Upl { get; set; }

    [JsonPropertyName("details")]
    public List<OkxBalanceDetail>? Details { get; set; }
}

public class OkxBalanceDetail
{
    [JsonPropertyName("ccy")]
    public string? Ccy { get; set; }

    [JsonPropertyName("eq")]
    public string? Eq { get; set; }

    [JsonPropertyName("cashBal")]
    public string? CashBal { get; set; }

    [JsonPropertyName("availBal")]
    public string? AvailBal { get; set; }

    [JsonPropertyName("frozenBal")]
    public string? FrozenBal { get; set; }

    [JsonPropertyName("eqUsd")]
    public string? EqUsd { get; set; }
}

/// <summary>
/// OKX Positions API Response
/// </summary>
public class OkxPositionsResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public List<OkxPositionData>? Data { get; set; }
}

public class OkxPositionData
{
    [JsonPropertyName("instId")]
    public string? InstId { get; set; }

    [JsonPropertyName("instType")]
    public string? InstType { get; set; }

    [JsonPropertyName("mgnMode")]
    public string? MgnMode { get; set; }

    [JsonPropertyName("posId")]
    public string? PosId { get; set; }

    [JsonPropertyName("posSide")]
    public string? PosSide { get; set; }

    [JsonPropertyName("pos")]
    public string? Pos { get; set; }

    [JsonPropertyName("availPos")]
    public string? AvailPos { get; set; }

    [JsonPropertyName("avgPx")]
    public string? AvgPx { get; set; }

    [JsonPropertyName("markPx")]
    public string? MarkPx { get; set; }

    [JsonPropertyName("upl")]
    public string? Upl { get; set; }

    [JsonPropertyName("uplRatio")]
    public string? UplRatio { get; set; }

    [JsonPropertyName("lever")]
    public string? Lever { get; set; }

    [JsonPropertyName("liqPx")]
    public string? LiqPx { get; set; }

    [JsonPropertyName("margin")]
    public string? Margin { get; set; }

    [JsonPropertyName("notionalUsd")]
    public string? NotionalUsd { get; set; }

    [JsonPropertyName("adl")]
    public string? Adl { get; set; }

    [JsonPropertyName("ccy")]
    public string? Ccy { get; set; }
}

/// <summary>
/// OKX Base Response (genel yanıt)
/// </summary>
public class OkxBaseResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }
}

/// <summary>
/// OKX Order Response
/// </summary>
public class OkxOrderResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public List<OkxOrderData>? Data { get; set; }
}

public class OkxOrderData
{
    [JsonPropertyName("ordId")]
    public string? OrdId { get; set; }

    [JsonPropertyName("clOrdId")]
    public string? ClOrdId { get; set; }

    [JsonPropertyName("sCode")]
    public string? SCode { get; set; }

    [JsonPropertyName("sMsg")]
    public string? SMsg { get; set; }
}

/// <summary>
/// OKX Instrument Response
/// </summary>
public class OkxInstrumentResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public List<OkxInstrumentData>? Data { get; set; }
}

public class OkxInstrumentData
{
    [JsonPropertyName("instId")]
    public string? InstId { get; set; }

    [JsonPropertyName("ctVal")]
    public string? CtVal { get; set; }

    [JsonPropertyName("ctMult")]
    public string? CtMult { get; set; }

    [JsonPropertyName("minSz")]
    public string? MinSz { get; set; }

    [JsonPropertyName("lotSz")]
    public string? LotSz { get; set; }

    [JsonPropertyName("tickSz")]
    public string? TickSz { get; set; }
}

/// <summary>
/// OKX Ticker Response
/// </summary>
public class OkxTickerResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public List<OkxTickerData>? Data { get; set; }
}

public class OkxTickerData
{
    [JsonPropertyName("instId")]
    public string? InstId { get; set; }

    [JsonPropertyName("last")]
    public string? Last { get; set; }

    [JsonPropertyName("askPx")]
    public string? AskPx { get; set; }

    [JsonPropertyName("bidPx")]
    public string? BidPx { get; set; }
}

#endregion
