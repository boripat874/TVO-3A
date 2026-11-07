using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace TVO_Report
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // อ่านจาก App.config (key = dbPath) ถ้าไม่ได้ตั้งไว้ใช้ค่าเดิม
        public static string dbPath =
            string.IsNullOrWhiteSpace(System.Configuration.ConfigurationManager.AppSettings["dbPath"])
                ? "C:/TVO API Reader/TVOData.db"
                : System.Configuration.ConfigurationManager.AppSettings["dbPath"];

        private static readonly string connectionString = "Data Source=" + dbPath + ";Version=3;";

        // เก็บข้อมูล Detail ไว้ก่อน แล้วค่อยผูกเข้า DataGrid ตอนเปิดแท็บ Sensor Detail
        private List<SensorRow> _detailList;
        private bool _detailBound = false;

        public MainWindow()
        {
            InitializeComponent();

            GlobalFontSettings.UseWindowsFontsUnderWindows = true;

            dpDate.SelectedDate = DateTime.Now;
            InitLogFilters();
        }

        // ================= Loading Popup =================

        private async Task ShowLoadingAsync(string message)
        {
            txtLoading.Text = message;
            LoadingOverlay.Visibility = Visibility.Visible;

            // รอให้ WPF วาด popup ขึ้นจอก่อน แล้วค่อยทำงานหนักต่อ
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        private async Task HideLoadingAsync()
        {
            // รอให้ Layout / Render ของ DataGrid เสร็จก่อน แล้วค่อยปิด popup
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        // รอบรายงาน 08:00 ถึง 08:00 ของวันถัดไป (ใช้ช่วงเดียวกันทุกตาราง, ไม่รวมขอบปลาย)
        private static DateTime PeriodStart(DateTime date)
        {
            return new DateTime(date.Year, date.Month, date.Day, 8, 0, 0);
        }

        private static SQLiteCommand NewCmd(SQLiteConnection conn, string sql, DateTime date)
        {
            SQLiteCommand cmd = new SQLiteCommand(sql, conn);
            DateTime start = PeriodStart(date);
            cmd.Parameters.AddWithValue("@StartDate", start);
            cmd.Parameters.AddWithValue("@EndDate", start.AddDays(1));
            return cmd;
        }

        // ================= Load Report =================

        private async void LoadReport_Click(object sender, RoutedEventArgs e)
        {
            if (dpDate.SelectedDate == null)
            {
                MessageBox.Show("Please select date.");
                return;
            }

            DateTime date = dpDate.SelectedDate.Value;
            Button btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;

            await ShowLoadingAsync("กำลังโหลดข้อมูล...");

            try
            {
                // ปล่อย DataGrid เดิมก่อน เพื่อคืนหน่วยความจำของรายงานก่อนหน้า
                dgDetail.ItemsSource = null;
                _detailList = null;

                // Query ทุกส่วนพร้อมกันบน Background Thread (แต่ละตัวเปิด connection ของตัวเอง)
                Task<List<SiloSummary>> tSilo = Task.Run(() => LoadSilo(date));
                Task<List<WeatherSummary>> tWeather = Task.Run(() => LoadWeather(date));
                Task<List<FanOperation>> tFan = Task.Run(() => LoadFan(date));
                Task<List<SiloLevel>> tLevel = Task.Run(() => LoadSiloLevel(date));
                Task<List<SensorRow>> tDetail = Task.Run(() => LoadAllDetail(date));
                await Task.WhenAll(tSilo, tWeather, tFan, tLevel, tDetail);

                if (tSilo.Result.Count == 0)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    MessageBox.Show("No data.");
                    return;
                }

                dgSilo.ItemsSource = tSilo.Result;
                dgWeather.ItemsSource = tWeather.Result;
                dgFan.ItemsSource = tFan.Result;
                dgLevel.ItemsSource = tLevel.Result;

                _detailList = tDetail.Result;
                _detailBound = false;

                txtDate.Text = date.ToString("dd/MM/yyyy") + "  (08:00 - 08:00)";

                // ถ้าผู้ใช้เปิดแท็บ Sensor Detail อยู่แล้ว ให้แสดงเลย
                if (tabCtl.SelectedIndex == 1)
                    await BindDetailAsync();
            }
            catch (Exception err)
            {
                MessageBox.Show(err.GetBaseException().Message);
            }
            finally
            {
                await HideLoadingAsync();
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private async void tabCtl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged ของ DataGrid จะ bubble ขึ้นมาด้วย -> กรองเอาเฉพาะของ TabControl
            if (e.OriginalSource != tabCtl) return;

            if (tabCtl.SelectedIndex == 1)
                await BindDetailAsync();
        }

        private async Task BindDetailAsync()
        {
            if (_detailBound || _detailList == null) return;

            await ShowLoadingAsync($"กำลังแสดงข้อมูล Sensor ({_detailList.Count:N0} แถว)...");

            // DataGrid เปิด virtualization แล้ว (ดู MainWindow.xaml) -> สร้างเฉพาะแถวที่เห็นบนจอ
            dgDetail.ItemsSource = _detailList;
            _detailBound = true;

            await HideLoadingAsync();
        }

        // cache string ที่ซ้ำกันทุกแถว (Silo / Time / Sensor) -> ลดหน่วยความจำของรายการหลายหมื่นแถว
        private static string SensorName(int type, int line)
        {
            return type == 0 ? "Temp L" + line : "Moisture L" + line;
        }

        private static string Cached(Dictionary<string, string> pool, string s)
        {
            if (s == null) return null;
            string v;
            if (pool.TryGetValue(s, out v)) return v;
            pool[s] = s;
            return s;
        }

        private static string ReadTime(SQLiteDataReader rd, int ordinal)
        {
            if (rd.IsDBNull(ordinal)) return "-";
            try
            {
                return rd.GetDateTime(ordinal).ToString("HH:mm:ss");
            }
            catch
            {
                DateTime dt;
                string raw = rd.GetValue(ordinal).ToString();
                return DateTime.TryParse(raw, out dt) ? dt.ToString("HH:mm:ss") : raw;
            }
        }

        private List<SensorRow> LoadAllDetail(DateTime date)
        {
            List<SensorRow> list = new List<SensorRow>(100000);
            Dictionary<string, string> pool = new Dictionary<string, string>();

            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                // เรียงตามเวลาจริง (LogTime) เพราะรอบรายงานคือ 08:00 - 08:00 ของวันถัดไป
                string sql = @"
            SELECT Siloname, LogTime, SensorType, SensorLine,
                   S1, S2, S3, S4, S5, S6, S7, S8
            FROM SensorConfig
            WHERE LogTime >= @StartDate
              AND LogTime < @EndDate
              AND Siloname IN ('S1','S2','S3','S4','S5','S6','S7','S8')
            ORDER BY LogTime ASC, Siloname ASC, SensorType ASC, SensorLine ASC;";

                using (SQLiteCommand cmd = NewCmd(conn, sql, date))
                using (SQLiteDataReader rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        SensorRow row = new SensorRow();
                        row.Silo = Cached(pool, rd.GetString(0));
                        row.Time = Cached(pool, ReadTime(rd, 1));
                        row.Sensor = Cached(pool, SensorName(Convert.ToInt32(rd.GetValue(2)), Convert.ToInt32(rd.GetValue(3))));

                        row.C1 = GetDouble(rd, 4);
                        row.C2 = GetDouble(rd, 5);
                        row.C3 = GetDouble(rd, 6);
                        row.C4 = GetDouble(rd, 7);
                        row.C5 = GetDouble(rd, 8);
                        row.C6 = GetDouble(rd, 9);
                        row.C7 = GetDouble(rd, 10);
                        row.C8 = GetDouble(rd, 11);

                        list.Add(row);
                    }
                }
            }

            list.TrimExcess();
            return list;
        }

        // ================= Queries =================
        // ค่า 0 / NULL = อ่านไม่ได้ หรือข้อมูลเก่าเกินกำหนด -> ไม่นำมาคิด Avg/Min/Max
        // (เดิมค่า 0 ทำให้ Min Temp / Min Moist แสดงเป็น 0.0)

        private List<SiloSummary> LoadSilo(DateTime date)
        {
            List<SiloSummary> list = new List<SiloSummary>();

            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                string sql = @"
            SELECT
                Siloname,
                -- Temperature (SensorType = 0)
                AVG(CASE WHEN SensorType = 0 THEN NULLIF(SAVG, 0) END) AS AvgTemp,
                (SELECT strftime('%H:%M', LogTime) FROM SensorConfig T2
                 WHERE T2.Siloname = T1.Siloname AND T2.SensorType = 0
                   AND T2.LogTime >= @StartDate AND T2.LogTime < @EndDate AND T2.SMin > 0
                 ORDER BY T2.SMin ASC LIMIT 1) AS TimeMinTemp,
                MIN(CASE WHEN SensorType = 0 THEN NULLIF(SMin, 0) END) AS MinTemp,
                (SELECT strftime('%H:%M', LogTime) FROM SensorConfig T2
                 WHERE T2.Siloname = T1.Siloname AND T2.SensorType = 0
                   AND T2.LogTime >= @StartDate AND T2.LogTime < @EndDate AND T2.SMax > 0
                 ORDER BY T2.SMax DESC LIMIT 1) AS TimeMaxTemp,
                MAX(CASE WHEN SensorType = 0 THEN NULLIF(SMax, 0) END) AS MaxTemp,

                -- Moisture (SensorType = 1)
                AVG(CASE WHEN SensorType = 1 THEN NULLIF(SAVG, 0) END) AS AvgMoist,
                (SELECT strftime('%H:%M', LogTime) FROM SensorConfig T2
                 WHERE T2.Siloname = T1.Siloname AND T2.SensorType = 1
                   AND T2.LogTime >= @StartDate AND T2.LogTime < @EndDate AND T2.SMin > 0
                 ORDER BY T2.SMin ASC LIMIT 1) AS TimeMinMoist,
                MIN(CASE WHEN SensorType = 1 THEN NULLIF(SMin, 0) END) AS MinMoist,
                (SELECT strftime('%H:%M', LogTime) FROM SensorConfig T2
                 WHERE T2.Siloname = T1.Siloname AND T2.SensorType = 1
                   AND T2.LogTime >= @StartDate AND T2.LogTime < @EndDate AND T2.SMax > 0
                 ORDER BY T2.SMax DESC LIMIT 1) AS TimeMaxMoist,
                MAX(CASE WHEN SensorType = 1 THEN NULLIF(SMax, 0) END) AS MaxMoist,

                AVG(SLevel) AS AvgLevel
            FROM SensorConfig T1
            WHERE LogTime >= @StartDate AND LogTime < @EndDate
            GROUP BY Siloname
            ORDER BY Siloname;";

                using (SQLiteCommand cmd = NewCmd(conn, sql, date))
                using (SQLiteDataReader rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        list.Add(new SiloSummary()
                        {
                            Silo = rd.GetString(0),

                            AvgTemp = GetDouble(rd, 1),
                            TimeMinTemp = rd.IsDBNull(2) ? "-" : rd.GetString(2),
                            MinTemp = GetDouble(rd, 3),
                            TimeMaxTemp = rd.IsDBNull(4) ? "-" : rd.GetString(4),
                            MaxTemp = GetDouble(rd, 5),

                            AvgMoisture = GetDouble(rd, 6),
                            TimeMinMoist = rd.IsDBNull(7) ? "-" : rd.GetString(7),
                            MinMoisture = GetDouble(rd, 8),
                            TimeMaxMoist = rd.IsDBNull(9) ? "-" : rd.GetString(9),
                            MaxMoisture = GetDouble(rd, 10),

                            GrainLevel = GetDouble(rd, 11)
                        });
                    }
                }
            }

            // เรียง S1, S2 ... S10 ตามตัวเลข (ไม่ใช่ S1, S10, S2)
            return list.OrderBy(x => SiloNo(x.Silo)).ThenBy(x => x.Silo).ToList();
        }

        private static int SiloNo(string name)
        {
            int n;
            string digits = new string((name ?? "").Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out n) ? n : int.MaxValue;
        }

        private List<WeatherSummary> LoadWeather(DateTime date)
        {
            List<WeatherSummary> list = new List<WeatherSummary>();

            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                // เดิมใช้ 00:00-24:00 ไม่ตรงกับตารางอื่น (08:00-08:00) -> ปรับให้ตรงกัน
                string sql = @"
    SELECT
        AVG(NULLIF(AMBTemp, 0)) AS AvgTemp,
        MIN(NULLIF(AMBTemp, 0)) AS MinTemp,
        MAX(NULLIF(AMBTemp, 0)) AS MaxTemp,
        AVG(NULLIF(AMBHum, 0))  AS AvgHum,
        MIN(NULLIF(AMBHum, 0))  AS MinHum,
        MAX(NULLIF(AMBHum, 0))  AS MaxHum
    FROM AMBLog
    WHERE LogTime >= @StartDate
      AND LogTime < @EndDate";

                using (SQLiteCommand cmd = NewCmd(conn, sql, date))
                using (SQLiteDataReader rd = cmd.ExecuteReader())
                {
                    if (rd.Read())
                    {
                        list.Add(new WeatherSummary()
                        {
                            Parameter = "Temperature",
                            Avg = Math.Round(GetDouble(rd, 0), 2),
                            Min = Math.Round(GetDouble(rd, 1), 2),
                            Max = Math.Round(GetDouble(rd, 2), 2)
                        });

                        list.Add(new WeatherSummary()
                        {
                            Parameter = "Humidity",
                            Avg = Math.Round(GetDouble(rd, 3), 2),
                            Min = Math.Round(GetDouble(rd, 4), 2),
                            Max = Math.Round(GetDouble(rd, 5), 2)
                        });
                    }
                }
            }

            return list;
        }

        private List<SiloLevel> LoadSiloLevel(DateTime date)
        {
            List<SiloLevel> list = new List<SiloLevel>();

            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                // ใช้ค่า SLevel แถวแรกหลังเวลา 14:30 / 22:30 / 06:30 ภายในรอบรายงาน
                string sql = @"
SELECT
    s1.Siloname,
    (SELECT SLevel FROM SensorConfig s2
      WHERE s2.Siloname = s1.Siloname AND s2.SensorType = 0
        AND s2.LogTime >= @T1430 AND s2.LogTime < @EndDate
      ORDER BY s2.LogTime LIMIT 1) AS Level1430,
    (SELECT SLevel FROM SensorConfig s2
      WHERE s2.Siloname = s1.Siloname AND s2.SensorType = 0
        AND s2.LogTime >= @T2230 AND s2.LogTime < @EndDate
      ORDER BY s2.LogTime LIMIT 1) AS Level2230,
    (SELECT SLevel FROM SensorConfig s2
      WHERE s2.Siloname = s1.Siloname AND s2.SensorType = 0
        AND s2.LogTime >= @T0630 AND s2.LogTime < @EndDate
      ORDER BY s2.LogTime LIMIT 1) AS Level0630
FROM SensorConfig s1
WHERE s1.LogTime >= @StartDate AND s1.LogTime < @EndDate
GROUP BY s1.Siloname;";

                using (SQLiteCommand cmd = NewCmd(conn, sql, date))
                {
                    DateTime start = PeriodStart(date);
                    cmd.Parameters.AddWithValue("@T1430", start.Date.AddHours(14.5));
                    cmd.Parameters.AddWithValue("@T2230", start.Date.AddHours(22.5));
                    cmd.Parameters.AddWithValue("@T0630", start.Date.AddDays(1).AddHours(6.5));

                    using (SQLiteDataReader rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            list.Add(new SiloLevel()
                            {
                                Silo = rd.GetString(0),
                                Level1430 = GetDouble(rd, 1),
                                Level2230 = GetDouble(rd, 2),
                                Level0630 = GetDouble(rd, 3)
                            });
                        }
                    }
                }
            }

            return list.OrderBy(x => SiloNo(x.Silo)).ThenBy(x => x.Silo).ToList();
        }

        private List<FanOperation> LoadFan(DateTime date)
        {
            List<FanOperation> list = new List<FanOperation>();

            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();

                string sql = @"SELECT LogTime, FanName, FanEvent
          FROM FanLog
          WHERE LogTime >= @StartDate AND LogTime < @EndDate
          ORDER BY LogTime";

                using (SQLiteCommand cmd = NewCmd(conn, sql, date))
                using (SQLiteDataReader rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        DateTime t;
                        try { t = rd.GetDateTime(0); }
                        catch { DateTime.TryParse(rd.GetValue(0).ToString(), out t); }

                        list.Add(new FanOperation()
                        {
                            Time = t,
                            Fan = rd.IsDBNull(1) ? "" : rd.GetString(1),
                            Event = rd.IsDBNull(2) ? "" : rd.GetString(2)
                        });
                    }
                }
            }

            return list;
        }

        public List<SensorRow> LoadDetail(string silo, DateTime date)
        {
            // ใช้ข้อมูลที่โหลดไว้แล้ว ไม่ต้อง query ซ้ำ
            List<SensorRow> all = _detailList ?? LoadAllDetail(date);
            return all.Where(r => r.Silo == silo).ToList();
        }

        public static double GetDouble(SQLiteDataReader rd, string column)
        {
            return GetDouble(rd, rd.GetOrdinal(column));
        }

        public static double GetDouble(SQLiteDataReader rd, int column)
        {
            if (rd.IsDBNull(column))
                return 0;

            return Convert.ToDouble(rd.GetValue(column));
        }

        // ================= Monitor Log =================

        private List<EventLogRow> _logList;

        private void InitLogFilters()
        {
            dpLogFrom.SelectedDate = DateTime.Today;
            dpLogTo.SelectedDate = DateTime.Today;

            cbLogCategory.ItemsSource = new[] { "All", "SYSTEM", "API", "PLC", "DATA" };
            cbLogLevel.ItemsSource = new[] { "All", "ERROR", "WARN", "INFO" };
            cbLogSource.ItemsSource = new[] { "All", "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "Weather", "API", "PLC", "Program" };
            cbLogCategory.SelectedIndex = 0;
            cbLogLevel.SelectedIndex = 0;
            cbLogSource.SelectedIndex = 0;
        }

        private class LogQuery
        {
            public DateTime Start, End;
            public string Category, Level, Source, Search;
        }

        private LogQuery ReadLogFilter()
        {
            DateTime from = (dpLogFrom.SelectedDate ?? DateTime.Today).Date;
            DateTime to = (dpLogTo.SelectedDate ?? from).Date;
            if (to < from) { DateTime t = from; from = to; to = t; }

            return new LogQuery
            {
                Start = from.AddHours(8),               // 08:00 ของวันเริ่ม
                End = to.AddDays(1).AddHours(8),        // 08:00 ของวันถัดจากวันสิ้นสุด
                Category = cbLogCategory.SelectedItem as string ?? "All",
                Level = cbLogLevel.SelectedItem as string ?? "All",
                Source = cbLogSource.SelectedItem as string ?? "All",
                Search = (txtLogSearch.Text ?? "").Trim()
            };
        }

        private void txtLogSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                LoadLog_Click(sender, e);
        }

        private async void LoadLog_Click(object sender, RoutedEventArgs e)
        {
            LogQuery q = ReadLogFilter();
            await ShowLoadingAsync("กำลังโหลด Monitor Log...");
            try
            {
                Task<List<EventLogRow>> tRows = Task.Run(() => LoadEventLog(q));
                Task<List<LogSummaryItem>> tSum = Task.Run(() => LoadLogSummary(q));
                await Task.WhenAll(tRows, tSum);

                _logList = tRows.Result;
                dgLog.ItemsSource = _logList;
                icLogSummary.ItemsSource = tSum.Result;
                txtLogInfo.Text = q.Start.ToString("dd/MM/yyyy HH:mm") + " - " + q.End.ToString("dd/MM/yyyy HH:mm")
                                  + "   (" + _logList.Count.ToString("N0") + " รายการ)";
            }
            catch (Exception err)
            {
                string msg = err.GetBaseException().Message;
                if (msg.IndexOf("no such table", StringComparison.OrdinalIgnoreCase) >= 0)
                    msg = "ยังไม่มีตาราง EventLog ในฐานข้อมูล\nกรุณาอัปเดตและเปิด TVO API Reader เวอร์ชันใหม่ก่อน";
                MessageBox.Show(msg);
            }
            finally
            {
                await HideLoadingAsync();
            }
        }

        private List<EventLogRow> LoadEventLog(LogQuery q)
        {
            List<EventLogRow> list = new List<EventLogRow>();
            StringBuilder sql = new StringBuilder(@"
SELECT LogTime, Category, Level, Source, Event, Detail, DurationSec
FROM EventLog
WHERE LogTime >= @StartDate AND LogTime < @EndDate");

            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand(conn))
                {
                    cmd.Parameters.AddWithValue("@StartDate", q.Start);
                    cmd.Parameters.AddWithValue("@EndDate", q.End);

                    if (q.Category != "All")
                    {
                        sql.Append(" AND Category = @Cat");
                        cmd.Parameters.AddWithValue("@Cat", q.Category);
                    }
                    if (q.Level != "All")
                    {
                        sql.Append(" AND Level = @Lv");
                        cmd.Parameters.AddWithValue("@Lv", q.Level);
                    }
                    if (q.Source != "All")
                    {
                        // "S3" ต้องตรงกับ "S3 L5", "S3 Moist" แต่ไม่ตรงกับ "S30"
                        sql.Append(" AND (Source = @Src OR Source LIKE @SrcPrefix)");
                        cmd.Parameters.AddWithValue("@Src", q.Source);
                        cmd.Parameters.AddWithValue("@SrcPrefix", q.Source + " %");
                    }
                    if (q.Search.Length > 0)
                    {
                        sql.Append(" AND (Event LIKE @Q OR Source LIKE @Q OR Detail LIKE @Q)");
                        cmd.Parameters.AddWithValue("@Q", "%" + q.Search + "%");
                    }
                    sql.Append(" ORDER BY LogTime DESC, ID DESC;");
                    cmd.CommandText = sql.ToString();

                    using (SQLiteDataReader rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            DateTime t;
                            try { t = rd.GetDateTime(0); }
                            catch { DateTime.TryParse(rd.GetValue(0).ToString(), out t); }

                            list.Add(new EventLogRow
                            {
                                Time = t,
                                Category = rd.IsDBNull(1) ? "" : rd.GetString(1),
                                Level = rd.IsDBNull(2) ? "" : rd.GetString(2),
                                Source = rd.IsDBNull(3) ? "" : rd.GetString(3),
                                Event = rd.IsDBNull(4) ? "" : rd.GetString(4),
                                Detail = rd.IsDBNull(5) ? "" : rd.GetString(5),
                                DurationSec = rd.IsDBNull(6) ? (long?)null : Convert.ToInt64(rd.GetValue(6))
                            });
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>สรุปทั้งช่วงเวลา (ไม่ขึ้นกับตัวกรอง Category/Level/Source/ค้นหา)</summary>
        private List<LogSummaryItem> LoadLogSummary(LogQuery q)
        {
            Dictionary<string, long> count = new Dictionary<string, long>();
            Dictionary<string, long> dur = new Dictionary<string, long>();

            using (SQLiteConnection conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand(@"
SELECT Event, COUNT(*), IFNULL(SUM(DurationSec), 0)
FROM EventLog
WHERE LogTime >= @StartDate AND LogTime < @EndDate
GROUP BY Event;", conn))
                {
                    cmd.Parameters.AddWithValue("@StartDate", q.Start);
                    cmd.Parameters.AddWithValue("@EndDate", q.End);
                    using (SQLiteDataReader rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            string ev = rd.GetString(0);
                            count[ev] = Convert.ToInt64(rd.GetValue(1));
                            dur[ev] = Convert.ToInt64(rd.GetValue(2));
                        }
                    }
                }
            }

            Func<string, long> C = ev => count.ContainsKey(ev) ? count[ev] : 0;
            Func<string, long> D = ev => dur.ContainsKey(ev) ? dur[ev] : 0;
            Func<long, string> Fmt = sec =>
            {
                TimeSpan t = TimeSpan.FromSeconds(sec);
                return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
            };
            const string red = "#FFE5E5", yellow = "#FFF8DC", white = "#FFFFFF";

            long apiDown = C("API_DISCONNECTED"), plcDown = C("PLC_DISCONNECTED");
            long loginFail = C("API_LOGIN_FAIL"), abnormal = C("APP_ABNORMAL_EXIT");
            //long lineFail = C("LINE_READ_FAIL"), tZero = C("TEMP_ZERO"), mZero = C("MOIST_ZERO");
            long tZero = C("TEMP_ZERO"), mZero = C("MOIST_ZERO");
            long wZero = C("WEATHER_TEMP_ZERO") + C("WEATHER_HUM_ZERO");

            return new List<LogSummaryItem>
            {
                new LogSummaryItem { Title = "API หลุด", Value = apiDown + " ครั้ง / " + Fmt(D("API_RECONNECTED")), Background = apiDown > 0 ? red : white },
                new LogSummaryItem { Title = "Login ไม่ผ่าน", Value = loginFail + " ครั้ง", Background = loginFail > 0 ? red : white },
                new LogSummaryItem { Title = "PLC หลุด", Value = plcDown + " ครั้ง / " + Fmt(D("PLC_RECONNECTED")), Background = plcDown > 0 ? red : white },
                //new LogSummaryItem { Title = "สายอ่านไม่ได้", Value = lineFail + " ครั้ง", Background = lineFail > 0 ? yellow : white },
                new LogSummaryItem { Title = "Temp = 0", Value = tZero + " ครั้ง", Background = tZero > 0 ? yellow : white },
                new LogSummaryItem { Title = "Moist = 0", Value = mZero + " ครั้ง", Background = mZero > 0 ? yellow : white },
                new LogSummaryItem { Title = "Weather = 0", Value = wZero + " ครั้ง", Background = wZero > 0 ? yellow : white },
                new LogSummaryItem { Title = "เปิด / ปิด / ปิดผิดปกติ",
                                     Value = C("APP_START") + " / " + C("APP_STOP") + " / " + abnormal,
                                     Background = abnormal > 0 ? red : white }
            };
        }

        private void ExportLogCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_logList == null)
            {
                MessageBox.Show("Please load log first.");
                return;
            }

            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Filter = "CSV files (*.csv)|*.csv";
            dlg.FileName = "MonitorLog.csv";
            if (dlg.ShowDialog() != true) return;

            try
            {
                using (StreamWriter sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8))
                {
                    sw.WriteLine("Time,Category,Level,Source,Event,Detail,Duration");
                    foreach (EventLogRow r in _logList)
                    {
                        sw.WriteLine(string.Join(",", new[]
                        {
                            Csv(r.Time.ToString("yyyy-MM-dd HH:mm:ss")), Csv(r.Category), Csv(r.Level),
                            Csv(r.Source), Csv(r.Event), Csv(r.Detail), Csv(r.Duration)
                        }));
                    }
                }
                MessageBox.Show("Export Log CSV Complete");
            }
            catch (Exception err)
            {
                MessageBox.Show(err.Message);
            }
        }

        private static string Csv(string v)
        {
            return "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        }

        // ================= Export PDF =================

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_detailList == null)
            {
                MessageBox.Show("Please load report first.");
                return;
            }

            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Filter = "PDF (*.pdf)|*.pdf";
            dlg.FileName = "Daily Report.pdf";

            if (dlg.ShowDialog() != true) return;

            string basePath = Path.Combine(
                Path.GetDirectoryName(dlg.FileName),
                Path.GetFileNameWithoutExtension(dlg.FileName));

            try
            {
                // Summary ยัง render จากหน้าจอ ต้องให้แท็บ Summary แสดงอยู่
                tabCtl.SelectedIndex = 0;
                await ShowLoadingAsync("กำลัง Export PDF...");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

                ExportPdf(basePath + "_Summary.pdf");

                // Detail วาดจากข้อมูลตรง ๆ บน background thread
                var rows = _detailList;
                string reportDate = txtDate.Text;
                await Task.Run(() => ExportPdfDetail(basePath + "_Detail.pdf", rows, reportDate));

                LoadingOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show("Export Complete");
            }
            catch (Exception err)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show(err.Message);
            }
        }

        private void ExportPdf(string filename)
        {
            double w = ReportPage.ActualWidth;
            double h = ReportPage.ActualHeight;

            const double dpi = 192;   // 2 เท่าของจอ -> PDF คมขึ้น
            int pxW = (int)Math.Ceiling(w * dpi / 96);
            int pxH = (int)Math.Ceiling(h * dpi / 96);

            // วาดผ่าน VisualBrush -> ไม่ติด offset/padding ของ parent
            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                Rect rect = new Rect(0, 0, w, h);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawRectangle(new VisualBrush(ReportPage), null, rect);
            }

            RenderTargetBitmap bmp = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
            bmp.Render(dv);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using (MemoryStream ms = new MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;

                PdfDocument pdf = new PdfDocument();
                PdfPage page = pdf.AddPage();
                page.Size = PdfSharp.PageSize.A4;

                using (XGraphics gfx = XGraphics.FromPdfPage(page))
                using (XImage img = XImage.FromStream(ms))
                {
                    // ย่อให้พอดีหน้า A4 โดยคงสัดส่วน
                    const double margin = 20;
                    double availW = page.Width.Point - margin * 2;
                    double availH = page.Height.Point - margin * 2;

                    double scale = Math.Min(availW / w, availH / h);
                    double drawW = w * scale;
                    double drawH = h * scale;

                    gfx.DrawImage(img, margin + (availW - drawW) / 2, margin, drawW, drawH);
                }

                pdf.Save(filename);
            }
        }

        private void ExportPdfDetail(string filename, List<SensorRow> rows, string reportDate)
        {
            const double margin = 30;
            const double titleH = 40;
            const double headerH = 16;
            const double rowH = 12;
            const double footerH = 20;

            string[] headers = { "Silo", "Time", "Sensor", "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8" };
            double[] weights = { 0.8, 1.2, 1.5, 1, 1, 1, 1, 1, 1, 1, 1 };

            // PdfSharp 6.x ใช้ XFontStyleEx / ถ้าเป็นเวอร์ชัน 1.5x ให้เปลี่ยนเป็น XFontStyle
            XFont fTitle = new XFont("Arial", 14, XFontStyleEx.Bold);
            XFont fSub = new XFont("Arial", 9, XFontStyleEx.Regular);
            XFont fHead = new XFont("Arial", 8, XFontStyleEx.Bold);
            XFont fBody = new XFont("Arial", 7.5, XFontStyleEx.Regular);
            XFont fFoot = new XFont("Arial", 7, XFontStyleEx.Regular);

            XPen gridPen = new XPen(XColors.Gray, 0.3);
            XPen groupPen = new XPen(XColors.Black, 0.8);   // เส้นหนาคั่นเมื่อเวลาเปลี่ยน
            XBrush headBg = new XSolidBrush(XColor.FromArgb(221, 221, 221));
            XBrush altBg = new XSolidBrush(XColor.FromArgb(245, 245, 245));

            PdfDocument pdf = new PdfDocument();
            pdf.Info.Title = "Daily Sensor Detail Report";

            var pages = new List<PdfPage>();
            int index = 0;

            while (index < rows.Count || pages.Count == 0)
            {
                PdfPage page = pdf.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                page.Orientation = PdfSharp.PageOrientation.Portrait;

                double pageW = page.Width.Point;
                double pageH = page.Height.Point;
                double tableW = pageW - margin * 2;
                double totalWeight = weights.Sum();
                double[] colW = weights.Select(w => tableW * w / totalWeight).ToArray();
                double bottom = pageH - margin - footerH;

                using (XGraphics gfx = XGraphics.FromPdfPage(page))
                {
                    double y = margin;

                    // ---- Title (เฉพาะหน้าแรก) ----
                    if (pages.Count == 0)
                    {
                        gfx.DrawString("Daily Sensor Detail Report", fTitle, XBrushes.Black,
                            new XRect(margin, y, tableW, 20), XStringFormats.TopCenter);
                        gfx.DrawString("Report Date : " + reportDate, fSub, XBrushes.Black,
                            new XRect(margin, y + 22, tableW, 12), XStringFormats.TopLeft);
                        y += titleH;
                    }

                    // ---- Header (ทุกหน้า) ----
                    double x = margin;
                    for (int c = 0; c < headers.Length; c++)
                    {
                        var rect = new XRect(x, y, colW[c], headerH);
                        gfx.DrawRectangle(gridPen, headBg, rect);
                        gfx.DrawString(headers[c], fHead, XBrushes.Black, rect, XStringFormats.Center);
                        x += colW[c];
                    }
                    y += headerH;

                    // ---- Rows ----
                    int rowOnPage = 0;
                    string prevTime = null;

                    while (index < rows.Count && y + rowH <= bottom)
                    {
                        SensorRow r = rows[index];
                        string[] cells =
                        {
                    r.Silo, r.Time, r.Sensor,
                    r.C1.ToString("N1"), r.C2.ToString("N1"), r.C3.ToString("N1"), r.C4.ToString("N1"),
                    r.C5.ToString("N1"), r.C6.ToString("N1"), r.C7.ToString("N1"), r.C8.ToString("N1")
                };

                        if (rowOnPage % 2 == 1)
                            gfx.DrawRectangle(altBg, margin, y, tableW, rowH);

                        x = margin;
                        for (int c = 0; c < cells.Length; c++)
                        {
                            var rect = new XRect(x, y, colW[c], rowH);
                            gfx.DrawRectangle(gridPen, rect);
                            gfx.DrawString(cells[c] ?? "", fBody, XBrushes.Black,
                                new XRect(rect.X + 2, rect.Y, rect.Width - 4, rect.Height),
                                c < 3 ? XStringFormats.CenterLeft : XStringFormats.CenterRight);
                            x += colW[c];
                        }

                        if (prevTime != null && prevTime != r.Time)
                            gfx.DrawLine(groupPen, margin, y, margin + tableW, y);

                        prevTime = r.Time;
                        y += rowH;
                        rowOnPage++;
                        index++;
                    }
                }

                pages.Add(page);
            }

            // ---- Footer: เลขหน้า (ต้องรู้จำนวนหน้าทั้งหมดก่อน จึงวาดทีหลัง) ----
            for (int p = 0; p < pages.Count; p++)
            {
                PdfPage page = pages[p];
                using (XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
                {
                    double pageW = page.Width.Point;
                    double pageH = page.Height.Point;
                    gfx.DrawString($"Page {p + 1} / {pages.Count}", fFoot, XBrushes.Gray,
                        new XRect(margin, pageH - margin - 10, pageW - margin * 2, 10),
                        XStringFormats.CenterRight);
                }
            }

            pdf.Save(filename);
        }
        // ================= Export CSV =================

        private async void ExportCSV_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Filter = "CSV files (*.csv)|*.csv";
            dlg.FileName = "Report.csv";

            if (dlg.ShowDialog() == true)
            {
                var siloData = dgSilo.ItemsSource as List<SiloSummary>;

                if (siloData != null)
                {
                    await ShowLoadingAsync("กำลัง Export CSV...");
                    try
                    {
                        ExportAllDataToCsv(dlg.FileName);
                    }
                    finally
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                    }
                    MessageBox.Show("Export CSV Complete");
                }
            }
        }

        private void ExportAllDataToCsv(string filePath)
        {
            using (StreamWriter sw = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                sw.WriteLine("--- Silo Summary ---");
                WriteListToCsv(sw, dgSilo.ItemsSource as List<SiloSummary>);
                sw.WriteLine();

                sw.WriteLine("--- Weather Summary ---");
                WriteListToCsv(sw, dgWeather.ItemsSource as List<WeatherSummary>);
                sw.WriteLine();

                sw.WriteLine("--- Silo Weight ---");
                WriteListToCsv(sw, dgLevel.ItemsSource as List<SiloLevel>);
                sw.WriteLine();

                sw.WriteLine("--- Fan Operation ---");
                WriteListToCsv(sw, dgFan.ItemsSource as List<FanOperation>);
                sw.WriteLine();

                // ใช้ _detailList โดยตรง (ไม่ต้องรอผูกเข้า DataGrid)
                sw.WriteLine("--- Detail Report ---");
                WriteDetailCsv(sw, _detailList);
            }
        }

        // เขียน Detail แบบตรง ๆ (ไม่ใช้ reflection ทีละช่อง) -> เร็วกว่ามากเมื่อมีหลายหมื่นแถว
        private static void WriteDetailCsv(StreamWriter sw, List<SensorRow> list)
        {
            if (list == null) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            sw.WriteLine("Silo,Time,Sensor,C1,C2,C3,C4,C5,C6,C7,C8");
            StringBuilder sb = new StringBuilder(128);
            foreach (SensorRow r in list)
            {
                sb.Clear();
                sb.Append(r.Silo).Append(',').Append(r.Time).Append(',').Append(r.Sensor);
                sb.Append(',').Append(r.C1.ToString("0.##", ci));
                sb.Append(',').Append(r.C2.ToString("0.##", ci));
                sb.Append(',').Append(r.C3.ToString("0.##", ci));
                sb.Append(',').Append(r.C4.ToString("0.##", ci));
                sb.Append(',').Append(r.C5.ToString("0.##", ci));
                sb.Append(',').Append(r.C6.ToString("0.##", ci));
                sb.Append(',').Append(r.C7.ToString("0.##", ci));
                sb.Append(',').Append(r.C8.ToString("0.##", ci));
                sw.WriteLine(sb.ToString());
            }
        }

        private void WriteListToCsv<T>(StreamWriter sw, IEnumerable<T> list)
        {
            if (list == null) return;

            var properties = typeof(T).GetProperties();

            sw.WriteLine(string.Join(",", properties.Select(p => p.Name)));

            foreach (var item in list)
            {
                var values = properties.Select(p =>
                {
                    var val = p.GetValue(item, null);
                    return $"\"{val?.ToString().Replace("\"", "\"\"")}\"";
                });
                sw.WriteLine(string.Join(",", values));
            }
        }

        private void dgFan_Loaded(object sender, RoutedEventArgs e)
        {
        }
    }
}
