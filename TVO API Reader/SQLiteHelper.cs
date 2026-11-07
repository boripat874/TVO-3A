using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using TVO_API_Reader;

class SQLiteHelper
{
    public static string dbPath = "TVOData.db";

    // เดิมเป็น static readonly ที่คำนวณตอนโหลด class -> ได้ "TVOData.db" (path สัมพัทธ์) เสมอ
    // แม้จะตั้ง dbPath เป็น path เต็มทีหลัง ทำให้ DB ไปอยู่ตาม working directory
    // ตอนนี้คำนวณใหม่ทุกครั้งจาก dbPath ปัจจุบัน
    private static string connectionString
    {
        get { return "Data Source=" + dbPath + ";Version=3;Journal Mode=Wal;Synchronous=Normal;"; }
    }

    /// <summary>
    /// สร้าง index สำหรับรายงาน (Query ตามช่วงเวลา/ไซโล) - ครั้งแรกบน DB ใหญ่อาจใช้เวลาสักครู่
    /// </summary>
    public static void EnsureIndexes()
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();
            string sql = @"
CREATE INDEX IF NOT EXISTS IX_SensorConfig_LogTime ON SensorConfig(LogTime);
CREATE INDEX IF NOT EXISTS IX_SensorConfig_Silo_Time ON SensorConfig(Siloname, SensorType, LogTime);
CREATE INDEX IF NOT EXISTS IX_FanLog_LogTime ON FanLog(LogTime);
CREATE INDEX IF NOT EXISTS IX_AMBLog_LogTime ON AMBLog(LogTime);";
            using (SQLiteCommand cmd = new SQLiteCommand(sql, conn))
            {
                cmd.CommandTimeout = 600;
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ================= EventLog (Monitor Log) =================

    public static void InitializeEventLog()
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();
            string sql = @"
CREATE TABLE IF NOT EXISTS EventLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    Category TEXT,
    Level TEXT,
    Source TEXT,
    Event TEXT,
    Detail TEXT,
    DurationSec INTEGER
);
CREATE INDEX IF NOT EXISTS IX_EventLog_LogTime ON EventLog(LogTime);";
            using (SQLiteCommand cmd = new SQLiteCommand(sql, conn))
                cmd.ExecuteNonQuery();
        }
    }

    public static void insertEvent(DateTime time, string category, string level, string source,
                                   string evt, string detail, long? durationSec)
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();
            string sql = @"
INSERT INTO EventLog (LogTime, Category, Level, Source, Event, Detail, DurationSec)
VALUES (@LogTime, @Category, @Level, @Source, @Event, @Detail, @DurationSec);";
            using (SQLiteCommand cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@LogTime", time);
                cmd.Parameters.AddWithValue("@Category", category);
                cmd.Parameters.AddWithValue("@Level", level);
                cmd.Parameters.AddWithValue("@Source", source ?? "");
                cmd.Parameters.AddWithValue("@Event", evt);
                cmd.Parameters.AddWithValue("@Detail", detail ?? "");
                cmd.Parameters.AddWithValue("@DurationSec", durationSec.HasValue ? (object)durationSec.Value : DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>ลบ log ที่เก่ากว่า keepDays วัน</summary>
    public static int purgeEventLog(int keepDays)
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();
            using (SQLiteCommand cmd = new SQLiteCommand("DELETE FROM EventLog WHERE LogTime < @Limit;", conn))
            {
                cmd.Parameters.AddWithValue("@Limit", DateTime.Now.AddDays(-keepDays));
                return cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// ดูว่าการเปิดโปรแกรมครั้งก่อนจบแบบปกติหรือไม่
    /// คืน null ถ้าปกติ หรือคืนข้อความรายละเอียดถ้าครั้งก่อนไม่มี APP_STOP
    /// </summary>
    public static string checkLastSession()
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();
            DateTime? lastStart = null;
            using (SQLiteCommand cmd = new SQLiteCommand(@"
SELECT Event, LogTime FROM EventLog
WHERE Category = 'SYSTEM' AND Event IN ('APP_START','APP_STOP')
ORDER BY LogTime DESC, ID DESC LIMIT 1;", conn))
            using (SQLiteDataReader rd = cmd.ExecuteReader())
            {
                if (!rd.Read()) return null;                 // ยังไม่เคยมี log
                if (rd.GetString(0) == "APP_STOP") return null;
                try { lastStart = rd.GetDateTime(1); } catch { }
            }

            // เวลาที่ยังทำงานอยู่ล่าสุด = เวลาบันทึกข้อมูลล่าสุดหลังจากเปิดครั้งก่อน
            object lastData = null;
            try
            {
                using (SQLiteCommand cmd = new SQLiteCommand("SELECT MAX(LogTime) FROM AMBLog;", conn))
                    lastData = cmd.ExecuteScalar();
            }
            catch { }

            string detail = "ครั้งก่อนไม่ได้ปิดโปรแกรมตามปกติ";
            if (lastStart.HasValue) detail += " (เปิดเมื่อ " + lastStart.Value.ToString("dd/MM/yyyy HH:mm:ss") + ")";
            DateTime lastDt;
            if (lastData != null && lastData != DBNull.Value && DateTime.TryParse(lastData.ToString(), out lastDt))
                detail += " บันทึกข้อมูลล่าสุด " + lastDt.ToString("dd/MM/yyyy HH:mm:ss");
            return detail;
        }
    }

    /// <summary>
    /// คำนวณ Avg/Min/Max โดยไม่นับค่า 0 (0 = อ่านไม่ได้ หรือข้อมูลเก่าเกิน lateHrData)
    /// ถ้าไม่มีค่าที่ใช้ได้เลยจะคืน DBNull เพื่อไม่ให้ค่า 0 ไปดึง Min/Avg ในรายงาน
    /// </summary>
    private static void SetStats(SQLiteCommand cmd, decimal[] values, int count)
    {
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        int n = 0;
        int len = values == null ? 0 : Math.Min(count, values.Length);
        for (int i = 0; i < len; i++)
        {
            if (values[i] == 0m) continue;
            double v = (double)values[i];
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
            n++;
        }

        if (n == 0)
        {
            cmd.Parameters["@SAVG"].Value = DBNull.Value;
            cmd.Parameters["@Smin"].Value = DBNull.Value;
            cmd.Parameters["@Smax"].Value = DBNull.Value;
        }
        else
        {
            cmd.Parameters["@SAVG"].Value = sum / n;
            cmd.Parameters["@Smin"].Value = min;
            cmd.Parameters["@Smax"].Value = max;
        }
    }

    public static void InitializeDatabaseEX3A()
    {
        // สร้าง Database ถ้ายังไม่มี
        if (!File.Exists(dbPath))
        {
            SQLiteConnection.CreateFile(dbPath);
        }

        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            // สร้างตารางถ้ายังไม่มี
            string createTable = @"
CREATE TABLE IF NOT EXISTS SensorConfig
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    Siloname TEXT NOT NULL,
    SensorType INTEGER,
    SensorLine INTEGER,

    S1 REAL,
    S2 REAL,
    S3 REAL,
    S4 REAL,
    S5 REAL,
    S6 REAL,
    S7 REAL,
    S8 REAL,
    S9 REAL,
    S10 REAL,
    S11 REAL,
    S12 REAL,
    SLevel REAL,
    SAVG REAL,
    Smin REAL,
    Smax REAL
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createFanLogTable = @"
CREATE TABLE IF NOT EXISTS FanLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    FanName TEXT,
    FanEvent TEXT,
    FanRemark TEXT
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createFanLogTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createAMBLogTable = @"
CREATE TABLE IF NOT EXISTS AMBLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    AMBTemp REAL,
    AMBHum REAL
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createAMBLogTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            conn.Close();
        }
    }

    public static void insertLogEX3A()
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            using (SQLiteTransaction tran = conn.BeginTransaction())
            {
                string sql = @"
        INSERT INTO SensorConfig
        (
            LogTime,
            Siloname,
            SensorType,
            SensorLine,
            S1,S2,S3,S4,S5,S6,S7,S8,
            SLevel,
            SAVG,Smin,Smax
        )
        VALUES
        (
            @LogTime,
            @Siloname,
            @SensorType,
            @SensorLine,
            @S1,@S2,@S3,@S4,@S5,@S6,@S7,@S8,
            @SLevel,
            @SAVG,@Smin,@Smax
        );";

                using (SQLiteCommand cmd = new SQLiteCommand(sql, conn, tran))
                {
                    // สร้าง Parameter ครั้งเดียว
                    cmd.Parameters.Add("@LogTime", DbType.DateTime);
                    cmd.Parameters.Add("@Siloname", DbType.String);
                    cmd.Parameters.Add("@SensorType", DbType.Int32);
                    cmd.Parameters.Add("@SensorLine", DbType.Int32);

                    cmd.Parameters.Add("@S1", DbType.Double);
                    cmd.Parameters.Add("@S2", DbType.Double);
                    cmd.Parameters.Add("@S3", DbType.Double);
                    cmd.Parameters.Add("@S4", DbType.Double);
                    cmd.Parameters.Add("@S5", DbType.Double);
                    cmd.Parameters.Add("@S6", DbType.Double);
                    cmd.Parameters.Add("@S7", DbType.Double);
                    cmd.Parameters.Add("@S8", DbType.Double);
                    cmd.Parameters.Add("@SLevel", DbType.Double);
                    cmd.Parameters.Add("@SAVG", DbType.Double);
                    cmd.Parameters.Add("@Smin", DbType.Double);
                    cmd.Parameters.Add("@Smax", DbType.Double);

                    foreach (silodata item in MainUI._dicSiloData.Values)
                    {
                        double Weight = 0;
                        try
                        {
                            Weight = (double)item.cropWeight;
                        }
                        catch { }

                        //Line1
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 1;

                        cmd.Parameters["@S1"].Value = item.line1[0];
                        cmd.Parameters["@S2"].Value = item.line1[1];
                        cmd.Parameters["@S3"].Value = item.line1[2];
                        cmd.Parameters["@S4"].Value = item.line1[3];
                        cmd.Parameters["@S5"].Value = item.line1[4];
                        cmd.Parameters["@S6"].Value = item.line1[5];
                        cmd.Parameters["@S7"].Value = item.line1[6];
                        cmd.Parameters["@S8"].Value = item.line1[7];

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line1.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.line1.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.line1.Take(8).Max();

                        cmd.ExecuteNonQuery();

                        //line2
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 2;

                        cmd.Parameters["@S1"].Value = item.line2[0];
                        cmd.Parameters["@S2"].Value = item.line2[1];
                        cmd.Parameters["@S3"].Value = item.line2[2];
                        cmd.Parameters["@S4"].Value = item.line2[3];
                        cmd.Parameters["@S5"].Value = item.line2[4];
                        cmd.Parameters["@S6"].Value = item.line2[5];
                        cmd.Parameters["@S7"].Value = item.line2[6];
                        cmd.Parameters["@S8"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line2.Take(7).Average();
                        cmd.Parameters["@Smin"].Value = item.line2.Take(7).Min();
                        cmd.Parameters["@Smax"].Value = item.line2.Take(7).Max();

                        cmd.ExecuteNonQuery();

                        //line3
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 3;

                        cmd.Parameters["@S1"].Value = item.line3[0];
                        cmd.Parameters["@S2"].Value = item.line3[1];
                        cmd.Parameters["@S3"].Value = item.line3[2];
                        cmd.Parameters["@S4"].Value = item.line3[3];
                        cmd.Parameters["@S5"].Value = item.line3[4];
                        cmd.Parameters["@S6"].Value = item.line3[5];
                        cmd.Parameters["@S7"].Value = item.line3[6];
                        cmd.Parameters["@S8"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line3.Take(7).Average();
                        cmd.Parameters["@Smin"].Value = item.line3.Take(7).Min();
                        cmd.Parameters["@Smax"].Value = item.line3.Take(7).Max();

                        cmd.ExecuteNonQuery();

                        //line4
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 4;

                        cmd.Parameters["@S1"].Value = item.line4[0];
                        cmd.Parameters["@S2"].Value = item.line4[1];
                        cmd.Parameters["@S3"].Value = item.line4[2];
                        cmd.Parameters["@S4"].Value = item.line4[3];
                        cmd.Parameters["@S5"].Value = item.line4[4];
                        cmd.Parameters["@S6"].Value = item.line4[5];
                        cmd.Parameters["@S7"].Value = item.line4[6];
                        cmd.Parameters["@S8"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line4.Take(7).Average();
                        cmd.Parameters["@Smin"].Value = item.line4.Take(7).Min();
                        cmd.Parameters["@Smax"].Value = item.line4.Take(7).Max();

                        cmd.ExecuteNonQuery();

                        //moisture
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 1;
                        cmd.Parameters["@SensorLine"].Value = 1;

                        cmd.Parameters["@S1"].Value = item.mois[0];
                        cmd.Parameters["@S2"].Value = item.mois[1];
                        cmd.Parameters["@S3"].Value = item.mois[2];
                        cmd.Parameters["@S4"].Value = item.mois[3];
                        cmd.Parameters["@S5"].Value = item.mois[4];
                        cmd.Parameters["@S6"].Value = item.mois[5];
                        cmd.Parameters["@S7"].Value = item.mois[6];
                        cmd.Parameters["@S8"].Value = item.mois[7];

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.mois.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.mois.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.mois.Take(8).Max();

                        cmd.ExecuteNonQuery();
                    }
                }

                tran.Commit();
            }
        }
    }

    public static void InitializeDatabaseEX3B()
    {
        // สร้าง Database ถ้ายังไม่มี
        if (!File.Exists(dbPath))
        {
            SQLiteConnection.CreateFile(dbPath);
        }

        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            // สร้างตารางถ้ายังไม่มี
            string createTable = @"
CREATE TABLE IF NOT EXISTS SensorConfig
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    Siloname TEXT NOT NULL,
    SensorType INTEGER,
    SensorLine INTEGER,

    S1 REAL,
    S2 REAL,
    S3 REAL,
    S4 REAL,
    S5 REAL,
    S6 REAL,
    S7 REAL,
    S8 REAL,
    S9 REAL,
    S10 REAL,
    S11 REAL,
    S12 REAL,
    SLevel REAL,
    SAVG REAL,
    Smin REAL,
    Smax REAL
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createFanLogTable = @"
CREATE TABLE IF NOT EXISTS FanLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    FanName TEXT,
    FanEvent TEXT,
    FanRemark TEXT
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createFanLogTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createAMBLogTable = @"
CREATE TABLE IF NOT EXISTS AMBLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    AMBTemp REAL,
    AMBHum REAL
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createAMBLogTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            conn.Close();
        }
    }

    public static void insertLogEX3B()
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            using (SQLiteTransaction tran = conn.BeginTransaction())
            {
                string sql = @"
        INSERT INTO SensorConfig
        (
            LogTime,
            Siloname,
            SensorType,
            SensorLine,
            S1,S2,S3,S4,S5,S6,S7,S8,
            SLevel,
            SAVG,Smin,Smax
        )
        VALUES
        (
            @LogTime,
            @Siloname,
            @SensorType,
            @SensorLine,
            @S1,@S2,@S3,@S4,@S5,@S6,@S7,@S8,
            @SLevel,
            @SAVG,@Smin,@Smax
        );";

                using (SQLiteCommand cmd = new SQLiteCommand(sql, conn, tran))
                {
                    // สร้าง Parameter ครั้งเดียว
                    cmd.Parameters.Add("@LogTime", DbType.DateTime);
                    cmd.Parameters.Add("@Siloname", DbType.String);
                    cmd.Parameters.Add("@SensorType", DbType.Int32);
                    cmd.Parameters.Add("@SensorLine", DbType.Int32);

                    cmd.Parameters.Add("@S1", DbType.Double);
                    cmd.Parameters.Add("@S2", DbType.Double);
                    cmd.Parameters.Add("@S3", DbType.Double);
                    cmd.Parameters.Add("@S4", DbType.Double);
                    cmd.Parameters.Add("@S5", DbType.Double);
                    cmd.Parameters.Add("@S6", DbType.Double);
                    cmd.Parameters.Add("@S7", DbType.Double);
                    cmd.Parameters.Add("@S8", DbType.Double);
                    cmd.Parameters.Add("@SLevel", DbType.Double);
                    cmd.Parameters.Add("@SAVG", DbType.Double);
                    cmd.Parameters.Add("@Smin", DbType.Double);
                    cmd.Parameters.Add("@Smax", DbType.Double);

                    foreach (silodata item in MainUI._dicSiloData.Values)
                    {
                        double Weight = 0;
                        try
                        {
                            Weight = (double)item.cropWeight;
                        }
                        catch { }
                        //Line1
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 1;

                        cmd.Parameters["@S1"].Value = item.line1[0];
                        cmd.Parameters["@S2"].Value = item.line1[1];
                        cmd.Parameters["@S3"].Value = item.line1[2];
                        cmd.Parameters["@S4"].Value = item.line1[3];
                        cmd.Parameters["@S5"].Value = item.line1[4];
                        cmd.Parameters["@S6"].Value = item.line1[5];
                        cmd.Parameters["@S7"].Value = item.line1[6];
                        cmd.Parameters["@S8"].Value = item.line1[7];

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line1.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.line1.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.line1.Take(8).Max();

                        cmd.ExecuteNonQuery();

                        //line2
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 2;

                        cmd.Parameters["@S1"].Value = item.line2[0];
                        cmd.Parameters["@S2"].Value = item.line2[1];
                        cmd.Parameters["@S3"].Value = item.line2[2];
                        cmd.Parameters["@S4"].Value = item.line2[3];
                        cmd.Parameters["@S5"].Value = item.line2[4];
                        cmd.Parameters["@S6"].Value = item.line2[5];
                        cmd.Parameters["@S7"].Value = item.line2[6];
                        cmd.Parameters["@S8"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line2.Take(7).Average();
                        cmd.Parameters["@Smin"].Value = item.line2.Take(7).Min();
                        cmd.Parameters["@Smax"].Value = item.line2.Take(7).Max();

                        cmd.ExecuteNonQuery();

                        //line3
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 3;

                        cmd.Parameters["@S1"].Value = item.line3[0];
                        cmd.Parameters["@S2"].Value = item.line3[1];
                        cmd.Parameters["@S3"].Value = item.line3[2];
                        cmd.Parameters["@S4"].Value = item.line3[3];
                        cmd.Parameters["@S5"].Value = item.line3[4];
                        cmd.Parameters["@S6"].Value = item.line3[5];
                        cmd.Parameters["@S7"].Value = item.line3[6];
                        cmd.Parameters["@S8"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line3.Take(7).Average();
                        cmd.Parameters["@Smin"].Value = item.line3.Take(7).Min();
                        cmd.Parameters["@Smax"].Value = item.line3.Take(7).Max();

                        cmd.ExecuteNonQuery();

                        //line4
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 4;

                        cmd.Parameters["@S1"].Value = item.line4[0];
                        cmd.Parameters["@S2"].Value = item.line4[1];
                        cmd.Parameters["@S3"].Value = item.line4[2];
                        cmd.Parameters["@S4"].Value = item.line4[3];
                        cmd.Parameters["@S5"].Value = item.line4[4];
                        cmd.Parameters["@S6"].Value = item.line4[5];
                        cmd.Parameters["@S7"].Value = item.line4[6];
                        cmd.Parameters["@S8"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line4.Take(7).Average();
                        cmd.Parameters["@Smin"].Value = item.line4.Take(7).Min();
                        cmd.Parameters["@Smax"].Value = item.line4.Take(7).Max();

                        cmd.ExecuteNonQuery();

                        //line5
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 5;

                        cmd.Parameters["@S1"].Value = item.line5[0];
                        cmd.Parameters["@S2"].Value = item.line5[1];
                        cmd.Parameters["@S3"].Value = item.line5[2];
                        cmd.Parameters["@S4"].Value = item.line5[3];
                        cmd.Parameters["@S5"].Value = item.line5[4];
                        cmd.Parameters["@S6"].Value = item.line5[5];
                        cmd.Parameters["@S7"].Value = item.line5[6];
                        cmd.Parameters["@S8"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line5.Take(7).Average();
                        cmd.Parameters["@Smin"].Value = item.line5.Take(7).Min();
                        cmd.Parameters["@Smax"].Value = item.line5.Take(7).Max();

                        cmd.ExecuteNonQuery();

                        //line6
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 6;

                        cmd.Parameters["@S1"].Value = item.line6[0];
                        cmd.Parameters["@S2"].Value = item.line6[1];
                        cmd.Parameters["@S3"].Value = item.line6[2];
                        cmd.Parameters["@S4"].Value = item.line6[3];
                        cmd.Parameters["@S5"].Value = item.line6[4];
                        cmd.Parameters["@S6"].Value = item.line6[5];
                        cmd.Parameters["@S7"].Value = item.line6[6];
                        cmd.Parameters["@S8"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line6.Take(7).Average();
                        cmd.Parameters["@Smin"].Value = item.line6.Take(7).Min();
                        cmd.Parameters["@Smax"].Value = item.line6.Take(7).Max();

                        cmd.ExecuteNonQuery();

                        //moisture
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 1;
                        cmd.Parameters["@SensorLine"].Value = 1;

                        cmd.Parameters["@S1"].Value = item.mois[0];
                        cmd.Parameters["@S2"].Value = item.mois[1];
                        cmd.Parameters["@S3"].Value = item.mois[2];
                        cmd.Parameters["@S4"].Value = item.mois[3];
                        cmd.Parameters["@S5"].Value = item.mois[4];
                        cmd.Parameters["@S6"].Value = item.mois[5];
                        cmd.Parameters["@S7"].Value = item.mois[6];
                        cmd.Parameters["@S8"].Value = item.mois[7];

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.mois.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.mois.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.mois.Take(8).Max();

                        cmd.ExecuteNonQuery();
                    }
                }

                tran.Commit();
            }
        }
    }

    public static void InitializeDatabase1B()
    {
        // สร้าง Database ถ้ายังไม่มี
        if (!File.Exists(dbPath))
        {
            SQLiteConnection.CreateFile(dbPath);
        }

        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            // สร้างตารางถ้ายังไม่มี
            string createTable = @"
CREATE TABLE IF NOT EXISTS SensorConfig
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    Siloname TEXT NOT NULL,
    SensorType INTEGER,
    SensorLine INTEGER,

    S1 REAL,
    S2 REAL,
    S3 REAL,
    S4 REAL,
    S5 REAL,
    S6 REAL,
    S7 REAL,
    S8 REAL,
    S9 REAL,
    S10 REAL,
    S11 REAL,
    S12 REAL,
    SLevel REAL,
    SAVG REAL,
    Smin REAL,
    Smax REAL
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createFanLogTable = @"
CREATE TABLE IF NOT EXISTS FanLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    FanName TEXT,
    FanEvent TEXT,
    FanRemark TEXT
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createFanLogTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createAMBLogTable = @"
CREATE TABLE IF NOT EXISTS AMBLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    AMBTemp REAL,
    AMBHum REAL
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createAMBLogTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            conn.Close();
        }
    }

    public static void insertLog1B()
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            using (SQLiteTransaction tran = conn.BeginTransaction())
            {
                string sql = @"
        INSERT INTO SensorConfig
        (
            LogTime,
            Siloname,
            SensorType,
            SensorLine,
            S1,S2,S3,S4,S5,S6,S7,S8,
            SLevel,
            SAVG,Smin,Smax
        )
        VALUES
        (
            @LogTime,
            @Siloname,
            @SensorType,
            @SensorLine,
            @S1,@S2,@S3,@S4,@S5,@S6,@S7,@S8,@S9
            @SLevel,
            @SAVG,@Smin,@Smax
        );";

                using (SQLiteCommand cmd = new SQLiteCommand(sql, conn, tran))
                {
                    // สร้าง Parameter ครั้งเดียว
                    cmd.Parameters.Add("@LogTime", DbType.DateTime);
                    cmd.Parameters.Add("@Siloname", DbType.String);
                    cmd.Parameters.Add("@SensorType", DbType.Int32);
                    cmd.Parameters.Add("@SensorLine", DbType.Int32);

                    cmd.Parameters.Add("@S1", DbType.Double);
                    cmd.Parameters.Add("@S2", DbType.Double);
                    cmd.Parameters.Add("@S3", DbType.Double);
                    cmd.Parameters.Add("@S4", DbType.Double);
                    cmd.Parameters.Add("@S5", DbType.Double);
                    cmd.Parameters.Add("@S6", DbType.Double);
                    cmd.Parameters.Add("@S7", DbType.Double);
                    cmd.Parameters.Add("@S8", DbType.Double);
                    cmd.Parameters.Add("@S9", DbType.Double);
                    cmd.Parameters.Add("@SLevel", DbType.Double);
                    cmd.Parameters.Add("@SAVG", DbType.Double);
                    cmd.Parameters.Add("@Smin", DbType.Double);
                    cmd.Parameters.Add("@Smax", DbType.Double);

                    foreach (silodata item in MainUI._dicSiloData.Values)
                    {
                        double Weight = 0;
                        try
                        {
                            Weight = (double)item.cropWeight;
                        }
                        catch { }
                        //Line1
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 1;

                        cmd.Parameters["@S1"].Value = item.line1[0];
                        cmd.Parameters["@S2"].Value = item.line1[1];
                        cmd.Parameters["@S3"].Value = item.line1[2];
                        cmd.Parameters["@S4"].Value = item.line1[3];
                        cmd.Parameters["@S5"].Value = item.line1[4];
                        cmd.Parameters["@S6"].Value = item.line1[5];
                        cmd.Parameters["@S7"].Value = item.line1[6];
                        cmd.Parameters["@S8"].Value = item.line1[7];
                        cmd.Parameters["@S9"].Value = item.line1[8];

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line1.Take(9).Average();
                        cmd.Parameters["@Smin"].Value = item.line1.Take(9).Min();
                        cmd.Parameters["@Smax"].Value = item.line1.Take(9).Max();

                        cmd.ExecuteNonQuery();

                        //line2
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 2;

                        cmd.Parameters["@S1"].Value = item.line2[0];
                        cmd.Parameters["@S2"].Value = item.line2[1];
                        cmd.Parameters["@S3"].Value = item.line2[2];
                        cmd.Parameters["@S4"].Value = item.line2[3];
                        cmd.Parameters["@S5"].Value = item.line2[4];
                        cmd.Parameters["@S6"].Value = item.line2[5];
                        cmd.Parameters["@S7"].Value = item.line2[6];
                        cmd.Parameters["@S8"].Value = item.line2[7];
                        cmd.Parameters["@S9"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line2.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.line2.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.line2.Take(8).Max();

                        cmd.ExecuteNonQuery();

                        //line3
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 3;

                        cmd.Parameters["@S1"].Value = item.line3[0];
                        cmd.Parameters["@S2"].Value = item.line3[1];
                        cmd.Parameters["@S3"].Value = item.line3[2];
                        cmd.Parameters["@S4"].Value = item.line3[3];
                        cmd.Parameters["@S5"].Value = item.line3[4];
                        cmd.Parameters["@S6"].Value = item.line3[5];
                        cmd.Parameters["@S7"].Value = item.line3[6];
                        cmd.Parameters["@S8"].Value = item.line3[7];
                        cmd.Parameters["@S9"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line3.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.line3.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.line3.Take(8).Max();

                        cmd.ExecuteNonQuery();

                        //line4
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 4;

                        cmd.Parameters["@S1"].Value = item.line4[0];
                        cmd.Parameters["@S2"].Value = item.line4[1];
                        cmd.Parameters["@S3"].Value = item.line4[2];
                        cmd.Parameters["@S4"].Value = item.line4[3];
                        cmd.Parameters["@S5"].Value = item.line4[4];
                        cmd.Parameters["@S6"].Value = item.line4[5];
                        cmd.Parameters["@S7"].Value = item.line4[6];
                        cmd.Parameters["@S8"].Value = item.line4[7];
                        cmd.Parameters["@S9"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line4.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.line4.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.line4.Take(8).Max();

                        cmd.ExecuteNonQuery();

                        //line5
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 5;

                        cmd.Parameters["@S1"].Value = item.line5[0];
                        cmd.Parameters["@S2"].Value = item.line5[1];
                        cmd.Parameters["@S3"].Value = item.line5[2];
                        cmd.Parameters["@S4"].Value = item.line5[3];
                        cmd.Parameters["@S5"].Value = item.line5[4];
                        cmd.Parameters["@S6"].Value = item.line5[5];
                        cmd.Parameters["@S7"].Value = item.line5[6];
                        cmd.Parameters["@S8"].Value = item.line5[7];
                        cmd.Parameters["@S9"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line5.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.line5.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.line5.Take(8).Max();

                        cmd.ExecuteNonQuery();

                        //line6
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 0;
                        cmd.Parameters["@SensorLine"].Value = 6;

                        cmd.Parameters["@S1"].Value = item.line6[0];
                        cmd.Parameters["@S2"].Value = item.line6[1];
                        cmd.Parameters["@S3"].Value = item.line6[2];
                        cmd.Parameters["@S4"].Value = item.line6[3];
                        cmd.Parameters["@S5"].Value = item.line6[4];
                        cmd.Parameters["@S6"].Value = item.line6[5];
                        cmd.Parameters["@S7"].Value = item.line6[6];
                        cmd.Parameters["@S8"].Value = item.line6[7];
                        cmd.Parameters["@S9"].Value = 0;

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.line6.Take(8).Average();
                        cmd.Parameters["@Smin"].Value = item.line6.Take(8).Min();
                        cmd.Parameters["@Smax"].Value = item.line6.Take(8).Max();

                        cmd.ExecuteNonQuery();

                        //moisture
                        cmd.Parameters["@LogTime"].Value = DateTime.Now;
                        cmd.Parameters["@Siloname"].Value = item.siloname;
                        cmd.Parameters["@SensorType"].Value = 1;
                        cmd.Parameters["@SensorLine"].Value = 1;

                        cmd.Parameters["@S1"].Value = item.mois[0];
                        cmd.Parameters["@S2"].Value = item.mois[1];
                        cmd.Parameters["@S3"].Value = item.mois[2];
                        cmd.Parameters["@S4"].Value = item.mois[3];
                        cmd.Parameters["@S5"].Value = item.mois[4];
                        cmd.Parameters["@S6"].Value = item.mois[5];
                        cmd.Parameters["@S7"].Value = item.mois[6];
                        cmd.Parameters["@S8"].Value = item.mois[7];
                        cmd.Parameters["@S9"].Value = item.mois[8];

                        cmd.Parameters["@SLevel"].Value = Weight;

                        cmd.Parameters["@SAVG"].Value = item.mois.Take(9).Average();
                        cmd.Parameters["@Smin"].Value = item.mois.Take(9).Min();
                        cmd.Parameters["@Smax"].Value = item.mois.Take(9).Max();

                        cmd.ExecuteNonQuery();
                    }
                }

                tran.Commit();
            }
        }
    }

    public static void InitializeDatabase3A()
    {
        // สร้าง Database ถ้ายังไม่มี
        if (!File.Exists(dbPath))
        {
            SQLiteConnection.CreateFile(dbPath);
        }

        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            // สร้างตารางถ้ายังไม่มี
            string createTable = @"
CREATE TABLE IF NOT EXISTS SensorConfig
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    Siloname TEXT NOT NULL,
    SensorType INTEGER,
    SensorLine INTEGER,

    S1 REAL,
    S2 REAL,
    S3 REAL,
    S4 REAL,
    S5 REAL,
    S6 REAL,
    S7 REAL,
    S8 REAL,
    S9 REAL,
    S10 REAL,
    S11 REAL,
    S12 REAL,
    SLevel REAL,
    SAVG REAL,
    Smin REAL,
    Smax REAL
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createFanLogTable = @"
CREATE TABLE IF NOT EXISTS FanLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    FanName TEXT,
    FanEvent TEXT,
    FanRemark TEXT
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createFanLogTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createAMBLogTable = @"
CREATE TABLE IF NOT EXISTS AMBLog
(
    ID INTEGER PRIMARY KEY AUTOINCREMENT,
    LogTime DATETIME NOT NULL,
    AMBTemp REAL,
    AMBHum REAL
);";

            using (SQLiteCommand cmd = new SQLiteCommand(createAMBLogTable, conn))
            {
                cmd.ExecuteNonQuery();
            }

            conn.Close();
        }
    }

    // จำนวนจุดวัดที่บันทึกต่อสาย ของไซต์ 3A (เท่ากับของเดิม: สาย 1 = 8 จุด, สาย 2-7 = 7 จุด, moisture = 8 จุด)
    private static readonly int[] _3A_PointsPerLine = { 8, 7, 7, 7, 7, 7, 7 };
    private const int _3A_MoisPoints = 8;

    public static void insertLog3A()
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            using (SQLiteTransaction tran = conn.BeginTransaction())
            {
                string sql = @"
        INSERT INTO SensorConfig
        (LogTime, Siloname, SensorType, SensorLine,
         S1,S2,S3,S4,S5,S6,S7,S8,
         SLevel, SAVG, Smin, Smax)
        VALUES
        (@LogTime, @Siloname, @SensorType, @SensorLine,
         @S1,@S2,@S3,@S4,@S5,@S6,@S7,@S8,
         @SLevel, @SAVG, @Smin, @Smax);";

                using (SQLiteCommand cmd = new SQLiteCommand(sql, conn, tran))
                {
                    cmd.Parameters.Add("@LogTime", DbType.DateTime);
                    cmd.Parameters.Add("@Siloname", DbType.String);
                    cmd.Parameters.Add("@SensorType", DbType.Int32);
                    cmd.Parameters.Add("@SensorLine", DbType.Int32);
                    string[] sNames = { "@S1", "@S2", "@S3", "@S4", "@S5", "@S6", "@S7", "@S8" };
                    foreach (string n in sNames) cmd.Parameters.Add(n, DbType.Double);
                    cmd.Parameters.Add("@SLevel", DbType.Double);
                    cmd.Parameters.Add("@SAVG", DbType.Double);
                    cmd.Parameters.Add("@Smin", DbType.Double);
                    cmd.Parameters.Add("@Smax", DbType.Double);

                    DateTime now = DateTime.Now;   // เวลาเดียวกันทั้งชุด -> รายงานจัดกลุ่มได้ถูก

                    foreach (silodata item in MainUI._dicSiloData.Values)
                    {
                        double weight = (double)item.cropWeight;

                        // Temp line 1..7 (SensorType 0) + Moisture (SensorType 1)
                        for (int row = 0; row <= _3A_PointsPerLine.Length; row++)
                        {
                            bool isMois = row == _3A_PointsPerLine.Length;
                            decimal[] values = isMois ? item.mois : item.GetLine(row);
                            int count = isMois ? _3A_MoisPoints : _3A_PointsPerLine[row];

                            cmd.Parameters["@LogTime"].Value = now;
                            cmd.Parameters["@Siloname"].Value = item.siloname;
                            cmd.Parameters["@SensorType"].Value = isMois ? 1 : 0;
                            cmd.Parameters["@SensorLine"].Value = isMois ? 1 : row + 1;

                            for (int k = 0; k < sNames.Length; k++)
                                cmd.Parameters[sNames[k]].Value = (k < count && values != null && k < values.Length) ? (double)values[k] : 0d;

                            cmd.Parameters["@SLevel"].Value = weight;
                            SetStats(cmd, values, count);

                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                tran.Commit();
            }
        }
    }

    public static void insertFanLog(fanData pFandata)
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();
            string sql = @"
INSERT INTO FanLog
(
    LogTime,
    FanName,
    FanEvent,
    FanRemark
)
VALUES
(
    @LogTime,
    @FanName,
    @FanEvent,
    @FanRemark
);";

            using (SQLiteCommand cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@LogTime", DateTime.Now);
                cmd.Parameters.AddWithValue("@FanName", pFandata.FanName);
                cmd.Parameters.AddWithValue("@FanEvent", pFandata.statusTxt);
                cmd.Parameters.AddWithValue("@FanRemark", "");

                cmd.ExecuteNonQuery();
            }
        }
    }
    
    public static void insertAMB()
    {
        using (SQLiteConnection conn = new SQLiteConnection(connectionString))
        {
            conn.Open();

            string sql = @"
    INSERT INTO AMBLog
    (
        LogTime,
        AMBTemp,
        AMBHum
    )
    VALUES
    (
        @LogTime,
        @AMBTemp,
        @AMBHum
    );";

            using (SQLiteCommand cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@LogTime", DateTime.Now);
                cmd.Parameters.AddWithValue("@AMBTemp", MainUI._wether.temperature);
                cmd.Parameters.AddWithValue("@AMBHum", MainUI._wether.humidity);

                cmd.ExecuteNonQuery();
            }
        }
    }

}