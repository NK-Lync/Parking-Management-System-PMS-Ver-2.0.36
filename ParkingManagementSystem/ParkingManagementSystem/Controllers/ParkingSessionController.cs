using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ParkingManagementSystem.Data;
using ParkingManagementSystem.Models;
using ParkingManagementSystem.ViewModels;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ParkingManagementSystem.Controllers
{
    public class ParkingSessionController : Controller
    {
        private readonly ParkingDbContext _context;
        private readonly IConfiguration _configuration;

        public ParkingSessionController(ParkingDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        private static string NormalizePlate(string? plate)
        {
            if (string.IsNullOrWhiteSpace(plate)) return string.Empty;
            return plate.Replace(".", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToUpperInvariant();
        }

        private static string NormalizeZone(string? zone)
        {
            if (string.IsNullOrWhiteSpace(zone)) return string.Empty;
            return zone.Trim().ToUpperInvariant();
        }

        private static string StripDiacritics(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var normalized = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }

        private async Task<List<ZoneAvailabilityViewModel>> GetZoneAvailabilityAsync()
        {
            return await _context.ParkingPositions
                .Where(p => !p.IsOccupied)
                .GroupBy(p => p.Zone)
                .Select(g => new ZoneAvailabilityViewModel
                {
                    Zone = g.Key,
                    AvailableCount = g.Count()
                })
                .OrderBy(z => z.Zone)
                .ToListAsync();
        }

        private async Task<VehicleType?> ResolveVehicleTypeAsync(int vehicleTypeId, string? detectedType, string normalizedPlate)
        {
            var selected = await _context.VehicleTypes.FirstOrDefaultAsync(v => v.TypeId == vehicleTypeId);
            if (selected != null) return selected;

            var allTypes = await _context.VehicleTypes.ToListAsync();
            if (allTypes.Count == 0) return null;

            var isMotorbike = detectedType?.Contains("motorcycle", StringComparison.OrdinalIgnoreCase) == true
                || detectedType?.Contains("scooter", StringComparison.OrdinalIgnoreCase) == true
                || Regex.IsMatch(normalizedPlate, @"^\d{2}[A-Z][1-9]");

            if (isMotorbike)
            {
                var motorType = allTypes.FirstOrDefault(t => StripDiacritics(t.TypeName).Contains("xe may"));
                if (motorType != null) return motorType;
            }
            else
            {
                var carType = allTypes.FirstOrDefault(t => StripDiacritics(t.TypeName).Contains("o to"));
                if (carType != null) return carType;
            }

            return allTypes.FirstOrDefault();
        }

        private async Task<ParkingSession?> FindLatestActiveSessionByPlateAsync(string normalizedPlate)
        {
            if (string.IsNullOrWhiteSpace(normalizedPlate)) return null;

            var activeSessions = await _context.ParkingSessions
                .Include(s => s.RFIDCard)
                .Include(s => s.Vehicle).ThenInclude(v => v.VehicleType)
                .Include(s => s.Vehicle).ThenInclude(v => v.MonthlyCards)
                .Include(s => s.ParkingPosition)
                .Where(s => s.CheckOutTime == null)
                .OrderByDescending(s => s.CheckInTime)
                .ToListAsync();

            return activeSessions.FirstOrDefault(s => NormalizePlate(s.LicensePlateIn) == normalizedPlate);
        }

        private async Task<FeeCalculationResult> CalculateFeeAsync(ParkingSession session, DateTime atTime)
        {
            if (!session.CheckInTime.HasValue)
            {
                return new FeeCalculationResult
                {
                    TotalFee = 0,
                    Duration = TimeSpan.Zero,
                    FreeMinutes = 3,
                    BasePrice = 0,
                    HourlyRate = 5000
                };
            }

            var checkIn = session.CheckInTime.Value;
            if (atTime < checkIn) atTime = checkIn;

            var duration = atTime - checkIn;
            var fallbackRate = session.Vehicle?.VehicleType?.PricePerHour ?? 5000;

            Price? priceConfig = null;
            if (session.Vehicle?.TypeId > 0)
            {
                priceConfig = await _context.Prices
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TypeId == session.Vehicle.TypeId);
            }

            var freeMinutes = priceConfig?.FreeMinutes is > 0 ? priceConfig.FreeMinutes : 3;
            var basePrice = priceConfig?.BasePrice ?? 0;
            var hourlyRate = priceConfig?.PricePerHour ?? fallbackRate;

            decimal fee = 0;
            if (duration.TotalMinutes > freeMinutes)
            {
                var billableMinutes = Math.Max(0, duration.TotalMinutes - freeMinutes);
                var billableHours = (decimal)Math.Ceiling(billableMinutes / 60d);
                fee = basePrice + (billableHours * hourlyRate);
            }

            return new FeeCalculationResult
            {
                TotalFee = fee,
                Duration = duration,
                FreeMinutes = freeMinutes,
                BasePrice = basePrice,
                HourlyRate = hourlyRate
            };
        }

        public async Task<IActionResult> Index(string? keyword, string? status, string? zone)
        {
            var query = _context.ParkingSessions
                .Include(p => p.RFIDCard)
                .Include(p => p.Vehicle).ThenInclude(v => v.VehicleType)
                .Include(p => p.ParkingPosition)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var keywordUpper = keyword.Trim().ToUpper();
                query = query.Where(s =>
                    (s.LicensePlateIn != null && s.LicensePlateIn.ToUpper().Contains(keywordUpper)) ||
                    (s.RFIDCard != null && s.RFIDCard.RfidCode.ToUpper().Contains(keywordUpper)) ||
                    (s.Vehicle != null && s.Vehicle.VehicleType != null && s.Vehicle.VehicleType.TypeName.ToUpper().Contains(keywordUpper)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (status.Equals("active", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(s => s.CheckOutTime == null);
                }
                else if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(s => s.CheckOutTime != null);
                }
            }

            if (!string.IsNullOrWhiteSpace(zone))
            {
                query = query.Where(s => s.ParkingPosition != null && s.ParkingPosition.Zone == zone);
            }

            var sessions = await query
                .OrderByDescending(p => p.CheckInTime)
                .ToListAsync();

            ViewBag.Keyword = keyword ?? string.Empty;
            ViewBag.SelectedStatus = status ?? string.Empty;
            ViewBag.SelectedZone = zone ?? string.Empty;
            ViewBag.ZoneList = await _context.ParkingPositions
                .Select(p => p.Zone)
                .Where(z => !string.IsNullOrWhiteSpace(z))
                .Distinct()
                .OrderBy(z => z)
                .ToListAsync();

            var overlapMap = sessions
                .Where(s => s.CheckOutTime == null)
                .GroupBy(s => NormalizePlate(s.LicensePlateIn))
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                .ToDictionary(g => g.Key, g => g.Count());

            ViewBag.OverlapMap = overlapMap;
            return View(sessions);
        }

        public async Task<IActionResult> Monitor()
        {
            ViewBag.AvailablePositions = await _context.ParkingPositions.Where(p => !p.IsOccupied).ToListAsync();
            ViewBag.VehicleTypes = await _context.VehicleTypes.ToListAsync();
            ViewBag.AvailableCount = await _context.ParkingPositions.CountAsync(p => !p.IsOccupied);
            ViewBag.ZoneOptions = await GetZoneAvailabilityAsync();
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> SuggestCheckInPosition(int? vehicleTypeId)
        {
            _ = vehicleTypeId;

            var zoneSummary = await _context.ParkingPositions
                .AsNoTracking()
                .Where(p => !p.IsOccupied)
                .GroupBy(p => p.Zone)
                .Select(g => new
                {
                    Zone = g.Key,
                    AvailableCount = g.Count(),
                    MinPosition = g.Min(x => x.PositionId)
                })
                .OrderByDescending(g => g.AvailableCount)
                .ThenBy(g => g.Zone)
                .ToListAsync();

            var bestZone = zoneSummary.FirstOrDefault();
            if (bestZone == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Bai xe da het cho trong."
                });
            }

            var suggestedPosition = await _context.ParkingPositions
                .AsNoTracking()
                .Where(p => !p.IsOccupied && p.Zone == bestZone.Zone)
                .OrderBy(p => p.PositionId)
                .Select(p => new { p.PositionId, p.Zone })
                .FirstOrDefaultAsync();

            if (suggestedPosition == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Khong tim thay vi tri phu hop."
                });
            }

            return Json(new
            {
                success = true,
                zone = suggestedPosition.Zone,
                positionId = suggestedPosition.PositionId,
                availableInZone = bestZone.AvailableCount,
                message = $"Moi xe di chuyen den {suggestedPosition.Zone}, vi tri {suggestedPosition.PositionId}."
            });
        }

        [HttpPost]
        public async Task<IActionResult> ProcessCheckIn(string licensePlate, string rfidCode, string capturedImageBase64, string detectedType, int vehicleTypeId, string? preferredZone, int? suggestedPositionId)
        {
            if (string.IsNullOrWhiteSpace(licensePlate))
            {
                TempData["Error"] = "Khong nhan dien duoc bien so.";
                return RedirectToAction("Monitor");
            }

            licensePlate = licensePlate.Trim().ToUpperInvariant();
            string cleanInputPlate = NormalizePlate(licensePlate);

            var activeMonthlyCards = await _context.MonthlyCards
                .Include(m => m.Vehicle)
                .Include(m => m.RFIDCard)
                .Where(m => m.ExpiryDate >= DateTime.Now)
                .AsNoTracking()
                .ToListAsync();

            var matchedCard = activeMonthlyCards.FirstOrDefault(m => NormalizePlate(m.Vehicle?.LicensePlate) == cleanInputPlate);

            Vehicle? vehicle;
            RFIDCard? card;

            if (matchedCard != null)
            {
                vehicle = matchedCard.Vehicle;
                card = matchedCard.RFIDCard;
                rfidCode = card?.RfidCode ?? rfidCode;
                licensePlate = vehicle?.LicensePlate ?? licensePlate;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rfidCode))
                {
                    rfidCode = "RFID" + DateTime.Now.Ticks.ToString()[^6..];
                }

                var vType = await ResolveVehicleTypeAsync(vehicleTypeId, detectedType, cleanInputPlate);
                if (vType == null)
                {
                    TempData["Error"] = "He thong chua co loai xe nao.";
                    return RedirectToAction("Monitor");
                }

                vehicle = await _context.Vehicles.FirstOrDefaultAsync(v => v.LicensePlate == licensePlate);
                if (vehicle == null)
                {
                    vehicle = new Vehicle
                    {
                        LicensePlate = licensePlate,
                        TypeId = vType.TypeId,
                        Description = "Auto"
                    };
                    _context.Vehicles.Add(vehicle);
                }
                else
                {
                    vehicle.TypeId = vType.TypeId;
                    _context.Vehicles.Update(vehicle);
                }

                card = await _context.RFIDCards.FirstOrDefaultAsync(c => c.RfidCode == rfidCode);
                if (card == null)
                {
                    card = new RFIDCard
                    {
                        RfidCode = rfidCode,
                        UID = rfidCode,
                        Status = "Active"
                    };
                    _context.RFIDCards.Add(card);
                }

                await _context.SaveChangesAsync();
            }

            // Bao dam moi phien vao deu co the RFID de doi chieu o lan ra.
            if (card == null)
            {
                if (string.IsNullOrWhiteSpace(rfidCode))
                {
                    rfidCode = "RFID" + DateTime.Now.Ticks.ToString()[^6..];
                }

                card = await _context.RFIDCards.FirstOrDefaultAsync(c => c.RfidCode == rfidCode);
                if (card == null)
                {
                    card = new RFIDCard
                    {
                        RfidCode = rfidCode,
                        UID = rfidCode,
                        Status = "Active"
                    };
                    _context.RFIDCards.Add(card);
                    await _context.SaveChangesAsync();
                }
            }

            var activeSessions = await _context.ParkingSessions
                .AsNoTracking()
                .Where(s => s.CheckOutTime == null)
                .OrderByDescending(s => s.CheckInTime)
                .Select(s => new ActiveSessionLookup
                {
                    LicensePlateIn = s.LicensePlateIn,
                    CardId = s.CardId,
                    CheckInTime = s.CheckInTime
                })
                .ToListAsync();

            var plateAlreadyIn = activeSessions.FirstOrDefault(s => NormalizePlate(s.LicensePlateIn) == cleanInputPlate);
            if (plateAlreadyIn != null)
            {
                var atTime = plateAlreadyIn.CheckInTime?.ToString("HH:mm dd/MM/yyyy") ?? "khong xac dinh";
                TempData["Error"] = $"Bien so {licensePlate} dang co phien chua checkout (vao luc {atTime}).";
                return RedirectToAction("Monitor");
            }

            if (card?.CardId != null)
            {
                var cardAlreadyIn = activeSessions.FirstOrDefault(s => s.CardId == card.CardId);
                if (cardAlreadyIn != null)
                {
                    var atTime = cardAlreadyIn.CheckInTime?.ToString("HH:mm dd/MM/yyyy") ?? "khong xac dinh";
                    TempData["Error"] = $"The {card.RfidCode} dang duoc dung cho mot xe chua ra bai (vao luc {atTime}).";
                    return RedirectToAction("Monitor");
                }
            }

            ParkingPosition? emptyPos = null;
            if (suggestedPositionId.HasValue)
            {
                emptyPos = await _context.ParkingPositions
                    .FirstOrDefaultAsync(p => p.PositionId == suggestedPositionId.Value && !p.IsOccupied);
            }

            if (emptyPos == null)
            {
                var normalizedZone = NormalizeZone(preferredZone);
                var emptyPosQuery = _context.ParkingPositions.Where(p => !p.IsOccupied);
                if (!string.IsNullOrWhiteSpace(normalizedZone))
                {
                    emptyPosQuery = emptyPosQuery.Where(p => p.Zone.ToUpper() == normalizedZone);
                }

                emptyPos = await emptyPosQuery.OrderBy(p => p.PositionId).FirstOrDefaultAsync();
                if (emptyPos == null)
                {
                    TempData["Error"] = string.IsNullOrWhiteSpace(normalizedZone)
                        ? "Bai xe da het cho do."
                        : $"Zone {normalizedZone} da het cho trong. Vui long chon zone khac.";
                    return RedirectToAction("Monitor");
                }
            }

            emptyPos.IsOccupied = true;
            var session = new ParkingSession
            {
                VehicleId = vehicle!.VehicleId,
                CardId = card?.CardId,
                PositionId = emptyPos.PositionId,
                CheckInTime = DateTime.Now,
                LicensePlateIn = licensePlate,
                LicensePlateOut = "",
                ImageIn = "/uploads/no-image.jpg",
                ImageOut = "",
                TotalFee = 0
            };

            if (!string.IsNullOrWhiteSpace(capturedImageBase64))
            {
                try
                {
                    string fileName = $"In_{cleanInputPlate}_{DateTime.Now:yyyyMMddHHmmss}.jpg";
                    string folderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
                    if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
                    await System.IO.File.WriteAllBytesAsync(
                        Path.Combine(folderPath, fileName),
                        Convert.FromBase64String(capturedImageBase64.Split(',')[1]));
                    session.ImageIn = "/uploads/" + fileName;
                }
                catch
                {
                    // Ignore image write failure to avoid blocking check-in.
                }
            }

            _context.ParkingSessions.Add(session);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Xe {licensePlate} da vao bai tai zone {emptyPos.Zone}, vi tri {emptyPos.PositionId}.";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> GetActiveSessionByPlate(string plate)
        {
            string cleanPlate = NormalizePlate(plate);
            var activeSessions = await _context.ParkingSessions
                .Include(s => s.RFIDCard)
                .Include(s => s.Vehicle).ThenInclude(v => v.VehicleType)
                .Include(s => s.Vehicle).ThenInclude(v => v.MonthlyCards)
                .Include(s => s.ParkingPosition)
                .Where(s => s.CheckOutTime == null)
                .OrderByDescending(s => s.CheckInTime)
                .ToListAsync();

            var samePlateSessions = activeSessions
                .Where(s => NormalizePlate(s.LicensePlateIn) == cleanPlate)
                .OrderByDescending(s => s.CheckInTime)
                .ToList();

            if (!samePlateSessions.Any()) return Json(new { success = false });

            var session = samePlateSessions.First();
            var overlapCount = samePlateSessions.Count;
            var requiresResolve = overlapCount > 1;

            var hasMonthlyCard = session.Vehicle?.MonthlyCards?.Any(m => m.ExpiryDate >= DateTime.Now) ?? false;
            var feeResult = await CalculateFeeAsync(session, DateTime.Now);

            return Json(new
            {
                success = true,
                ownerName = session.Vehicle?.MonthlyCards?.FirstOrDefault()?.OwnerName ?? "Khach",
                checkInTime = session.CheckInTime?.ToString("HH:mm dd/MM/yyyy") ?? "---",
                rfidCode = session.RFIDCard?.RfidCode ?? string.Empty,
                fee = feeResult.TotalFee,
                isMonthly = hasMonthlyCard,
                zone = session.ParkingPosition?.Zone ?? "---",
                freeMinutes = feeResult.FreeMinutes,
                requiresResolve = requiresResolve,
                overlapCount = overlapCount,
                warning = requiresResolve
                    ? $"Phat hien {overlapCount} phien dang hoat dong cung bien so. Vui long vao Lich Su Ra Vao de xu ly trung truoc khi cho xe ra."
                    : string.Empty
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResolvePlateOverlap(string licensePlate)
        {
            var normalizedPlate = NormalizePlate(licensePlate);
            if (string.IsNullOrWhiteSpace(normalizedPlate))
            {
                TempData["Error"] = "Khong tim thay bien so hop le de xu ly trung.";
                return RedirectToAction(nameof(Index));
            }

            var sessions = await _context.ParkingSessions
                .Include(s => s.ParkingPosition)
                .Where(s => s.CheckOutTime == null)
                .OrderByDescending(s => s.CheckInTime)
                .ToListAsync();

            var samePlateSessions = sessions
                .Where(s => NormalizePlate(s.LicensePlateIn) == normalizedPlate)
                .OrderByDescending(s => s.CheckInTime)
                .ToList();

            if (samePlateSessions.Count <= 1)
            {
                TempData["Success"] = "Khong con phien trung cho bien so nay.";
                return RedirectToAction(nameof(Index));
            }

            var keepSession = samePlateSessions.First();
            var closedCount = 0;

            foreach (var staleSession in samePlateSessions.Skip(1))
            {
                staleSession.CheckOutTime = DateTime.Now;
                staleSession.TotalFee = 0;
                staleSession.LicensePlateOut = staleSession.LicensePlateIn;

                if (staleSession.ParkingPosition != null)
                {
                    staleSession.ParkingPosition.IsOccupied = false;
                }

                closedCount++;
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Da xu ly trung bien {keepSession.LicensePlateIn}: dong {closedCount} phien cu, giu lai phien moi nhat.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSession(int sessionId)
        {
            var session = await _context.ParkingSessions
                .Include(s => s.ParkingPosition)
                .FirstOrDefaultAsync(s => s.SessionId == sessionId);

            if (session == null)
            {
                TempData["Error"] = "Khong tim thay phien de xoa.";
                return RedirectToAction(nameof(Index));
            }

            // Neu xoa phien dang hoat dong thi mo lai vi tri do de tranh bi khoa cho sai.
            if (session.CheckOutTime == null && session.ParkingPosition != null)
            {
                session.ParkingPosition.IsOccupied = false;
            }

            _context.ParkingSessions.Remove(session);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"Da xoa phien #{sessionId} thanh cong.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAllHistory()
        {
            var allSessions = await _context.ParkingSessions.ToListAsync();
            if (allSessions.Count == 0)
            {
                TempData["Success"] = "Lich su dang rong.";
                return RedirectToAction(nameof(Index));
            }

            var occupiedPositions = await _context.ParkingPositions
                .Where(p => p.IsOccupied)
                .ToListAsync();

            foreach (var position in occupiedPositions)
            {
                position.IsOccupied = false;
            }

            _context.ParkingSessions.RemoveRange(allSessions);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Da xoa toan bo {allSessions.Count} phien lich su.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> ProcessCheckOut(string rfidCode)
        {
            if (string.IsNullOrWhiteSpace(rfidCode))
            {
                TempData["Error"] = "Vui long quet the RFID hop le.";
                return RedirectToAction("Monitor");
            }

            rfidCode = rfidCode.Trim();

            var session = await _context.ParkingSessions
                .Include(s => s.RFIDCard)
                .Include(s => s.Vehicle).ThenInclude(v => v.VehicleType)
                .Where(s => s.RFIDCard != null && s.RFIDCard.RfidCode == rfidCode && s.CheckOutTime == null)
                .OrderByDescending(s => s.CheckInTime)
                .FirstOrDefaultAsync();

            if (session == null)
            {
                TempData["Error"] = "The khong hop le hoac xe chua quet vao.";
                return RedirectToAction("Monitor");
            }

            session.CheckOutTime = DateTime.Now;
            var feeResult = await CalculateFeeAsync(session, session.CheckOutTime.Value);
            var feeToPay = feeResult.TotalFee;

            var monthlyCard = await _context.MonthlyCards
                .FirstOrDefaultAsync(m => m.CardId == session.CardId && m.VehicleId == session.VehicleId && m.ExpiryDate >= DateTime.Now);

            if (monthlyCard != null)
            {
                if (feeToPay > 0)
                {
                    if (monthlyCard.Balance < feeToPay)
                    {
                        TempData["Error"] = $"The thang khong du tien! Can: {feeToPay:N0}d, So du: {monthlyCard.Balance:N0}d";
                        session.CheckOutTime = null;
                        return RedirectToAction("Monitor");
                    }

                    monthlyCard.Balance -= feeToPay;
                    _context.MonthlyCards.Update(monthlyCard);
                    TempData["Success"] = $"Xe the thang ra thanh cong. Da tru {feeToPay:N0}d vao so du.";
                }
                else
                {
                    TempData["Success"] = "Xe the thang ra thanh cong. Khong phat sinh phi trong thoi gian mien phi.";
                }
            }
            else
            {
                TempData["Success"] = $"Xe khach ra thanh cong. Phi thu: {feeToPay:N0}d";
            }

            session.TotalFee = feeToPay;
            var pos = await _context.ParkingPositions.FindAsync(session.PositionId);
            if (pos != null) pos.IsOccupied = false;

            await _context.SaveChangesAsync();
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> DetectPlate([FromBody] ImageModel model)
        {
            try
            {
                if (string.IsNullOrEmpty(model.Image)) return Json(new { success = false });
                var bytes = Convert.FromBase64String(model.Image.Contains(',') ? model.Image.Split(',')[1] : model.Image);
                var plateApiToken = Environment.GetEnvironmentVariable("PLATE_RECOGNIZER_TOKEN")
                    ?? _configuration["PlateRecognizer:ApiToken"];
                var plateApiUrl = _configuration["PlateRecognizer:ApiUrl"] ?? "https://api.platerecognizer.com/v1/plate-reader/";

                if (string.IsNullOrWhiteSpace(plateApiToken))
                {
                    return Json(new { success = false, message = "Missing plate recognizer API token." });
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"Token {plateApiToken}");
                var content = new MultipartFormDataContent();
                content.Add(new ByteArrayContent(bytes), "upload", "plate.jpg");
                var res = await client.PostAsync(plateApiUrl, content);
                dynamic result = Newtonsoft.Json.JsonConvert.DeserializeObject(await res.Content.ReadAsStringAsync());
                if (result.results.Count > 0)
                {
                    return Json(new
                    {
                        success = true,
                        plate = result.results[0].plate.ToString().ToUpper(),
                        vehicleType = result.results[0].vehicle?.type?.ToString() ?? "unknown"
                    });
                }

                return Json(new { success = false });
            }
            catch
            {
                return Json(new { success = false });
            }
        }

        private sealed class ActiveSessionLookup
        {
            public string? LicensePlateIn { get; set; }
            public int? CardId { get; set; }
            public DateTime? CheckInTime { get; set; }
        }

        private sealed class FeeCalculationResult
        {
            public decimal TotalFee { get; set; }
            public TimeSpan Duration { get; set; }
            public int FreeMinutes { get; set; }
            public decimal BasePrice { get; set; }
            public decimal HourlyRate { get; set; }
        }
    }
}
