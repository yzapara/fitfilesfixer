using System.Diagnostics;
using File = System.IO.File;
using Dynastream.Fit;
using FitFilesFixer.Web.Infrastructure;
using FitFilesFixer.Web.Models;

namespace FitFilesFixer.Web.Services;

public class FitFixerService : IFitFixerService
{
    public async Task<FitProcessResult> ProcessAsync(Stream inputStream, string originalFileName, double maxSpeedKmh, double? startLat, double? startLon)
    {
        if (inputStream == null) throw new ArgumentNullException(nameof(inputStream));

        var tmpDir = Path.Combine(Path.GetTempPath(), "fiteditor");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "activity.fit");
        var uniqueId = Guid.NewGuid().ToString();
        var extension = Path.GetExtension(originalFileName);
        var outputName = Path.GetFileNameWithoutExtension(originalFileName) + "_fixed" + extension;
        var savedFileName = outputName + "." + uniqueId;
        var outputPath = Path.Combine(tmpDir, savedFileName);

        using (var fs = File.Create(inputPath))
        {
            await inputStream.CopyToAsync(fs);
        }

        int nullCoords = 0, jumpCoords = 0, fixedPoints = 0, totalPoints = 0;
        int droppedTimestamp = 0, droppedDuplicate = 0, droppedCorrupt = 0;
        var trackPoints = new List<TrackPoint>();

        var sw = Stopwatch.StartNew();

        try
        {
            var messages = FitHelpers.ReadAllMessages(inputPath);

            const uint MIN_VALID_FIT_TS = 315619200u;
            const uint MAX_VALID_FIT_TS = 1420156800u;

            double? lastValidLatDeg = null;
            double? lastValidLonDeg = null;
            uint? lastValidTs = null;
            bool fileIdSeen = false;
            var cleanMessages = new List<Mesg>();

            foreach (var m in messages)
            {
                if (m.Num == MesgNum.FileId)
                {
                    if (fileIdSeen) { droppedDuplicate++; continue; }
                    fileIdSeen = true;
                    cleanMessages.Add(m);
                    continue;
                }

                if (m.Num == MesgNum.Invalid)
                {
                    droppedCorrupt++;
                    continue;
                }

                if (!string.Equals(m.Name, "record", StringComparison.OrdinalIgnoreCase))
                {
                    var tsField = m.GetField(253);
                    if (tsField?.GetValue() is uint uval && (uval < MIN_VALID_FIT_TS || uval > MAX_VALID_FIT_TS))
                    {
                        droppedTimestamp++;
                        continue;
                    }
                    cleanMessages.Add(m);
                    continue;
                }

                totalPoints++;
                var rec = new RecordMesg(m);
                int? recLat = rec.GetPositionLat();
                int? recLon = rec.GetPositionLong();

                uint? recTs = rec.GetField(253)?.GetValue() is uint rts && rts >= MIN_VALID_FIT_TS && rts <= MAX_VALID_FIT_TS
                    ? rts : (uint?)null;

                bool needsFix = false;

                if (recLat == null || recLon == null)
                {
                    nullCoords++;
                    needsFix = true;
                }
                else
                {
                    double latDeg = FitHelpers.SemicirclesToDegrees(recLat.Value);
                    double lonDeg = FitHelpers.SemicirclesToDegrees(recLon.Value);

                    if (!lastValidLatDeg.HasValue)
                    {
                        bool withinStartArea = !startLat.HasValue || !startLon.HasValue ||
                            FitHelpers.HaversineMeters(startLat.Value, startLon.Value, latDeg, lonDeg) <= 50_000.0;

                        if (withinStartArea)
                        {
                            lastValidLatDeg = latDeg;
                            lastValidLonDeg = lonDeg;
                            lastValidTs = recTs;
                        }
                        else
                        {
                            jumpCoords++;
                            needsFix = true;
                        }
                    }
                    else
                    {
                        var dist = FitHelpers.HaversineMeters(lastValidLatDeg.Value, lastValidLonDeg!.Value, latDeg, lonDeg);
                        double dtSeconds = 1.0;

                        if (recTs.HasValue && lastValidTs.HasValue && recTs.Value > lastValidTs.Value)
                            dtSeconds = recTs.Value - lastValidTs.Value;

                        double maxSpeedMs = maxSpeedKmh / 3.6;
                        double maxAllowedDist = maxSpeedMs * dtSeconds;

                        if (dist > maxAllowedDist)
                        {
                            jumpCoords++;
                            needsFix = true;
                        }
                    }
                }

                if (needsFix)
                {
                    double? clampLat = lastValidLatDeg ?? startLat;
                    double? clampLon = lastValidLonDeg ?? startLon;

                    if (clampLat.HasValue && clampLon.HasValue)
                    {
                        rec.SetPositionLat(FitHelpers.DegreesToSemicircles(clampLat.Value));
                        rec.SetPositionLong(FitHelpers.DegreesToSemicircles(clampLon.Value));
                        trackPoints.Add(new TrackPoint(clampLat.Value, clampLon.Value, true));
                    }
                    fixedPoints++;
                }
                else if (recLat != null && recLon != null)
                {
                    lastValidLatDeg = FitHelpers.SemicirclesToDegrees(recLat.Value);
                    lastValidLonDeg = FitHelpers.SemicirclesToDegrees(recLon.Value);
                    lastValidTs = recTs;
                    trackPoints.Add(new TrackPoint(lastValidLatDeg.Value, lastValidLonDeg.Value, false));
                }

                cleanMessages.Add(rec);
            }

            FitHelpers.WriteAllMessages(cleanMessages, outputPath);

            sw.Stop();

            return new FitProcessResult
            {
                TotalPoints = totalPoints,
                FixedPoints = fixedPoints,
                NullCoords = nullCoords,
                JumpCoords = jumpCoords,
                DroppedTimestamp = droppedTimestamp,
                DroppedDuplicate = droppedDuplicate,
                DroppedCorrupt = droppedCorrupt,
                ProcessingMs = (int)sw.ElapsedMilliseconds,
                OutputPath = outputPath,
                OutputName = outputName,
                TrackPoints = TrackSampler.Subsample(trackPoints, 4000)
            };
        }
        catch
        {
            sw.Stop();
            throw;
        }
    }
}
