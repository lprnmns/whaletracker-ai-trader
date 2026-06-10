using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

/// <summary>
/// Test Controller
/// API bağlantılarını test etmek için
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class TestController : ControllerBase
{
    private readonly IOkxService _okxService;
    private readonly IAIService _aiService;
    private readonly ILogger<TestController> _logger;
    private readonly IWebHostEnvironment _env;

    public TestController(
        IOkxService okxService,
        IAIService aiService,
        ILogger<TestController> logger,
        IWebHostEnvironment env)
    {
        _okxService = okxService;
        _aiService = aiService;
        _logger = logger;
        _env = env;
    }

    // ================================================================
    // KAPSAMLI TEST - TÜM METODLARI TEST ET
    // ================================================================

    /// <summary>
    /// 🧪 KAPSAMLI TEST - Tüm OKX metodlarını test et
    /// GET /api/test/comprehensive
    /// </summary>
    [HttpGet("comprehensive")]
    public async Task<IActionResult> ComprehensiveTest()
    {
        var results = new List<object>();
        var startTime = DateTime.Now;

        _logger.LogInformation("🧪 KAPSAMLI TEST BAŞLIYOR...");

        // ================================================================
        // TEST 1: Hesap Bilgisi
        // ================================================================
        try
        {
            _logger.LogInformation("📊 TEST 1: GetAccountInfoAsync");
            var userStats = await _okxService.GetAccountInfoAsync();
            
            results.Add(new
            {
                Test = "1. GetAccountInfoAsync",
                Status = "✅ BAŞARILI",
                Data = new
                {
                    TotalBalanceUSD = userStats.TotalUsd,
                    DefaultLeverage = userStats.Leverage,
                    OpenPositionsCount = userStats.ActivePositions.Count
                }
            });
        }
        catch (Exception ex)
        {
            results.Add(new { Test = "1. GetAccountInfoAsync", Status = "❌ HATA", Error = ex.Message });
        }

        // ================================================================
        // TEST 2: Tüm Pozisyonlar
        // ================================================================
        List<Position> allPositions = new();
        try
        {
            _logger.LogInformation("📊 TEST 2: GetAllPositionsAsync");
            allPositions = await _okxService.GetAllPositionsAsync();
            
            results.Add(new
            {
                Test = "2. GetAllPositionsAsync",
                Status = "✅ BAŞARILI",
                PositionCount = allPositions.Count,
                Positions = allPositions.Select(p => new
                {
                    p.Symbol,
                    p.Direction,
                    MarginUSD = Math.Round(p.MarginUsd, 2),
                    p.EntryPrice,
                    p.Size,
                    PnL = Math.Round(p.UnrealizedPnl, 4)
                })
            });
        }
        catch (Exception ex)
        {
            results.Add(new { Test = "2. GetAllPositionsAsync", Status = "❌ HATA", Error = ex.Message });
        }

        // ================================================================
        // TEST 3: BABY Pozisyonu (Long ve Short ayrı ayrı)
        // ================================================================
        try
        {
            _logger.LogInformation("📊 TEST 3: GetPositionAsync(BABY)");
            var babyPosition = await _okxService.GetPositionAsync("BABY");
            
            // Pozisyonları direction'a göre grupla
            var babyPositions = allPositions.Where(p => p.Symbol == "BABY").ToList();
            
            results.Add(new
            {
                Test = "3. GetPositionAsync(BABY)",
                Status = babyPosition != null ? "✅ BAŞARILI" : "⚠️ POZİSYON YOK",
                FirstPosition = babyPosition != null ? new
                {
                    babyPosition.Symbol,
                    babyPosition.Direction,
                    MarginUSD = Math.Round(babyPosition.MarginUsd, 2),
                    babyPosition.Size,
                    PnL = Math.Round(babyPosition.UnrealizedPnl, 4)
                } : null,
                AllBABYPositions = babyPositions.Select(p => new
                {
                    p.Direction,
                    MarginUSD = Math.Round(p.MarginUsd, 2),
                    p.Size,
                    PnL = Math.Round(p.UnrealizedPnl, 4)
                })
            });
        }
        catch (Exception ex)
        {
            results.Add(new { Test = "3. GetPositionAsync(BABY)", Status = "❌ HATA", Error = ex.Message });
        }

        // ================================================================
        // TEST 4: Kaldıraç Ayarlama (DOGE için test - küçük coin)
        // ================================================================
        try
        {
            _logger.LogInformation("📊 TEST 4: SetLeverageAsync(DOGE, 5)");
            var leverageResult = await _okxService.SetLeverageAsync("DOGE", 5);
            
            results.Add(new
            {
                Test = "4. SetLeverageAsync(DOGE, 5x)",
                Status = leverageResult ? "✅ BAŞARILI" : "⚠️ UYARI",
                Message = leverageResult ? "DOGE kaldıracı 5x olarak ayarlandı" : "Kaldıraç ayarlanamadı"
            });
        }
        catch (Exception ex)
        {
            results.Add(new { Test = "4. SetLeverageAsync", Status = "❌ HATA", Error = ex.Message });
        }

        // ================================================================
        // TEST 5: Pozisyon Özeti (Long vs Short analizi)
        // ================================================================
        try
        {
            _logger.LogInformation("📊 TEST 5: Pozisyon Analizi");
            
            var longPositions = allPositions.Where(p => p.Direction == "Long").ToList();
            var shortPositions = allPositions.Where(p => p.Direction == "Short").ToList();
            
            results.Add(new
            {
                Test = "5. Pozisyon Analizi",
                Status = "✅ BAŞARILI",
                Summary = new
                {
                    TotalPositions = allPositions.Count,
                    LongCount = longPositions.Count,
                    ShortCount = shortPositions.Count,
                    TotalLongMargin = Math.Round(longPositions.Sum(p => p.MarginUsd), 2),
                    TotalShortMargin = Math.Round(shortPositions.Sum(p => p.MarginUsd), 2),
                    TotalLongPnL = Math.Round(longPositions.Sum(p => p.UnrealizedPnl), 4),
                    TotalShortPnL = Math.Round(shortPositions.Sum(p => p.UnrealizedPnl), 4)
                },
                LongPositions = longPositions.Select(p => $"{p.Symbol}: {p.MarginUsd:F2} USD, PnL: {p.UnrealizedPnl:F4}"),
                ShortPositions = shortPositions.Select(p => $"{p.Symbol}: {p.MarginUsd:F2} USD, PnL: {p.UnrealizedPnl:F4}")
            });
        }
        catch (Exception ex)
        {
            results.Add(new { Test = "5. Pozisyon Analizi", Status = "❌ HATA", Error = ex.Message });
        }

        var totalTime = (DateTime.Now - startTime).TotalMilliseconds;

        return Ok(new
        {
            Title = "🐋 WhaleTracker Kapsamlı Test Sonuçları",
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            TotalDurationMs = Math.Round(totalTime, 0),
            TestCount = results.Count,
            Results = results
        });
    }

    // ================================================================
    // DEBUG: KONTRAT HESAPLAMA TESTİ
    // ================================================================

    /// <summary>
    /// 🔧 DEBUG: USDT -> Kontrat dönüşümünü test et
    /// GET /api/test/debug/contracts?symbol=DOGE&usdt=2&leverage=3
    /// </summary>
    [HttpGet("debug/contracts")]
    public async Task<IActionResult> DebugContracts(
        [FromQuery] string symbol = "DOGE",
        [FromQuery] decimal usdt = 2,
        [FromQuery] int leverage = 3)
    {
        _logger.LogInformation("🔧 DEBUG: Kontrat hesaplama - {Symbol} {USDT} USDT {Leverage}x", 
            symbol, usdt, leverage);

        try
        {
            var (contracts, ctVal, price, notional, minSz, lotSz) = await _okxService.ConvertToContractsDebugAsync(symbol, usdt, leverage);
            
            // 1 TAM kontratın USD değeri
            var oneFullContractUsd = ctVal * price;
            // Minimum kontratın USD değeri (0.01 kontrat gibi)
            var minContractUsd = minSz * oneFullContractUsd;
            // Minimum margin gerekli (minSz kontrat için)
            var minMarginRequired = minContractUsd / leverage;
            // Açılacak kontratın USD değeri
            var positionValueUsd = contracts * oneFullContractUsd;
            // Gerçek margin
            var actualMarginUsd = positionValueUsd / leverage;
            // Açılacak coin miktarı
            var coinAmount = contracts * ctVal;
            
            // Durum belirleme
            string status;
            var marginDiff = Math.Abs(actualMarginUsd - usdt);
            if (marginDiff <= usdt * 0.5m)
                status = "✅ UYGUN - Margin doğru hesaplandı";
            else if (usdt >= minMarginRequired / 2)
                status = $"⚠️ UYARI - Minimum {minSz} kontrat açılacak ({Math.Round(actualMarginUsd, 4)} USDT margin)";
            else
                status = $"❌ REDDEDİLECEK - Minimum {Math.Round(minMarginRequired, 4)} USDT margin gerekli";

            return Ok(new
            {
                Title = "🔧 Kontrat Hesaplama Debug (minSz/lotSz ile)",
                Input = new
                {
                    Symbol = symbol,
                    RequestedMarginUSDT = usdt,
                    Leverage = leverage
                },
                ContractInfo = new
                {
                    CtVal = ctVal,
                    CtVal_Aciklama = $"1 tam kontrat = {ctVal} {symbol}",
                    MinSz = minSz,
                    MinSz_Aciklama = $"Minimum emir = {minSz} kontrat = {minSz * ctVal} {symbol}",
                    LotSz = lotSz,
                    LotSz_Aciklama = $"Artış miktarı = {lotSz} kontrat",
                    CurrentPrice = price,
                    OneFullContract_USD = Math.Round(oneFullContractUsd, 2),
                    MinContract_USD = Math.Round(minContractUsd, 4),
                    MinMarginRequired = Math.Round(minMarginRequired, 4)
                },
                Calculation = new
                {
                    Step1 = $"{usdt} USDT * {leverage}x = {notional} USDT notional",
                    Step2 = $"1 tam kontrat = {ctVal} coin * ${price} = ${Math.Round(oneFullContractUsd, 2)}",
                    Step3 = $"{notional} / {Math.Round(oneFullContractUsd, 2)} = {Math.Round(notional / oneFullContractUsd, 6)} (ham)",
                    Step4 = $"lotSz ({lotSz}) ile yuvarla -> {contracts} kontrat"
                },
                Result = new
                {
                    Contracts = contracts,
                    CoinAmount = coinAmount,
                    CoinAmount_Aciklama = $"{contracts} kontrat * {ctVal} = {coinAmount} {symbol}",
                    PositionValueUSD = Math.Round(positionValueUsd, 4),
                    ActualMarginUSD = Math.Round(actualMarginUsd, 4),
                    RequestedMarginUSD = usdt,
                    Difference = Math.Round(actualMarginUsd - usdt, 4)
                },
                Status = status
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Debug hatası!");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    // ================================================================
    // 🏗️ YENİ MİMARİ - ORDER CALCULATION
    // ================================================================

    /// <summary>
    /// 🎯 ORDER CALCULATION - AI sinyali için tam hesaplama
    /// GET /api/test/calculate-order?symbol=ETH&usdt=20&leverage=5&action=long
    /// 
    /// Bu endpoint işlem yapmadan önce tüm hesaplamaları yapar:
    /// - Minimum kontrat kontrolü
    /// - lotSz'ye göre yuvarlama
    /// - Gerçek margin hesabı
    /// - Tüm validasyonlar
    /// </summary>
    [HttpGet("calculate-order")]
    public async Task<IActionResult> CalculateOrder(
        [FromQuery] string symbol = "ETH",
        [FromQuery] decimal usdt = 20,
        [FromQuery] int leverage = 5,
        [FromQuery] string action = "long")
    {
        _logger.LogInformation("🎯 ORDER CALCULATION: {Symbol} {USDT} USDT {Leverage}x {Action}", 
            symbol, usdt, leverage, action);

        try
        {
            var calculation = await _okxService.CalculateOrderAsync(symbol, usdt, leverage, action);

            // Status emoji belirleme
            var statusEmoji = calculation.ValidationStatus switch
            {
                OrderValidationStatus.Valid => "✅",
                OrderValidationStatus.ValidWithWarning => "⚠️",
                OrderValidationStatus.InsufficientMargin => "💰",
                OrderValidationStatus.LeverageTooHigh => "📊",
                OrderValidationStatus.InstrumentNotFound => "🔍",
                OrderValidationStatus.PriceUnavailable => "💵",
                OrderValidationStatus.Error => "❌",
                _ => "❓"
            };

            return Ok(new
            {
                Title = "🏗️ Order Calculation (Demir Mimari)",
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                
                Request = new
                {
                    calculation.Symbol,
                    calculation.RequestedMarginUSDT,
                    calculation.Leverage,
                    calculation.Action
                },
                
                Validation = new
                {
                    IsValid = calculation.IsValid,
                    Status = $"{statusEmoji} {calculation.ValidationStatus}",
                    Message = calculation.ValidationMessage,
                    Warnings = calculation.Warnings
                },
                
                InstrumentInfo = calculation.Instrument != null ? new
                {
                    calculation.Instrument.InstId,
                    calculation.Instrument.CtVal,
                    CtVal_Aciklama = $"1 tam kontrat = {calculation.Instrument.CtVal} {symbol}",
                    calculation.Instrument.MinSz,
                    MinSz_Aciklama = $"Minimum = {calculation.Instrument.MinSz} kontrat = {calculation.Instrument.MinCoinAmount} {symbol}",
                    calculation.Instrument.LotSz,
                    LotSz_Aciklama = $"Artış = {calculation.Instrument.LotSz} kontrat",
                    calculation.Instrument.MaxLeverage,
                    calculation.Instrument.LastPrice,
                    OneFullContractUSD = calculation.Instrument.OneFullContractUsd,
                    MinContractUSD = calculation.Instrument.MinContractUsd,
                    MinMarginForLeverage = calculation.Instrument.GetMinMarginForLeverage(leverage)
                } : null,
                
                Calculation = new
                {
                    calculation.Contracts,
                    calculation.CoinAmount,
                    CoinAmount_Aciklama = $"{calculation.Contracts} kontrat × {calculation.Instrument?.CtVal} = {calculation.CoinAmount} {symbol}",
                    PositionValueUSD = calculation.PositionValueUSD,
                    ActualMarginUSD = calculation.ActualMarginUSD,
                    MarginDifference = calculation.MarginDifference,
                    MarginDifferencePercent = usdt > 0 ? Math.Round((calculation.MarginDifference / usdt) * 100, 2) : 0
                },
                
                Summary = calculation.IsValid 
                    ? $"✅ İŞLEM YAPILABİLİR: {calculation.Contracts} kontrat ({calculation.CoinAmount} {symbol}), margin: {calculation.ActualMarginUSD:F4} USDT"
                    : $"❌ İŞLEM YAPILAMAZ: {calculation.ValidationMessage}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Calculate order hatası!");
            return StatusCode(500, new { Error = ex.Message, Stack = ex.StackTrace });
        }
    }

    /// <summary>
    /// 🔍 INSTRUMENT INFO - Coin bilgisini al
    /// GET /api/test/instrument?symbol=DOGE
    /// </summary>
    [HttpGet("instrument")]
    public async Task<IActionResult> GetInstrumentInfo([FromQuery] string symbol = "DOGE")
    {
        _logger.LogInformation("🔍 Instrument bilgisi: {Symbol}", symbol);

        try
        {
            var info = await _okxService.GetInstrumentInfoAsync(symbol);

            if (info == null)
            {
                return NotFound(new { Error = $"{symbol} için instrument bulunamadı" });
            }

            return Ok(new
            {
                Title = $"🔍 {symbol} Instrument Bilgisi",
                InstId = info.InstId,
                Symbol = info.Symbol,
                
                ContractSpec = new
                {
                    CtVal = info.CtVal,
                    CtVal_Aciklama = $"1 tam kontrat = {info.CtVal} {symbol}",
                    MinSz = info.MinSz,
                    MinSz_Aciklama = $"Minimum emir = {info.MinSz} kontrat = {info.MinCoinAmount} {symbol}",
                    LotSz = info.LotSz,
                    LotSz_Aciklama = $"Lot artışı = {info.LotSz} kontrat",
                    TickSz = info.TickSz,
                    MaxLeverage = info.MaxLeverage
                },
                
                Price = new
                {
                    LastPrice = info.LastPrice,
                    PriceUpdatedAt = info.PriceUpdatedAt,
                    InfoUpdatedAt = info.InfoUpdatedAt
                },
                
                CalculatedValues = new
                {
                    OneFullContractUSD = info.OneFullContractUsd,
                    MinContractUSD = info.MinContractUsd,
                    MinMargin_5x = info.GetMinMarginForLeverage(5),
                    MinMargin_10x = info.GetMinMarginForLeverage(10),
                    MinMargin_20x = info.GetMinMarginForLeverage(20)
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Instrument bilgisi hatası!");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// 🧪 MULTI-COIN TEST - Birden fazla coin için hesaplama
    /// GET /api/test/multi-calculate?usdt=10&leverage=5
    /// </summary>
    [HttpGet("multi-calculate")]
    public async Task<IActionResult> MultiCoinCalculate(
        [FromQuery] decimal usdt = 10,
        [FromQuery] int leverage = 5)
    {
        var symbols = new[] { "BTC", "ETH", "SOL", "DOGE", "XRP", "AVAX", "LINK", "PEPE" };
        var results = new List<object>();

        _logger.LogInformation("🧪 Multi-coin hesaplama: {USDT} USDT, {Leverage}x", usdt, leverage);

        foreach (var symbol in symbols)
        {
            try
            {
                var calc = await _okxService.CalculateOrderAsync(symbol, usdt, leverage, "long");
                
                var statusEmoji = calc.ValidationStatus switch
                {
                    OrderValidationStatus.Valid => "✅",
                    OrderValidationStatus.ValidWithWarning => "⚠️",
                    _ => "❌"
                };

                results.Add(new
                {
                    Symbol = symbol,
                    Status = $"{statusEmoji} {calc.ValidationStatus}",
                    IsValid = calc.IsValid,
                    Contracts = calc.Contracts,
                    CoinAmount = calc.CoinAmount,
                    ActualMarginUSD = Math.Round(calc.ActualMarginUSD, 4),
                    MarginDiff = Math.Round(calc.MarginDifference, 4),
                    Price = calc.Instrument?.LastPrice ?? 0,
                    MinSz = calc.Instrument?.MinSz ?? 0,
                    Warning = calc.Warnings.FirstOrDefault()
                });
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    Symbol = symbol,
                    Status = "❌ HATA",
                    IsValid = false,
                    Error = ex.Message
                });
            }
        }

        return Ok(new
        {
            Title = "🧪 Multi-Coin Hesaplama Testi",
            RequestedMarginUSDT = usdt,
            Leverage = leverage,
            Results = results,
            Summary = new
            {
                Total = results.Count,
                Valid = results.Count(r => ((dynamic)r).IsValid == true),
                Invalid = results.Count(r => ((dynamic)r).IsValid == false)
            }
        });
    }

    // ================================================================
    // LIVE TRADE TESTLERI (DİKKATLİ KULLAN!)
    // ================================================================

    /// <summary>
    /// 🔥 LIVE TEST: Küçük bir LONG pozisyon aç
    /// POST /api/test/live/open-long?symbol=DOGE&usdt=1&leverage=2
    /// </summary>
    [HttpPost("live/open-long")]
    public async Task<IActionResult> LiveTestOpenLong(
        [FromQuery] string symbol = "DOGE",
        [FromQuery] decimal usdt = 1,
        [FromQuery] int leverage = 2)
    {
        _logger.LogWarning("🔥 LIVE TEST: LONG POZİSYON AÇILIYOR - {Symbol} {USDT} USDT {Leverage}x", 
            symbol, usdt, leverage);

        try
        {
            var signal = new TradeSignal
            {
                Symbol = symbol,
                Action = TradeAction.OPEN_LONG,
                MarginAmountUSDT = usdt,
                Leverage = leverage,
                Reason = "Live Test - Open Long"
            };

            var result = await _okxService.ExecuteTradeAsync(signal);

            return Ok(new
            {
                Test = "LIVE: Open Long",
                Signal = new { symbol, usdt, leverage, action = "OPEN_LONG" },
                Result = new
                {
                    result.Success,
                    result.OrderId,
                    result.Symbol,
                    result.Side,
                    result.Size,
                    result.ErrorMessage
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live test hatası!");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// 🔥 LIVE TEST: Küçük bir SHORT pozisyon aç
    /// POST /api/test/live/open-short?symbol=DOGE&usdt=1&leverage=2
    /// </summary>
    [HttpPost("live/open-short")]
    public async Task<IActionResult> LiveTestOpenShort(
        [FromQuery] string symbol = "DOGE",
        [FromQuery] decimal usdt = 1,
        [FromQuery] int leverage = 2)
    {
        _logger.LogWarning("🔥 LIVE TEST: SHORT POZİSYON AÇILIYOR - {Symbol} {USDT} USDT {Leverage}x", 
            symbol, usdt, leverage);

        try
        {
            var signal = new TradeSignal
            {
                Symbol = symbol,
                Action = TradeAction.OPEN_SHORT,
                MarginAmountUSDT = usdt,
                Leverage = leverage,
                Reason = "Live Test - Open Short"
            };

            var result = await _okxService.ExecuteTradeAsync(signal);

            return Ok(new
            {
                Test = "LIVE: Open Short",
                Signal = new { symbol, usdt, leverage, action = "OPEN_SHORT" },
                Result = new
                {
                    result.Success,
                    result.OrderId,
                    result.Symbol,
                    result.Side,
                    result.Size,
                    result.ErrorMessage
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live test hatası!");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// 🔥 LIVE TEST: LONG pozisyonu kapat
    /// POST /api/test/live/close-long?symbol=DOGE&usdt=1
    /// usdt = kapatılacak miktar (dust threshold'a göre tam/kısmi kapanış)
    /// </summary>
    [HttpPost("live/close-long")]
    public async Task<IActionResult> LiveTestCloseLong(
        [FromQuery] string symbol = "DOGE",
        [FromQuery] decimal usdt = 100) // Yüksek değer = tam kapanış
    {
        _logger.LogWarning("🔥 LIVE TEST: LONG POZİSYON KAPATILIYOR - {Symbol} {USDT} USDT", symbol, usdt);

        try
        {
            var signal = new TradeSignal
            {
                Symbol = symbol,
                Action = TradeAction.CLOSE_LONG,
                MarginAmountUSDT = usdt,
                Leverage = 1,
                Reason = "Live Test - Close Long"
            };

            var result = await _okxService.ExecuteTradeAsync(signal);

            return Ok(new
            {
                Test = "LIVE: Close Long",
                Signal = new { symbol, usdt, action = "CLOSE_LONG" },
                Result = new
                {
                    result.Success,
                    result.OrderId,
                    result.Symbol,
                    result.Side,
                    result.ErrorMessage
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live test hatası!");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// 🔥 LIVE TEST: SHORT pozisyonu kapat
    /// POST /api/test/live/close-short?symbol=BABY&usdt=100
    /// </summary>
    [HttpPost("live/close-short")]
    public async Task<IActionResult> LiveTestCloseShort(
        [FromQuery] string symbol = "BABY",
        [FromQuery] decimal usdt = 100)
    {
        _logger.LogWarning("🔥 LIVE TEST: SHORT POZİSYON KAPATILIYOR - {Symbol} {USDT} USDT", symbol, usdt);

        try
        {
            var signal = new TradeSignal
            {
                Symbol = symbol,
                Action = TradeAction.CLOSE_SHORT,
                MarginAmountUSDT = usdt,
                Leverage = 1,
                Reason = "Live Test - Close Short"
            };

            var result = await _okxService.ExecuteTradeAsync(signal);

            return Ok(new
            {
                Test = "LIVE: Close Short",
                Signal = new { symbol, usdt, action = "CLOSE_SHORT" },
                Result = new
                {
                    result.Success,
                    result.OrderId,
                    result.Symbol,
                    result.Side,
                    result.ErrorMessage
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live test hatası!");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// 🔥 LIVE TEST: Market emri direkt gönder
    /// POST /api/test/live/place-order?symbol=DOGE&side=buy&posSide=long&size=10
    /// </summary>
    [HttpPost("live/place-order")]
    public async Task<IActionResult> LiveTestPlaceOrder(
        [FromQuery] string symbol = "DOGE",
        [FromQuery] string side = "buy",
        [FromQuery] string posSide = "long",
        [FromQuery] decimal size = 10,
        [FromQuery] bool reduceOnly = false)
    {
        _logger.LogWarning("🔥 LIVE TEST: MARKET EMRİ - {Symbol} {Side} {PosSide} {Size} kontrat", 
            symbol, side, posSide, size);

        try
        {
            var result = await _okxService.PlaceMarketOrderAsync(symbol, side, posSide, size, reduceOnly);

            return Ok(new
            {
                Test = "LIVE: Place Market Order",
                Order = new { symbol, side, posSide, size, reduceOnly },
                Result = new
                {
                    result.Success,
                    result.OrderId,
                    result.Symbol,
                    result.Side,
                    result.Size,
                    result.ErrorMessage
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live test hatası!");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    // ================================================================
    // MEVCUT TEST METODLARI
    // ================================================================

    /// <summary>
    /// OKX hesap bilgisini test et
    /// GET /api/test/okx-account
    /// </summary>
    [HttpGet("okx-account")]
    public async Task<IActionResult> TestOkxAccount()
    {
        try
        {
            _logger.LogInformation("OKX hesap testi başlatılıyor...");
            
            var userStats = await _okxService.GetAccountInfoAsync();

            return Ok(new
            {
                Success = true,
                Message = "OKX bağlantısı başarılı!",
                Data = new
                {
                    TotalBalanceUSD = userStats.TotalUsd,
                    DefaultLeverage = userStats.Leverage,
                    OpenPositionsCount = userStats.ActivePositions.Count,
                    OpenPositions = userStats.ActivePositions.Select(p => new
                    {
                        p.Symbol,
                        p.Direction,
                        MarginUSD = p.MarginUsd,
                        p.EntryPrice,
                        p.Size,
                        UnrealizedPnL = p.UnrealizedPnl
                    })
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX test hatası!");
            return StatusCode(500, new
            {
                Success = false,
                Error = ex.Message,
                Details = ex.InnerException?.Message
            });
        }
    }

    /// <summary>
    /// Belirli bir coin için pozisyon kontrol et
    /// GET /api/test/okx-position/ETH
    /// </summary>
    [HttpGet("okx-position/{symbol}")]
    public async Task<IActionResult> TestOkxPosition(string symbol)
    {
        try
        {
            _logger.LogInformation("OKX pozisyon testi: {Symbol}", symbol);
            
            var position = await _okxService.GetPositionAsync(symbol);

            if (position == null)
            {
                return Ok(new
                {
                    Success = true,
                    Message = $"{symbol} için açık pozisyon YOK",
                    HasPosition = false
                });
            }

            return Ok(new
            {
                Success = true,
                Message = $"{symbol} pozisyonu bulundu",
                HasPosition = true,
                Position = new
                {
                    position.Symbol,
                    position.Direction,
                    MarginUSD = position.MarginUsd,
                    position.EntryPrice,
                    position.Size,
                    UnrealizedPnL = position.UnrealizedPnl
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX pozisyon test hatası: {Symbol}", symbol);
            return StatusCode(500, new
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Tüm açık pozisyonları listele
    /// GET /api/test/okx-positions
    /// </summary>
    [HttpGet("okx-positions")]
    public async Task<IActionResult> TestOkxAllPositions()
    {
        try
        {
            _logger.LogInformation("Tüm OKX pozisyonları çekiliyor...");
            
            var positions = await _okxService.GetAllPositionsAsync();

            return Ok(new
            {
                Success = true,
                TotalPositions = positions.Count,
                Positions = positions.Select(p => new
                {
                    p.Symbol,
                    p.Direction,
                    MarginUSD = p.MarginUsd,
                    p.EntryPrice,
                    p.Size,
                    UnrealizedPnL = p.UnrealizedPnl
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX pozisyonlar test hatası!");
            return StatusCode(500, new
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    // ================================================================
    // 🎯 FULL LIVE TEST - TÜM İŞLEMLERİ SIRALI YAP
    // ================================================================

    /// <summary>
    /// 🚀 FULL LIVE TEST
    /// Sırayla: DOGE Long → DOGE Short → ETH Long → Pozisyonları Göster → Hepsini Kapat
    /// POST /api/test/live/full-cycle
    /// </summary>
    [HttpPost("live/full-cycle")]
    public async Task<IActionResult> FullLiveCycleTest()
    {
        var testResults = new List<object>();
        var startTime = DateTime.Now;

        _logger.LogInformation("🚀 FULL LIVE CYCLE TEST BAŞLIYOR...");

        // ================================================================
        // AŞAMA 0: Başlangıç Durumu
        // ================================================================
        decimal startBalance = 0;
        try
        {
            _logger.LogInformation("0. Start balance aliniyor...");

            var startAccount = await _okxService.GetAccountInfoAsync();
            startBalance = startAccount.TotalUsd;

            testResults.Add(new
            {
                Step = "0. START BALANCE",
                Status = "OK",
                StartBalance = $"${startBalance:F2}",
                OpenPositions = startAccount.ActivePositions.Count
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Step = "0. START BALANCE", Status = "ERROR", Error = ex.Message });
        }

        // ================================================================
        // AŞAMA 3: ETH LONG AÇ (5 USDT, 5x)
        // ================================================================
        try
        {
            _logger.LogInformation("3️⃣ ETH LONG açılıyor...");
            
            var calculation = await _okxService.CalculateOrderAsync("ETH", 5, 5, "LONG");
            
            await _okxService.SetLeverageAsync("ETH", 5);
            
            var result = await _okxService.PlaceMarketOrderAsync("ETH", "buy", "long", calculation.Contracts);
            
            testResults.Add(new
            {
                Step = "3️⃣ ETH LONG AÇ",
                Status = result.Success ? "✅ BAŞARILI" : "❌ BAŞARISIZ",
                OrderId = result.OrderId,
                Contracts = calculation.Contracts,
                CoinAmount = $"{calculation.CoinAmount:F4} ETH",
                Margin = $"{calculation.ActualMarginUSD:F2} USDT",
                Leverage = "5x",
                Error = result.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Step = "3️⃣ ETH LONG AÇ", Status = "❌ HATA", Error = ex.Message });
        }

        await Task.Delay(1000);

        // ================================================================
        // AŞAMA 4: TÜM POZİSYONLARI GÖSTER
        // ================================================================
        List<Position> allPositions = new();
        try
        {
            _logger.LogInformation("4️⃣ Pozisyonlar listeleniyor...");
            
            allPositions = await _okxService.GetAllPositionsAsync();
            
            testResults.Add(new
            {
                Step = "4️⃣ AÇIK POZİSYONLAR",
                Status = "✅",
                TotalPositions = allPositions.Count,
                Positions = allPositions.Select(p => new
                {
                    p.Symbol,
                    p.Direction,
                    Margin = $"${p.MarginUsd:F2}",
                    Size = p.Size,
                    EntryPrice = $"${p.EntryPrice:F4}",
                    PnL = $"${p.UnrealizedPnl:F4}"
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Step = "4️⃣ POZİSYONLAR", Status = "❌ HATA", Error = ex.Message });
        }

        await Task.Delay(1000);

        // ================================================================
        // AŞAMA 5: DOGE LONG KAPAT
        // ================================================================
        try
        {
            _logger.LogInformation("5️⃣ DOGE LONG kapatılıyor...");
            
            var result = await _okxService.ClosePositionAsync("DOGE", "long");
            
            testResults.Add(new
            {
                Step = "5️⃣ DOGE LONG KAPAT",
                Status = result.Success ? "✅ KAPATILDI" : "❌ BAŞARISIZ",
                Error = result.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Step = "5️⃣ DOGE LONG KAPAT", Status = "❌ HATA", Error = ex.Message });
        }

        await Task.Delay(500);

        // ================================================================
        // AŞAMA 6: DOGE SHORT KAPAT
        // ================================================================
        try
        {
            _logger.LogInformation("6️⃣ DOGE SHORT kapatılıyor...");
            
            var result = await _okxService.ClosePositionAsync("DOGE", "short");
            
            testResults.Add(new
            {
                Step = "6️⃣ DOGE SHORT KAPAT",
                Status = result.Success ? "✅ KAPATILDI" : "❌ BAŞARISIZ",
                Error = result.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Step = "6️⃣ DOGE SHORT KAPAT", Status = "❌ HATA", Error = ex.Message });
        }

        await Task.Delay(500);

        // ================================================================
        // AŞAMA 7: ETH LONG KAPAT
        // ================================================================
        try
        {
            _logger.LogInformation("7️⃣ ETH LONG kapatılıyor...");
            
            var result = await _okxService.ClosePositionAsync("ETH", "long");
            
            testResults.Add(new
            {
                Step = "7️⃣ ETH LONG KAPAT",
                Status = result.Success ? "✅ KAPATILDI" : "❌ BAŞARISIZ",
                Error = result.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Step = "7️⃣ ETH LONG KAPAT", Status = "❌ HATA", Error = ex.Message });
        }

        await Task.Delay(500);

        // ================================================================
        // AŞAMA 8: FİNAL DURUM
        // ================================================================
        decimal endBalance = 0;
        int remainingPositions = 0;
        try
        {
            var finalAccount = await _okxService.GetAccountInfoAsync();
            endBalance = finalAccount.TotalUsd;
            remainingPositions = finalAccount.ActivePositions.Count;
            
            var pnl = endBalance - startBalance;
            
            testResults.Add(new
            {
                Step = "8️⃣ FİNAL DURUM",
                Status = "✅",
                StartBalance = $"${startBalance:F2}",
                EndBalance = $"${endBalance:F2}",
                TotalPnL = $"${pnl:+0.00;-0.00}",
                RemainingPositions = remainingPositions
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Step = "8️⃣ FİNAL DURUM", Status = "❌ HATA", Error = ex.Message });
        }

        var totalTime = (DateTime.Now - startTime).TotalSeconds;

        return Ok(new
        {
            TestName = "🚀 FULL LIVE CYCLE TEST",
            TotalSteps = testResults.Count,
            TotalTimeSeconds = Math.Round(totalTime, 1),
            Results = testResults,
            Summary = new
            {
                StartBalance = $"${startBalance:F2}",
                EndBalance = $"${endBalance:F2}",
                PnL = $"${(endBalance - startBalance):+0.00;-0.00}",
                RemainingPositions = remainingPositions
            }
        });
    }

    // ================================================================
    // 🤖 AI TEST ENDPOINT'LERİ
    // ================================================================

    /// <summary>
    /// 🔌 AI Bağlantı Testi
    /// GET /api/test/ai/connection
    /// </summary>
    [HttpGet("ai/connection")]
    public async Task<IActionResult> TestAIConnection()
    {
        _logger.LogInformation("🔌 AI Bağlantı testi başlatılıyor...");

        try
        {
            var (success, message) = await _aiService.TestConnectionAsync();

            return Ok(new
            {
                Test = "AI Connection Test",
                Status = success ? "✅ BAŞARILI" : "❌ HATA",
                Message = message,
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI bağlantı testi hatası");
            return Ok(new
            {
                Test = "AI Connection Test",
                Status = "❌ HATA",
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// 💬 AI'a Basit Soru Sor
    /// GET /api/test/ai/ask?q=merhaba
    /// </summary>
    [HttpGet("ai/ask")]
    public async Task<IActionResult> AskAI([FromQuery] string q = "Merhaba, kripto piyasası hakkında ne düşünüyorsun?")
    {
        _logger.LogInformation("💬 AI'a soru soruluyor: {Question}", q);

        try
        {
            var startTime = DateTime.Now;
            var response = await _aiService.AskAsync(q);
            var duration = (DateTime.Now - startTime).TotalMilliseconds;

            return Ok(new
            {
                Test = "AI Ask",
                Status = "✅ BAŞARILI",
                Question = q,
                Response = response,
                DurationMs = Math.Round(duration, 0),
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI soru hatası");
            return Ok(new
            {
                Test = "AI Ask",
                Status = "❌ HATA",
                Question = q,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// 🐋 Sahte Balina Hareketi ile AI Karar Testi
    /// POST /api/test/ai/analyze
    /// Simüle edilmiş bir balina hareketi gönderir ve AI'ın kararını döndürür
    /// </summary>
    [HttpPost("ai/analyze")]
    public async Task<IActionResult> TestAIAnalysis([FromBody] AITestRequest? request = null)
    {
        _logger.LogInformation("🐋 AI Analiz testi başlatılıyor...");

        try
        {
            // 1. Mevcut bakiye ve pozisyonları al
            var userStats = await _okxService.GetAccountInfoAsync();
            var positions = await _okxService.GetAllPositionsAsync();

            // 2. Test için sahte balina hareketi oluştur (ya da request'ten al)
            var movement = request?.Movement ?? new WhaleMovement
            {
                Type = "BUY",
                Symbol = "ETH",
                Amount = 0.5m,
                ValueUSDT = 2000m,
                Price = 4000m,
                TxHash = "0x_test_" + Guid.NewGuid().ToString()[..8],
                Timestamp = DateTime.UtcNow,
                WhalePositionAfter = 10.5m
            };

            // 3. AI Context oluştur
            var context = new AIContext
            {
                OurBalanceUSDT = userStats.TotalUsd,
                WhaleBalanceUSDT = request?.WhaleBalance ?? 500000m, // 500K varsayılan
                NewMovement = movement,
                OurPositions = positions.Select(p => new OurPosition
                {
                    Symbol = p.Symbol.Replace("-USDT-SWAP", ""),
                    Direction = p.Direction,
                    MarginUSDT = p.MarginUsd,
                    Leverage = p.Size > 0 && p.MarginUsd > 0 ? (int)Math.Ceiling(p.Size * p.EntryPrice / p.MarginUsd) : 3,
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnL = p.UnrealizedPnl
                }).ToList()
            };

            _logger.LogInformation("📊 AI Context: Balance=${Balance}, Positions={Count}",
                context.OurBalanceUSDT, context.OurPositions.Count);

            // 4. AI'a gönder
            var startTime = DateTime.Now;
            var decision = await _aiService.AnalyzeMovementAsync(context);
            var duration = (DateTime.Now - startTime).TotalMilliseconds;

            return Ok(new
            {
                Test = "AI Analysis",
                Status = decision.ParseSuccess ? "✅ BAŞARILI" : "⚠️ PARSE HATASI",
                Input = new
                {
                    OurBalance = $"${context.OurBalanceUSDT:F2}",
                    WhaleBalance = $"${context.WhaleBalanceUSDT:F2}",
                    Movement = new
                    {
                        movement.Type,
                        movement.Symbol,
                        Value = $"${movement.ValueUSDT:F2}",
                        movement.Amount,
                        Price = $"${movement.Price:F2}"
                    },
                    OurPositionsCount = context.OurPositions.Count
                },
                Decision = new
                {
                    decision.Action,
                    decision.Symbol,
                    AmountUSDT = $"${decision.AmountUSDT:F2}",
                    decision.Leverage,
                    Confidence = $"{decision.ConfidenceScore}%",
                    decision.Reasoning,
                    ShouldTrade = decision.ShouldTrade
                },
                RawResponse = decision.RawResponse,
                ParseInfo = new
                {
                    decision.ParseSuccess,
                    decision.ParseError
                },
                DurationMs = Math.Round(duration, 0),
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Analiz testi hatası");
            return Ok(new
            {
                Test = "AI Analysis",
                Status = "❌ HATA",
                Error = ex.Message,
                StackTrace = ex.StackTrace
            });
        }
    }

    /// <summary>
    /// 🔄 FULL AI → OKX TEST (Simülasyon)
    /// POST /api/test/ai/full-cycle
    /// AI karar verir, ShouldTrade=true ise OKX'e gerçek işlem gönderir
    /// ⚠️ DİKKAT: GERÇEK İŞLEM AÇAR!
    /// </summary>
    [HttpPost("ai/full-cycle")]
    public async Task<IActionResult> AIFullCycleTest([FromBody] AITestRequest? request = null)
    {
        var testResults = new List<object>();
        var startTime = DateTime.Now;

        _logger.LogInformation("🚀 AI FULL CYCLE TEST BAŞLIYOR...");
        _logger.LogWarning("⚠️ DİKKAT: Bu test GERÇEK İŞLEM yapabilir!");

        try
        {
            // Step 1: Mevcut durum
            var userStats = await _okxService.GetAccountInfoAsync();
            var positions = await _okxService.GetAllPositionsAsync();
            var startBalance = userStats.TotalUsd;

            testResults.Add(new
            {
                Step = "1️⃣ BAŞLANGIÇ DURUMU",
                Balance = $"${startBalance:F2}",
                OpenPositions = positions.Count
            });

            // Step 2: Sahte balina hareketi
            var movement = request?.Movement ?? new WhaleMovement
            {
                Type = "BUY",
                Symbol = "DOGE",
                Amount = 1000m,
                ValueUSDT = 300m,
                Price = 0.30m,
                TxHash = "0x_ai_test_" + Guid.NewGuid().ToString()[..8],
                Timestamp = DateTime.UtcNow,
                WhalePositionAfter = 50000m
            };

            testResults.Add(new
            {
                Step = "2️⃣ BALİNA HAREKETİ",
                Type = movement.Type,
                Symbol = movement.Symbol,
                Value = $"${movement.ValueUSDT:F2}",
                TxHash = movement.TxHash
            });

            // Step 3: AI Context
            var context = new AIContext
            {
                OurBalanceUSDT = userStats.TotalUsd,
                WhaleBalanceUSDT = request?.WhaleBalance ?? 500000m,
                NewMovement = movement,
                OurPositions = positions.Select(p => new OurPosition
                {
                    Symbol = p.Symbol.Replace("-USDT-SWAP", ""),
                    Direction = p.Direction,
                    MarginUSDT = p.MarginUsd,
                    Leverage = p.Size > 0 && p.MarginUsd > 0 ? (int)Math.Ceiling(p.Size * p.EntryPrice / p.MarginUsd) : 3,
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnL = p.UnrealizedPnl
                }).ToList()
            };

            // Step 4: AI Karar
            var decision = await _aiService.AnalyzeMovementAsync(context);

            testResults.Add(new
            {
                Step = "3️⃣ AI KARARI",
                Action = decision.Action,
                Symbol = decision.Symbol,
                Amount = $"${decision.AmountUSDT:F2}",
                Leverage = decision.Leverage,
                Confidence = $"{decision.ConfidenceScore}%",
                Reasoning = decision.Reasoning,
                ShouldTrade = decision.ShouldTrade
            });

            // Step 5: İşlem Yap (eğer AI onayladıysa)
            if (decision.ShouldTrade)
            {
                if (decision.Action == "LONG")
                {
                    _logger.LogInformation("📈 LONG pozisyon açılıyor: {Symbol} ${Amount}",
                        decision.Symbol, decision.AmountUSDT);

                    // TradeSignal oluştur ve ExecuteTradeAsync kullan
                    var signal = new TradeSignal
                    {
                        Symbol = decision.Symbol,
                        Action = "OPEN_LONG",
                        Decision = "TRADE",
                        MarginAmountUSDT = decision.AmountUSDT,
                        Leverage = decision.Leverage,
                        Reason = "AI Test"
                    };
                    
                    var tradeResult = await _okxService.ExecuteTradeAsync(signal);

                    testResults.Add(new
                    {
                        Step = "4️⃣ İŞLEM SONUCU",
                        Status = tradeResult.Success ? "✅ BAŞARILI" : "❌ HATA",
                        OrderId = tradeResult.OrderId,
                        Error = tradeResult.ErrorMessage
                    });
                }
                else if (decision.Action == "CLOSE_LONG" || decision.Action == "SHORT")
                {
                    // SHORT = Mevcut LONG pozisyonu kapat
                    _logger.LogInformation("📉 SHORT sinyali: {Symbol} pozisyonu kapatılıyor", decision.Symbol);

                    // Mevcut pozisyonu bul
                    var instId = $"{decision.Symbol}-USDT-SWAP";
                    var existingPosition = positions.FirstOrDefault(p => 
                        p.Symbol == instId && p.Direction == "long");

                    if (existingPosition != null)
                    {
                        var closeResult = await _okxService.ClosePositionAsync(
                            decision.Symbol, "long");

                        testResults.Add(new
                        {
                            Step = "4️⃣ POZİSYON KAPATMA",
                            Status = closeResult.Success ? "✅ BAŞARILI" : "❌ HATA",
                            OrderId = closeResult.OrderId,
                            ClosedPosition = $"{existingPosition.Symbol} ${existingPosition.MarginUsd:F2}",
                            Error = closeResult.ErrorMessage
                        });
                    }
                    else
                    {
                        testResults.Add(new
                        {
                            Step = "4️⃣ POZİSYON KAPATMA",
                            Status = "⚠️ POZİSYON BULUNAMADI",
                            Message = $"{instId} için açık LONG pozisyon yok"
                        });
                    }
                }
                else
                {
                    testResults.Add(new
                    {
                        Step = "4️⃣ İŞLEM",
                        Status = "⏭️ ATLANDIĞ",
                        Reason = $"Action: {decision.Action}"
                    });
                }
            }
            else
            {
                testResults.Add(new
                {
                    Step = "4️⃣ İŞLEM",
                    Status = "⏭️ AI ONAYLAMADI",
                    Reason = decision.Reasoning
                });
            }

            // Step 6: Final durum
            await Task.Delay(1000);
            var finalStats = await _okxService.GetAccountInfoAsync();
            var finalPositions = await _okxService.GetAllPositionsAsync();

            testResults.Add(new
            {
                Step = "5️⃣ FİNAL DURUM",
                StartBalance = $"${startBalance:F2}",
                EndBalance = $"${finalStats.TotalUsd:F2}",
                PnL = $"${(finalStats.TotalUsd - startBalance):+0.00;-0.00}",
                OpenPositions = finalPositions.Count
            });

            var totalTime = (DateTime.Now - startTime).TotalSeconds;

            return Ok(new
            {
                TestName = "🤖 AI FULL CYCLE TEST",
                TotalSteps = testResults.Count,
                TotalTimeSeconds = Math.Round(totalTime, 1),
                Results = testResults,
                AIDecision = new
                {
                    decision.Action,
                    decision.Symbol,
                    AmountUSDT = decision.AmountUSDT,
                    decision.Leverage,
                    decision.ConfidenceScore,
                    decision.Reasoning,
                    decision.ShouldTrade
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Full Cycle test hatası");
            testResults.Add(new
            {
                Step = "❌ HATA",
                Error = ex.Message,
                StackTrace = ex.StackTrace
            });

            return Ok(new
            {
                TestName = "🤖 AI FULL CYCLE TEST",
                Status = "❌ HATA",
                Results = testResults
            });
        }
    }

    // ================================================================
    // 🐋 WHALE LIVE CYCLE TEST - TAM SENARYO
    // ================================================================

    /// <summary>
    /// 🐋 WHALE LIVE CYCLE TEST
    /// Mock whale verisi ile gerçek AI + OKX işlem testi
    /// 
    /// SENARYO:
    /// 1. Başlangıç durumu kontrol (pozisyon yok)
    /// 2. Whale ETH alıyor ($400) → AI analiz → OKX LONG aç
    /// 3. Whale yarısını satıyor ($200) → AI analiz → OKX kısmi kapat
    /// 4. Whale kalanı satıyor ($200) → AI analiz → OKX tam kapat
    /// 
    /// GET /api/test/whale-live-cycle
    /// </summary>
    [HttpGet("whale-live-cycle")]
    public async Task<IActionResult> WhaleLiveCycleTest()
    {
        var testResults = new List<object>();
        var startTime = DateTime.UtcNow;

        const string symbol = "ETH";
        const decimal whaleTotalBalance = 100_000m;
        const decimal whaleBuyUsd = 4000m;

        _logger.LogWarning("LIVE WHALE CYCLE TEST starting. This will place REAL orders.");

        InstrumentInfo? instrument = null;
        try
        {
            instrument = await _okxService.GetInstrumentInfoAsync(symbol);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instrument lookup failed for {Symbol}", symbol);
        }

        var referencePrice = instrument?.LastPrice ?? 3000m;
        if (referencePrice <= 0)
        {
            referencePrice = 3000m;
        }

                async Task<object> ProcessStageAsync(string stageName, string movementType, decimal valueUsdt, decimal amount)
        {
            var userStats = await _okxService.GetAccountInfoAsync();
            var positions = userStats.ActivePositions;

            var movement = new WhaleMovement
            {
                Type = movementType,
                Symbol = symbol,
                Amount = amount,
                ValueUSDT = valueUsdt,
                Price = referencePrice,
                TxHash = $"0x_mock_{Guid.NewGuid().ToString()[..8]}",
                Timestamp = DateTime.UtcNow
            };

            var context = new AIContext
            {
                OurBalanceUSDT = userStats.TotalUsd,
                WhaleBalanceUSDT = whaleTotalBalance,
                NewMovement = movement,
                OurPositions = positions.Select(p => new OurPosition
                {
                    Symbol = p.Symbol,
                    Direction = p.Direction,
                    MarginUSDT = p.MarginUsd,
                    Leverage = p.MarginUsd > 0 && p.EntryPrice > 0
                        ? (int)Math.Ceiling((p.Size * p.EntryPrice) / p.MarginUsd)
                        : 3,
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnL = p.UnrealizedPnl
                }).ToList()
            };

            _logger.LogInformation(
                "Stage {Stage} - AI context: OurBalance={Balance:F2} WhaleBalance={WhaleBalance:F2} Positions={Positions} Movement={Type} {Symbol} ${Value:F2}",
                stageName,
                context.OurBalanceUSDT,
                context.WhaleBalanceUSDT,
                context.OurPositions.Count,
                movement.Type,
                movement.Symbol,
                movement.ValueUSDT);

            var decision = await _aiService.AnalyzeMovementAsync(context);
            _logger.LogInformation(
                "Stage {Stage} - AI decision: Action={Action} Symbol={Symbol} AmountUSDT={Amount:F4} ShouldTrade={ShouldTrade}",
                stageName,
                decision.Action,
                decision.Symbol,
                decision.AmountUSDT,
                decision.ShouldTrade);

            TradeSignal? signal = null;
            TradeResult? tradeResult = null;
            string? skipReason = null;

            if (decision.ShouldTrade)
            {
                if (decision.Action.Equals("CLOSE_LONG", StringComparison.OrdinalIgnoreCase))
                {
                    var hasLong = positions.Any(p =>
                        p.Symbol.Equals(decision.Symbol, StringComparison.OrdinalIgnoreCase) &&
                        p.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase));

                    if (!hasLong)
                    {
                        skipReason = $"LONG pozisyon yok: {decision.Symbol}";
                    }
                }

                if (skipReason == null && !await _okxService.IsSymbolSupportedAsync(decision.Symbol))
                {
                    skipReason = $"OKX futures desteklemiyor: {decision.Symbol}";
                }

                if (skipReason == null)
                {
                    var mappedAction = decision.Action.ToUpperInvariant() switch
                    {
                        "LONG" => TradeAction.OPEN_LONG,
                        "CLOSE_LONG" => TradeAction.CLOSE_LONG,
                        _ => TradeAction.IGNORE
                    };

                    if (mappedAction == TradeAction.IGNORE)
                    {
                        skipReason = $"Action ignore: {decision.Action}";
                    }
                    else
                    {
                        signal = new TradeSignal
                        {
                            Decision = "TRADE",
                            Reason = decision.Reasoning,
                            Symbol = decision.Symbol,
                            Action = mappedAction,
                            Leverage = decision.Leverage,
                            MarginAmountUSDT = decision.AmountUSDT,
                            TradeConfidence = decision.ConfidenceScore,
                            SourceTxHash = movement.TxHash
                        };

                        tradeResult = await _okxService.ExecuteTradeAsync(signal);
                    }
                }
            }
            else
            {
                skipReason = decision.Reasoning;
            }

            return new
            {
                Stage = stageName,
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Context = new
                {
                    context.OurBalanceUSDT,
                    context.WhaleBalanceUSDT,
                    Positions = context.OurPositions.Select(p => new
                    {
                        p.Symbol,
                        p.Direction,
                        p.MarginUSDT,
                        p.EntryPrice,
                        p.UnrealizedPnL
                    }),
                    Movement = new
                    {
                        movement.Type,
                        movement.Symbol,
                        movement.Amount,
                        movement.ValueUSDT,
                        movement.Price,
                        movement.TxHash
                    }
                },
                AIDecision = new
                {
                    decision.Action,
                    decision.Symbol,
                    decision.AmountUSDT,
                    decision.Leverage,
                    decision.ConfidenceScore,
                    decision.Reasoning,
                    decision.ShouldTrade,
                    decision.ParseSuccess,
                    decision.ParseError,
                    decision.RawResponse
                },
                Signal = signal == null
                    ? null
                    : new
                    {
                        signal.Action,
                        signal.Symbol,
                        signal.MarginAmountUSDT,
                        signal.Leverage
                    },
                OkxResult = tradeResult == null
                    ? null
                    : new
                    {
                        tradeResult.Success,
                        tradeResult.OrderId,
                        tradeResult.Symbol,
                        tradeResult.Side,
                        tradeResult.Size,
                        tradeResult.ErrorMessage
                    },
                SkipReason = skipReason
            };
        }
        try
        {
            var accountInfo = await _okxService.GetAccountInfoAsync();
            testResults.Add(new
            {
                Stage = "0 - START",
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                BalanceUSD = Math.Round(accountInfo.TotalUsd, 2),
                OpenPositions = accountInfo.ActivePositions.Count,
                WhaleBalanceUSD = whaleTotalBalance,
                PlannedBuyUSD = whaleBuyUsd,
                ReferencePrice = referencePrice
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Stage = "0 - START", Error = ex.Message });
        }

        var whaleBuyAmount = referencePrice > 0 ? whaleBuyUsd / referencePrice : 0m;
        var whaleSellHalfUsd = whaleBuyUsd / 2m;
        var whaleSellHalfAmount = referencePrice > 0 ? whaleSellHalfUsd / referencePrice : 0m;

        testResults.Add(await ProcessStageAsync("1 - WHALE BUY", "BUY", whaleBuyUsd, whaleBuyAmount));
        await Task.Delay(1000);

        testResults.Add(await ProcessStageAsync("2 - WHALE SELL HALF", "SELL", whaleSellHalfUsd, whaleSellHalfAmount));
        await Task.Delay(1000);

        testResults.Add(await ProcessStageAsync("3 - WHALE SELL REST", "SELL", whaleSellHalfUsd, whaleSellHalfAmount));
        await Task.Delay(1000);
        try
        {
            var finalAccount = await _okxService.GetAccountInfoAsync();
            testResults.Add(new
            {
                Stage = "4 - FINAL",
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                BalanceUSD = Math.Round(finalAccount.TotalUsd, 2),
                OpenPositions = finalAccount.ActivePositions.Count
            });
        }
        catch (Exception ex)
        {
            testResults.Add(new { Stage = "4 - FINAL", Error = ex.Message });
        }

        var totalTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

        return Ok(new
        {
            Title = "WHALE LIVE CYCLE TEST",
            Status = "COMPLETED",
            TotalDurationMs = Math.Round(totalTimeMs, 0),
            Results = testResults
        });
    }
    // ================================================================
    // WHALE HISTORY REPLAY - MEGA TEST (LIVE)
    // ================================================================

    /// <summary>
    /// Raw whale ge�mi�ini tek tek AI'a g�nderip OKX'te canl� replay eder.
    ///
    /// GET /api/test/whale-history-replay
    /// Varsay�lan dosya: data/whale_history_raw.txt
    /// </summary>
    [HttpGet("whale-history-replay")]
    public async Task<IActionResult> WhaleHistoryReplay(
        [FromQuery] string file = "data/whale_history_raw.txt",
        [FromQuery] decimal whaleBalanceUSDT = 100000m,
        [FromQuery] int delayMs = 500)
    {
        var results = new List<object>();
        var startTime = DateTime.UtcNow;

        _logger.LogWarning("MEGA TEST ba�l�yor. TAMAMEN LIVE emir g�nderilecek!");

        var resolvedPath = ResolveHistoryFilePath(file);

        if (!System.IO.File.Exists(resolvedPath))
        {
            return Ok(new
            {
                Title = "WHALE HISTORY REPLAY",
                Status = "ERROR",
                Error = $"Dosya bulunamad�: {resolvedPath}"
            });
        }

        var rawText = System.IO.File.ReadAllText(resolvedPath);
        var entries = ExtractEventEntries(rawText);
        var orderedEntries = entries
            .OrderBy(e => e.Timestamp ?? DateTime.MaxValue)
            .ThenBy(e => e.Index)
            .ToList();

        results.Add(new
        {
            Stage = "0 - LOADED",
            Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            File = resolvedPath,
            TotalBlocks = orderedEntries.Count,
            Order = "oldest_to_newest"
        });

        var index = 0;

        foreach (var entry in orderedEntries)
        {
            index++;

            var userStats = await _okxService.GetAccountInfoAsync();
            var positions = userStats.ActivePositions;

            var movement = new WhaleMovement
            {
                Type = "RAW",
                RawText = entry.RawText,
                TxHash = $"0x_raw_{Guid.NewGuid():N}"[..12],
                Timestamp = DateTime.UtcNow
            };

            var context = new AIContext
            {
                OurBalanceUSDT = userStats.TotalUsd,
                WhaleBalanceUSDT = whaleBalanceUSDT,
                NewMovement = movement,
                OurPositions = positions.Select(p => new OurPosition
                {
                    Symbol = p.Symbol,
                    Direction = p.Direction,
                    MarginUSDT = p.MarginUsd,
                    Leverage = p.MarginUsd > 0 && p.EntryPrice > 0
                        ? (int)Math.Ceiling((p.Size * p.EntryPrice) / p.MarginUsd)
                        : 3,
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnL = p.UnrealizedPnl
                }).ToList()
            };

            _logger.LogInformation("Replay {Index}/{Total} - RAW EVENT:\n{Block}", index, orderedEntries.Count, entry.RawText);

            var decision = await _aiService.AnalyzeMovementAsync(context);

            _logger.LogInformation(
                "Replay {Index} - AI: Action={Action} Symbol={Symbol} AmountUSDT={Amount:F4} ShouldTrade={ShouldTrade}",
                index,
                decision.Action,
                decision.Symbol,
                decision.AmountUSDT,
                decision.ShouldTrade);
            TradeSignal? signal = null;
            TradeResult? tradeResult = null;
            string? skipReason = null;

            if (decision.ShouldTrade)
            {
                if (decision.Action.Equals("CLOSE_LONG", StringComparison.OrdinalIgnoreCase))
                {
                    var hasLong = positions.Any(p =>
                        p.Symbol.Equals(decision.Symbol, StringComparison.OrdinalIgnoreCase) &&
                        p.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase));

                    if (!hasLong)
                    {
                        skipReason = $"LONG pozisyon yok: {decision.Symbol}";
                    }
                }

                if (skipReason == null && !await _okxService.IsSymbolSupportedAsync(decision.Symbol))
                {
                    skipReason = $"OKX futures desteklemiyor: {decision.Symbol}";
                }

                if (skipReason == null)
                {
                    var mappedAction = decision.Action.ToUpperInvariant() switch
                    {
                        "LONG" => TradeAction.OPEN_LONG,
                        "CLOSE_LONG" => TradeAction.CLOSE_LONG,
                        _ => TradeAction.IGNORE
                    };

                    if (mappedAction == TradeAction.IGNORE)
                    {
                        skipReason = $"Action ignore: {decision.Action}";
                    }
                    else
                    {
                        signal = new TradeSignal
                        {
                            Decision = "TRADE",
                            Reason = decision.Reasoning,
                            Symbol = decision.Symbol,
                            Action = mappedAction,
                            Leverage = decision.Leverage,
                            MarginAmountUSDT = decision.AmountUSDT,
                            TradeConfidence = decision.ConfidenceScore,
                            SourceTxHash = movement.TxHash
                        };

                        tradeResult = await _okxService.ExecuteTradeAsync(signal);
                    }
                }
            }
            else
            {
                skipReason = decision.Reasoning;
            }

            results.Add(new
            {
                Index = index,
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                RawEvent = entry.RawText,
                AIDecision = new
                {
                    decision.Action,
                    decision.Symbol,
                    decision.AmountUSDT,
                    decision.Leverage,
                    decision.ConfidenceScore,
                    decision.Reasoning,
                    decision.ShouldTrade,
                    decision.ParseSuccess,
                    decision.ParseError,
                    decision.RawResponse
                },
                Signal = signal == null
                    ? null
                    : new
                    {
                        signal.Action,
                        signal.Symbol,
                        signal.MarginAmountUSDT,
                        signal.Leverage
                    },
                OkxResult = tradeResult == null
                    ? null
                    : new
                    {
                        tradeResult.Success,
                        tradeResult.OrderId,
                        tradeResult.Symbol,
                        tradeResult.Side,
                        tradeResult.Size,
                        tradeResult.ErrorMessage
                    },
                SkipReason = skipReason
            });

            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }
        }

        var totalTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

        return Ok(new
        {
            Title = "WHALE HISTORY REPLAY",
            Status = "COMPLETED",
            TotalBlocks = orderedEntries.Count,
            TotalDurationMs = Math.Round(totalTimeMs, 0),
            Results = results
        });
    }

    private sealed class WhaleEventEntry
    {
        public WhaleEventEntry(int index, string rawText, DateTime? timestamp)
        {
            Index = index;
            RawText = rawText;
            Timestamp = timestamp;
        }

        public int Index { get; }
        public string RawText { get; }
        public DateTime? Timestamp { get; }
    }

    private static List<WhaleEventEntry> ExtractEventEntries(string rawText)
    {
        var entries = new List<WhaleEventEntry>();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return entries;
        }

        rawText = rawText.TrimStart('\ufeff');

        var blocks = Regex.Split(rawText, @"\r?\n\r?\n")
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        string? currentDate = null;
        var index = 0;

        foreach (var block in blocks)
        {
            if (Regex.IsMatch(block, @"^[A-Za-z]+\s+\d{1,2},\s+\d{4}$"))
            {
                currentDate = block;
                continue;
            }

            if (!Regex.IsMatch(block, @"\b(Trade|Deposit|Receive|Send|Approve|Execute|Mint)\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            index++;
            var raw = currentDate != null
                ? $"{currentDate}\n\n{block}"
                : block;

            var timestamp = TryParseEventTimestamp(currentDate, block);
            entries.Add(new WhaleEventEntry(index, raw, timestamp));
        }

        return entries;
    }

    private static DateTime? TryParseEventTimestamp(string? dateLine, string block)
    {
        if (string.IsNullOrWhiteSpace(dateLine))
        {
            return null;
        }

        var match = Regex.Match(block, @"\b\d{1,2}:\d{2}\s+[AP]M\b");
        if (!match.Success)
        {
            return null;
        }

        var combined = $"{dateLine} {match.Value}";

        if (DateTime.TryParseExact(combined, "MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        if (DateTime.TryParseExact(combined, "MMMM dd, yyyy h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return parsed;
        }

        return null;
    }

private string ResolveHistoryFilePath(string file)
    {
        if (Path.IsPathRooted(file))
        {
            return file;
        }

        var candidates = new[]
        {
            Path.Combine(_env.ContentRootPath, file),
            Path.Combine(_env.ContentRootPath, "..", "..", file),
            Path.Combine(Directory.GetCurrentDirectory(), file)
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (System.IO.File.Exists(full))
            {
                return full;
            }
        }

        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, file));
    }
}
public class AITestRequest
{
    public WhaleMovement? Movement { get; set; }
    public decimal? WhaleBalance { get; set; }
}












