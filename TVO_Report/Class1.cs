using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TVO_Report
{
    internal class Class1
    {
    }

    public class SiloSummary
    {
        public string Silo { get; set; }

        // Temperature
        public double AvgTemp { get; set; }
        public string TimeMinTemp { get; set; }
        public double MinTemp { get; set; }
        public string TimeMaxTemp { get; set; }
        public double MaxTemp { get; set; }

        // Moisture
        public double AvgMoisture { get; set; }
        public string TimeMinMoist { get; set; }
        public double MinMoisture { get; set; }
        public string TimeMaxMoist { get; set; }
        public double MaxMoisture { get; set; }

        public double GrainLevel { get; set; }
    }

    public class WeatherSummary
    {
        public string Parameter { get; set; }

        public double Avg { get; set; }

        public double Min { get; set; }

        public double Max { get; set; }
    }

    public class FanOperation
    {
        public DateTime Time { get; set; }

        //public string Silo { get; set; }

        public string Fan { get; set; }

        public string Event { get; set; }
    }
    public class SiloDetailModel
    {
        public string SiloName { get; set; }

        public List<SensorRow> Sensors { get; set; }
    }


    public class SensorRow
    {
        public string Silo { get; set; }
        public string Time { get; set; }
        public string Sensor { get; set; }

        public double C1 { get; set; }
        public double C2 { get; set; }
        public double C3 { get; set; }
        public double C4 { get; set; }
        public double C5 { get; set; }
        public double C6 { get; set; }
        public double C7 { get; set; }
        public double C8 { get; set; }
    }

    public class SiloLevel
    {
        public string Silo { get; set; }

        public double Level1430 { get; set; }

        public double Level2230 { get; set; }

        public double Level0630 { get; set; }
    }

    public class EventLogRow
    {
        public DateTime Time { get; set; }
        public string Category { get; set; }
        public string Level { get; set; }
        public string Source { get; set; }
        public string Event { get; set; }
        public string Detail { get; set; }
        public long? DurationSec { get; set; }

        public string Duration
        {
            get
            {
                if (!DurationSec.HasValue) return "";
                TimeSpan t = TimeSpan.FromSeconds(DurationSec.Value);
                return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
            }
        }
    }

    public class LogSummaryItem
    {
        public string Title { get; set; }
        public string Value { get; set; }
        public string Background { get; set; }
    }
}
