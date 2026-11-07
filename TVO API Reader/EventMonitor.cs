using System;
using System.Collections.Generic;
using System.Text;

namespace TVO_API_Reader
{
    /// <summary>
    /// บันทึก EventLog เฉพาะตอน "เริ่มมีปัญหา" และ "กลับมาใช้งานได้"
    /// เก็บสถานะของแต่ละจุดไว้ในหน่วยความจำ แล้วบันทึกเมื่อสถานะเปลี่ยนเท่านั้น
    /// (ไม่บันทึกซ้ำทุกรอบระหว่างที่ปัญหายังอยู่)
    /// </summary>
    internal static class EventMonitor
    {
        public const string CAT_SYSTEM = "SYSTEM";
        public const string CAT_API = "API";
        public const string CAT_PLC = "PLC";
        public const string CAT_DATA = "DATA";

        public const string LV_INFO = "INFO";
        public const string LV_WARN = "WARN";
        public const string LV_ERROR = "ERROR";

        // จำนวนจุดวัดจริง: สาย 1 = 8 จุด, สาย 2 ขึ้นไป = 7 จุด, moisture = 8 จุด
        // (ตรงกับจำนวนที่บันทึกใน SensorConfig) จุดที่ไม่มีเซนเซอร์จะไม่ถูกตรวจ
        public static int TempPoints(int lineIndex) { return lineIndex == 0 ? 8 : 7; }
        public const int MoisPoints = 8;

        /// <summary>ส่งข้อความไปหน้าต่าง log ของโปรแกรม (ตั้งจาก MainUI)</summary>
        public static Action<string> UiLog;

        private class State
        {
            public bool Bad;
            public DateTime Since;
            public string Token;   // ใช้เทียบว่าปัญหา "เปลี่ยน" หรือไม่ (เช่นรายการจุดที่เป็น 0)
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly object _lock = new object();

        // ================= พื้นฐาน =================

        public static void Write(string category, string level, string source, string evt, string detail, long? durationSec)
        {
            try
            {
                SQLiteHelper.insertEvent(DateTime.Now, category, level, source, evt, detail, durationSec);
            }
            catch (Exception ex)
            {
                if (UiLog != null) UiLog("EventLog write fail: " + ex.Message);
            }

            if (UiLog != null)
            {
                string d = string.IsNullOrEmpty(detail) ? "" : " : " + detail;
                string dur = durationSec.HasValue ? " (" + FormatDuration(durationSec.Value) + ")" : "";
                UiLog("[" + level + "] " + evt + " " + source + d + dur);
            }
        }

        public static string FormatDuration(long sec)
        {
            TimeSpan t = TimeSpan.FromSeconds(sec);
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        /// <summary>
        /// แจ้งว่ามีปัญหา: บันทึกครั้งแรกที่เกิด
        /// ถ้ายังมีปัญหาอยู่แต่ changeToken เปลี่ยน (เช่นรายการจุดที่เป็น 0 เปลี่ยน) จะบันทึกใหม่อีก 1 แถว
        /// changeToken = null : ไม่บันทึกซ้ำจนกว่าจะกลับมาปกติ
        /// </summary>
        public static void Problem(string key, string category, string level, string source,
                                   string evt, string detail, string changeToken)
        {
            bool write = false;
            lock (_lock)
            {
                State st;
                if (!_states.TryGetValue(key, out st))
                {
                    st = new State();
                    _states[key] = st;
                }

                if (!st.Bad)
                {
                    st.Bad = true;
                    st.Since = DateTime.Now;
                    st.Token = changeToken;
                    write = true;
                }
                else if (changeToken != null && st.Token != changeToken)
                {
                    st.Token = changeToken;
                    write = true;
                }
            }
            if (write) Write(category, level, source, evt, detail, null);
        }

        /// <summary>แจ้งว่ากลับมาปกติ: บันทึกเฉพาะเมื่อก่อนหน้านี้มีปัญหาอยู่ พร้อมระยะเวลา</summary>
        public static void Recover(string key, string category, string source, string evt, string detail)
        {
            long dur = 0;
            bool write = false;
            lock (_lock)
            {
                State st;
                if (_states.TryGetValue(key, out st) && st.Bad)
                {
                    st.Bad = false;
                    dur = (long)(DateTime.Now - st.Since).TotalSeconds;
                    write = true;
                }
            }
            if (write) Write(category, LV_INFO, source, evt, detail, dur);
        }

        public static bool IsBad(string key)
        {
            lock (_lock)
            {
                State st;
                return _states.TryGetValue(key, out st) && st.Bad;
            }
        }

        // ================= Program =================

        public static void AppStart(string site, string version)
        {
            try
            {
                string prev = SQLiteHelper.checkLastSession();
                if (prev != null)
                    Write(CAT_SYSTEM, LV_ERROR, "Program", "APP_ABNORMAL_EXIT", prev, null);
            }
            catch (Exception ex)
            {
                if (UiLog != null) UiLog("Check last session fail: " + ex.Message);
            }
            Write(CAT_SYSTEM, LV_INFO, "Program", "APP_START", "Site " + site + " , Version " + version, null);
        }

        public static void AppStop(string reason)
        {
            Write(CAT_SYSTEM, LV_INFO, "Program", "APP_STOP", reason, null);
        }

        // ================= API / PLC =================

        public static void LoginFail(string reason)
        {
            Problem("API|LOGIN", CAT_API, LV_ERROR, "API", "API_LOGIN_FAIL", reason, null);
        }

        public static void LoginOk()
        {
            Recover("API|LOGIN", CAT_API, "API", "API_LOGIN_OK", "");
        }

        public static void PlcFail(string plcIp, string reason)
        {
            Problem("PLC", CAT_PLC, LV_ERROR, "PLC " + plcIp, "PLC_DISCONNECTED", reason, null);
        }

        public static void PlcOk(string plcIp)
        {
            Recover("PLC", CAT_PLC, "PLC " + plcIp, "PLC_RECONNECTED", "");
        }

        // ================= ตรวจผลการอ่าน 1 รอบ =================

        internal class LineRead
        {
            public string Silo;
            public int LineIndex;
            public ReadResult Result;
        }

        /// <summary>
        /// เรียกหลังอ่าน API ครบ 1 รอบ (เรียกจาก thread เดียว)
        /// </summary>
        public static void EvaluateCycle(ReadResult weather, List<ReadResult> volumes, List<LineRead> lines)
        {
            // ---- API หลุด = อ่านไม่ได้ทั้งรอบ ----
            int total = 0, fail = 0;
            string firstErr = null;
            Count(weather, ref total, ref fail, ref firstErr);
            foreach (ReadResult r in volumes) Count(r, ref total, ref fail, ref firstErr);
            foreach (LineRead l in lines) Count(l.Result, ref total, ref fail, ref firstErr);

            if (total > 0 && fail == total)
            {
                Problem("API|CONN", CAT_API, LV_ERROR, "API", "API_DISCONNECTED", firstErr, null);
                return; // ไม่ตรวจค่า 0 / รายสาย ระหว่างที่ API หลุด
            }
            Recover("API|CONN", CAT_API, "API", "API_RECONNECTED", "");

            // ---- Weather ----
            if (weather != null)
            {
                CheckWeather(weather, 0, "WEATHER_TEMP", "Temperature");
                CheckWeather(weather, 1, "WEATHER_HUM", "Humidity");
            }

            // ---- รายสาย ----
            foreach (LineRead l in lines)
            {
                string src = l.Silo + " L" + (l.LineIndex + 1);
                string readKey = "LINE|" + src;

                if (!l.Result.Ok)
                {
                    // สายอ่านไม่ได้ -> บันทึก LINE_READ_FAIL อย่างเดียว ไม่บันทึก TEMP_ZERO ซ้ำ
                    Problem(readKey, CAT_DATA, LV_WARN, src, "LINE_READ_FAIL", l.Result.Error, null);
                    continue;
                }
                Recover(readKey, CAT_DATA, src, "LINE_READ_OK", "");

                CheckZero("TEMP|" + src, src, "TEMP_ZERO", "TEMP_RECOVER",
                          l.Result.Values, l.Result.Cause, TempPoints(l.LineIndex));

                if (l.LineIndex == 0 && l.Result.Values2 != null)
                {
                    string msrc = l.Silo + " Moist";
                    CheckZero("MOIS|" + msrc, msrc, "MOIST_ZERO", "MOIST_RECOVER",
                              l.Result.Values2, l.Result.Cause2, MoisPoints);
                }
            }
        }

        private static void Count(ReadResult r, ref int total, ref int fail, ref string firstErr)
        {
            if (r == null) return;
            total++;
            if (!r.Ok)
            {
                fail++;
                if (firstErr == null) firstErr = r.Error;
            }
        }

        private static void CheckWeather(ReadResult w, int idx, string evtPrefix, string name)
        {
            string key = "WEATHER|" + name;
            bool zero = !w.Ok || w.Values[idx] == 0m;
            if (zero)
            {
                string detail = !w.Ok ? "อ่านไม่ได้ (" + w.Error + ")"
                                      : name + " = 0 (" + CauseText(w.Cause != null ? w.Cause[idx] : API.CAUSE_ZERO) + ")";
                Problem(key, CAT_DATA, LV_WARN, "Weather", evtPrefix + "_ZERO", detail, null);
            }
            else
            {
                Recover(key, CAT_DATA, "Weather", evtPrefix + "_RECOVER", name + " = " + w.Values[idx].ToString("0.##"));
            }
        }

        private static void CheckZero(string key, string source, string evtZero, string evtRecover,
                                      decimal[] values, byte[] cause, int points)
        {
            if (values == null) return;
            int n = Math.Min(points, values.Length);

            // แยกจุดที่เป็น 0 ตามสาเหตุ
            List<string> zeroPts = new List<string>();
            List<string> stale = new List<string>(), missing = new List<string>(), apiZero = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (values[i] != 0m) continue;
                string p = "P" + (i + 1);
                zeroPts.Add(p);
                byte c = (cause != null && i < cause.Length) ? cause[i] : API.CAUSE_ZERO;
                if (c == API.CAUSE_STALE) stale.Add(p);
                else if (c == API.CAUSE_MISSING) missing.Add(p);
                else apiZero.Add(p);
            }

            if (zeroPts.Count == 0)
            {
                Recover(key, CAT_DATA, source, evtRecover, "");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("จุด ").Append(string.Join(", ", zeroPts)).Append(" = 0");
            List<string> why = new List<string>();
            if (stale.Count > 0) why.Add("ข้อมูลเก่าเกิน " + API._lateHrData + " ชม.: " + string.Join(", ", stale));
            if (missing.Count > 0) why.Add("ไม่มีข้อมูล: " + string.Join(", ", missing));
            if (apiZero.Count > 0) why.Add("ค่า 0 จาก API: " + string.Join(", ", apiZero));
            sb.Append(" (").Append(string.Join(" ; ", why)).Append(")");

            // จุดที่เป็น 0 เปลี่ยน -> บันทึกใหม่ 1 แถว
            Problem(key, CAT_DATA, LV_WARN, source, evtZero, sb.ToString(), string.Join(",", zeroPts));
        }

        private static string CauseText(byte c)
        {
            if (c == API.CAUSE_STALE) return "ข้อมูลเก่าเกิน " + API._lateHrData + " ชม.";
            if (c == API.CAUSE_MISSING) return "ไม่มีข้อมูล";
            return "ค่า 0 จาก API";
        }
    }
}
