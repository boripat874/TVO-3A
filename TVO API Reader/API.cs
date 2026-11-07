using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TVO_API_Reader
{
    /// <summary>ผลการอ่านค่า 1 ครั้ง (แยก error ต่อ request แทนตัวแปร static ที่ใช้ร่วมกัน)</summary>
    internal class ReadResult
    {
        public decimal[] Values;
        public decimal[] Values2;   // ใช้กับสายที่อ่าน moisture พร้อม temperature
        public byte[] Cause;        // สาเหตุเมื่อค่าเป็น 0 (ดู API.CAUSE_*) ต่อจุด
        public byte[] Cause2;
        public string Error = "";
        public bool Ok { get { return Error.Length == 0; } }
    }

    internal class API
    {
        public static string _apiUrl = ConfigurationManager.AppSettings["ip"];
        public static string _apiUser = ConfigurationManager.AppSettings["user"];
        public static string _apiPass = ConfigurationManager.AppSettings["password"];
        public static int _lateHrData = Convert.ToInt16(ConfigurationManager.AppSettings["lateHrData"]);
        public static string _apiToken = "";
        public static string _resTxt = "";
        public static string _errTxt = "";

        public static string _plcIP = ConfigurationManager.AppSettings["plcIP"];
        public static string _customer_ID = "";
        public static string _Asset_ID = "";
        public static string _Weather_Asset_ID = "";
        public static string _Weather_Device_ID = "";
        public static string[] _Silo_ID;

        /// <summary>true เมื่อ API ตอบ 401/403 (token หมดอายุ) -> MainUI จะ login ใหม่ในรอบถัดไป</summary>
        public static volatile bool NeedRelogin = false;

        public const int PointsPerLine = silodata.PointsPerLine;

        // สาเหตุของค่า 0 (ใช้ใน EventLog)
        public const byte CAUSE_OK = 0;       // ค่าปกติ (ไม่ใช่ 0)
        public const byte CAUSE_ZERO = 1;     // API ส่งค่า 0 มาจริง
        public const byte CAUSE_STALE = 2;    // ข้อมูลเก่าเกิน lateHrData
        public const byte CAUSE_MISSING = 3;  // ไม่มี key นี้ในข้อมูลที่ได้

        // ====== HttpClient ตัวเดียวใช้ทั้งโปรแกรม ======
        // เดิมสร้าง HttpClient ใหม่ทุก request (~70 ครั้ง/รอบ) ทำให้ socket ค้าง TIME_WAIT และกินหน่วยความจำ
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false;
            // ค่าเริ่มต้นของ .NET Framework จำกัด 2 connection ต่อ host -> อ่านขนานไม่ได้
            if (ServicePointManager.DefaultConnectionLimit < 16)
                ServicePointManager.DefaultConnectionLimit = 16;

            HttpClient c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            return c;
        }

        // key ที่ใช้อ่าน (สร้างครั้งเดียว)
        private static readonly string[] _tempKeys = BuildKeys("temperature-", PointsPerLine);
        private static readonly string[] _moisKeys = BuildKeys("moisture-", PointsPerLine);
        private static readonly string _qsTemp = string.Join(",", _tempKeys);
        private static readonly string _qsTempMois = _qsTemp + "," + string.Join(",", _moisKeys);

        private static string[] BuildKeys(string prefix, int n)
        {
            string[] k = new string[n];
            for (int i = 0; i < n; i++) k[i] = prefix + (i + 1).ToString();
            return k;
        }

        private static string BaseUrl { get { return "http://" + _apiUrl; } }

        private class HttpRes
        {
            public bool Ok;
            public string Body = "";
            public string Error = "";
        }

        private static async Task<HttpRes> SendAsync(HttpMethod method, string path, object body, bool auth)
        {
            HttpRes r = new HttpRes();
            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(method, BaseUrl + path))
                {
                    if (auth)
                        req.Headers.TryAddWithoutValidation("X-Authorization", "Bearer " + _apiToken);
                    if (body != null)
                    {
                        string json = body as string ?? JsonConvert.SerializeObject(body);
                        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }

                    using (HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            r.Body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            r.Ok = true;
                        }
                        else
                        {
                            r.Error = resp.StatusCode.ToString();
                            if (auth && (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden))
                                NeedRelogin = true;
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                r.Error = "Timeout";
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        // ================= Login / Device discovery =================

        public static async Task<bool> login_agrolog()
        {
            _resTxt = "";
            _errTxt = "";
            var obj = new { username = _apiUser, password = _apiPass };
            HttpRes r = await SendAsync(HttpMethod.Post, "/api/auth/login", obj, false).ConfigureAwait(false);
            if (!r.Ok)
            {
                _resTxt = r.Error;
                _errTxt = r.Error;
                return false;
            }

            try
            {
                JObject json = JObject.Parse(r.Body);
                if (json["token"] != null)
                {
                    _apiToken = json["token"].ToString();
                    NeedRelogin = false;
                    return true;
                }
                if (json["message"] != null && json["message"].ToString() == "User not found")
                    _resTxt = "* Username/Password ของคุณไม่ถูกต้อง";
                else
                    _resTxt = r.Body;
            }
            catch (Exception ex)
            {
                _errTxt = ex.Message;
            }
            return false;
        }

        public static async Task<bool> get_customer_ID()
        {
            HttpRes r = await SendAsync(HttpMethod.Get, "/api/auth/user", null, true).ConfigureAwait(false);
            if (!r.Ok) { _resTxt = r.Error; return false; }
            try
            {
                JObject json = JObject.Parse(r.Body);
                JToken id = json["customerId"]?["id"];
                if (id == null) { _resTxt = r.Body; return false; }
                _customer_ID = id.ToString();
                return true;
            }
            catch (Exception ex) { _errTxt = ex.Message; return false; }
        }

        private static object Relation(string typesName, string[] types, string rootType, string rootId)
        {
            var parameters = new { direction = "FROM", maxLevel = 1, rootType = rootType, rootId = rootId };
            if (typesName == "assetTypes")
                return new { assetTypes = types, parameters = parameters, relationType = "Contains" };
            return new { deviceTypes = types, parameters = parameters, relationType = "Contains" };
        }

        public static async Task<bool> post_asset_ID()
        {
            HttpRes r = await SendAsync(HttpMethod.Post, "/api/assets",
                Relation("assetTypes", new[] { "site" }, "CUSTOMER", _customer_ID), true).ConfigureAwait(false);
            if (!r.Ok) { _resTxt = r.Error; return false; }
            try
            {
                JArray array = JArray.Parse(r.Body);
                JToken id = array.Count > 0 ? array[0]["id"]?["id"] : null;
                if (id == null) { _resTxt = r.Body; return false; }
                _Asset_ID = id.ToString();
                return true;
            }
            catch (Exception ex) { _errTxt = ex.Message; return false; }
        }

        public static async Task<bool> post_asset_List()
        {
            HttpRes r = await SendAsync(HttpMethod.Post, "/api/assets",
                Relation("assetTypes", new[] { "silo", "silo_group", "weather_station", "aeration" }, "ASSET", _Asset_ID), true).ConfigureAwait(false);
            if (!r.Ok) { _resTxt = r.Error; return false; }
            try
            {
                JArray array = JArray.Parse(r.Body);
                foreach (JToken item in array)
                {
                    string name = (string)item["name"];
                    string type = (string)item["type"];
                    string id = (string)item["id"]?["id"];
                    if (name == null || id == null) continue;

                    if (type == "silo")
                    {
                        // เดิมใช้ Add -> login ซ้ำแล้ว error "key ซ้ำ" ; ตอนนี้อัปเดต ID ของตัวเดิมแทน
                        silodata s;
                        if (MainUI._dicSiloData.TryGetValue(name, out s))
                            s.siloID = id;
                        else
                            MainUI._dicSiloData.Add(name, new silodata(name, id));
                    }
                    else if (type == "weather_station")
                    {
                        _Weather_Asset_ID = id;
                    }
                }
                return true;
            }
            catch (Exception ex) { _errTxt = ex.Message; return false; }
        }

        public static async Task<bool> post_Weather_Device_ID()
        {
            HttpRes r = await SendAsync(HttpMethod.Post, "/api/devices",
                Relation("deviceTypes", new[] { "weather_station" }, "ASSET", _Weather_Asset_ID), true).ConfigureAwait(false);
            if (!r.Ok) { _resTxt = r.Error; return false; }
            try
            {
                JArray array = JArray.Parse(r.Body);
                JToken id = array.Count > 0 ? array[0]["id"]?["id"] : null;
                if (id == null) { _resTxt = r.Body; return false; }
                _Weather_Device_ID = id.ToString();
                return true;
            }
            catch (Exception ex) { _errTxt = ex.Message; return false; }
        }

        private static readonly Regex _trailingNumber = new Regex(@"(\d+)\s*$", RegexOptions.Compiled);

        public static async Task<bool> post_Silo_Device_ID(string pSiloName, int pSensorLineQty)
        {
            silodata silo = MainUI._dicSiloData[pSiloName];
            HttpRes r = await SendAsync(HttpMethod.Post, "/api/devices",
                Relation("deviceTypes", new[] { "temperature_sensor_lines", "moisture_sensor_lines", "level_indicator", "head_space_sensor" },
                         "ASSET", silo.siloID), true).ConfigureAwait(false);
            if (!r.Ok) { _resTxt = r.Error; return false; }
            try
            {
                JArray array = JArray.Parse(r.Body);
                foreach (JToken item in array)
                {
                    string type = (string)item["type"];
                    if (type != "moisture_sensor_lines" && type != "temperature_sensor_lines")
                        continue;

                    string dName = (string)item["name"];
                    string id = (string)item["id"]?["id"];
                    if (dName == null || id == null) continue;

                    // เดิมใช้ Substring(Length-2) -> ชื่อที่ลงท้ายเลขหลักเดียวทำให้ error ทั้ง silo
                    Match m = _trailingNumber.Match(dName);
                    if (!m.Success) { _resTxt = "ไม่พบเลขสายในชื่อ " + dName; continue; }

                    int i = (int.Parse(m.Groups[1].Value) - 1) % pSensorLineQty;
                    if (i < 0 || i >= silo.line_id.Length) { _resTxt = "เลขสายเกินช่วง " + dName; continue; }

                    silo.line_id[i] = id;
                }
                return true;
            }
            catch (Exception ex) { _errTxt = ex.Message; return false; }
        }

        // ================= Telemetry =================

        private static async Task<JObject> GetTelemetryAsync(string entity, string id, string keys, ReadResult res)
        {
            HttpRes r = await SendAsync(HttpMethod.Get,
                "/api/plugins/telemetry/" + entity + "/" + id + "/values/timeseries?keys=" + keys, null, true).ConfigureAwait(false);
            if (!r.Ok) { res.Error = r.Error; return null; }
            try { return JObject.Parse(r.Body); }
            catch (Exception ex) { res.Error = ex.Message; return null; }
        }

        /// <summary>
        /// อ่านอุณหภูมิ 1 สาย (ถ้า withMoisture = true จะอ่าน moisture ใน request เดียวกัน)
        /// เดิมสายที่ 1 ยิง 2 request (moisture + temp) ตอนนี้รวมเป็น 1
        /// </summary>
        public static async Task<ReadResult> ReadLineAsync(string deviceID, bool withMoisture)
        {
            ReadResult res = new ReadResult();
            res.Values = new decimal[PointsPerLine];
            res.Cause = new byte[PointsPerLine];
            if (withMoisture)
            {
                res.Values2 = new decimal[PointsPerLine];
                res.Cause2 = new byte[PointsPerLine];
            }

            JObject json = await GetTelemetryAsync("DEVICE", deviceID, withMoisture ? _qsTempMois : _qsTemp, res).ConfigureAwait(false);
            if (json == null) return res;

            for (int i = 0; i < PointsPerLine; i++)
            {
                res.Values[i] = valueDetectCheckTime(json, _tempKeys[i], out res.Cause[i]);
                if (withMoisture) res.Values2[i] = valueDetectCheckTime(json, _moisKeys[i], out res.Cause2[i]);
            }
            return res;
        }

        public static async Task<ReadResult> ReadWeatherAsync()
        {
            ReadResult res = new ReadResult();
            res.Values = new decimal[2];
            res.Cause = new byte[2];
            JObject json = await GetTelemetryAsync("DEVICE", _Weather_Device_ID, "temperature,humidity", res).ConfigureAwait(false);
            if (json == null) return res;
            res.Values[0] = valueDetect(json, "temperature", out res.Cause[0]);
            res.Values[1] = valueDetect(json, "humidity", out res.Cause[1]);
            return res;
        }

        private static readonly string[] _volKeys = { "silo-volume", "crop-volume", "crop-weight", "crop-level-min", "crop-level-avg", "crop-level-max" };
        private static readonly string _qsVol = string.Join(",", _volKeys);

        public static async Task<ReadResult> ReadVolumeAsync(string assetID)
        {
            ReadResult res = new ReadResult();
            res.Values = new decimal[_volKeys.Length];
            JObject json = await GetTelemetryAsync("ASSET", assetID, _qsVol, res).ConfigureAwait(false);
            if (json == null) return res;
            for (int i = 0; i < _volKeys.Length; i++)
                res.Values[i] = valueDetect(json, _volKeys[i]);
            return res;
        }

        // ================= Helpers =================

        public static bool checkNotLate(long pTimestamp)
        {
            DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds(pTimestamp).LocalDateTime;
            return (DateTime.Now - dt).TotalHours <= _lateHrData;
        }

        private static JToken FirstPoint(JObject pJson, string pKey)
        {
            JArray arr = pJson[pKey] as JArray;
            return (arr != null && arr.Count > 0) ? arr[0] : null;
        }

        private static decimal ToDecimal(JToken v)
        {
            if (v == null || v.Type == JTokenType.Null) return 0m;
            try { return v.ToObject<decimal>(); }
            catch { return 0m; }   // ค่าแปลก ๆ เช่น "NaN" ไม่ทำให้ทั้ง request ล้ม
        }

        public static decimal valueDetect(JObject pJson, string pKey)
        {
            byte cause;
            return valueDetect(pJson, pKey, out cause);
        }

        public static decimal valueDetect(JObject pJson, string pKey, out byte cause)
        {
            JToken p = FirstPoint(pJson, pKey);
            if (p == null) { cause = CAUSE_MISSING; return 0m; }
            decimal v = ToDecimal(p["value"]);
            cause = v == 0m ? CAUSE_ZERO : CAUSE_OK;
            return v;
        }

        public static decimal valueDetectCheckTime(JObject pJson, string pKey)
        {
            byte cause;
            return valueDetectCheckTime(pJson, pKey, out cause);
        }

        public static decimal valueDetectCheckTime(JObject pJson, string pKey, out byte cause)
        {
            JToken p = FirstPoint(pJson, pKey);
            if (p == null) { cause = CAUSE_MISSING; return 0m; }
            JToken ts = p["ts"];
            long t = (ts == null || ts.Type == JTokenType.Null) ? 0 : ts.ToObject<long>();
            if (!checkNotLate(t)) { cause = CAUSE_STALE; return 0m; }
            decimal v = ToDecimal(p["value"]);
            cause = v == 0m ? CAUSE_ZERO : CAUSE_OK;
            return v;
        }
    }
}
