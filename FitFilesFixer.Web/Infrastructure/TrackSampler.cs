using FitFilesFixer.Web.Models;

namespace FitFilesFixer.Web.Infrastructure;

public static class TrackSampler
{
    public static List<TrackPoint> Subsample(List<TrackPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints)
            return points;

        double step = (double)points.Count / maxPoints;
        var result = new List<TrackPoint>(maxPoints);

        for (int slot = 0; slot < maxPoints; slot++)
        {
            int centre = (int)(slot * step + step / 2.0);
            int lo = (int)(slot * step);
            int hi = Math.Min(points.Count - 1, (int)((slot + 1) * step) - 1);

            TrackPoint? chosen = null;
            for (int j = lo; j <= hi; j++)
            {
                if (points[j].Fixed)
                {
                    chosen = points[j];
                    break;
                }
            }

            result.Add(chosen ?? points[Math.Clamp(centre, 0, points.Count - 1)]);
        }

        return result;
    }
}
