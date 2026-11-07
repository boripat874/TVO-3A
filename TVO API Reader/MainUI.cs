using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FluentModbus;

namespace TVO_API_Reader
{
    public partial class MainUI : Form
    {
        private bool _isLogin = false;
        private bool _indexesChecked = false;

        // ConcurrentQueue: เดิมใช้ Queue<string> ซึ่งไม่ thread-safe แต่ถูกเขียนจากหลาย thread
        private readonly ConcurrentQueue<string> _qStatus = new ConcurrentQueue<string>();

        private string _siteName = ConfigurationManager.AppSettings["site"];
        private int _readInterval = Convert.ToInt16(ConfigurationManager.AppSettings["readSecInterval"]);
        private int _logMinuteInteval = Convert.ToInt16(ConfigurationManager.AppSettings["logMinuteInterval"]);
        private readonly int _apiParallel = ReadIntSetting("apiParallel", 4, 1, 16);
        private readonly bool _verboseLog = (ConfigurationManager.AppSettings["verboseLog"] ?? "false").Trim().ToLower() == "true";
        private const int MaxLogLines = 200;

        public static WeatherStation _wether = new WeatherStation();
        private fanData[] _fandata;
        private DateTime _lastLogDate = DateTime.Now;

        public static Dictionary<string, silodata> _dicSiloData = new Dictionary<string, silodata>();

        // Modbus client ของรอบปัจจุบัน (เปิดครั้งเดียวต่อรอบ แทนการเปิด/ปิดใหม่ทุกบล็อก ~73 ครั้ง/รอบ)
        private ModbusTcpClient _mb;
        private bool _mbFailed;
        private string _mbError = "";

        // EventLog
        private readonly int _eventLogKeepDays = ReadIntSetting("eventLogKeepDays", 365, 1, 3650);
        private DateTime _lastPurgeDate = DateTime.MinValue;

        public MainUI()
        {
            InitializeComponent();
        }

        private static int ReadIntSetting(string key, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(ConfigurationManager.AppSettings[key], out v)) v = def;
            return Math.Max(min, Math.Min(max, v));
        }

        private void Log(string msg)
        {
            _qStatus.Enqueue(DateTime.Now.ToString("dd/MM/yy HH:mm:ss") + "   " + msg);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            this.Text = _siteName + " API to PLC";
            intilize();

            // ===== EventLog: เปิดโปรแกรม (ตรวจด้วยว่าครั้งก่อนปิดผิดปกติหรือไม่) =====
            EventMonitor.UiLog = Log;
            try
            {
                SQLiteHelper.InitializeEventLog();
                EventMonitor.AppStart(_siteName, Application.ProductVersion);
            }
            catch (Exception ex)
            {
                Log("EventLog init fail: " + ex.Message);
            }

            timer1.Interval = Math.Max(1, _readInterval) * 1000;
            timer2.Start();

            // เริ่มรอบแรกทันที (login ทำใน background ไม่ทำให้หน้าจอค้าง)
            // timer1 จะถูก Start ใน RunWorkerCompleted
            backgroundWorker1.RunWorkerAsync();
        }

        private void intilize()
        {
            SQLiteHelper.dbPath = System.IO.Path.Combine(Application.StartupPath, "TVOData.db");

            if (_siteName == "EX3A")
            {
                SQLiteHelper.InitializeDatabaseEX3A();

                _fandata = new fanData[9];
                _fandata[0] = new fanData("S11", "S11-FAN-T-A");
                _fandata[1] = new fanData("S11", "S11-FAN-B-A");
                _fandata[2] = new fanData("S11", "S11-FAN-B-B");
                _fandata[3] = new fanData("S12", "S12-FAN-T-A");
                _fandata[4] = new fanData("S12", "S12-FAN-B-A");
                _fandata[5] = new fanData("S12", "S12-FAN-B-B");
                _fandata[6] = new fanData("S12A", "S12A-FAN-T-A");
                _fandata[7] = new fanData("S12A", "S12A-FAN-B-A");
                _fandata[8] = new fanData("S12A", "S12A-FAN-B-B");
                //    _silo = new silodata[3];
                //    _silo[0] = new silodata("S11", "4cb35900-fcf3-11f0-bb76-3141031e8d3e");
                //    _silo[1] = new silodata("S12", "4c94d481-fcf3-11f0-bb76-3141031e8d3e");
                //    _silo[2] = new silodata("S12A","4c9b8b40-fcf3-11f0-bb76-3141031e8d3e");
                //    _wether = new WeatherStation("4cbc80c0-fcf3-11f0-bb76-3141031e8d3e");

                //    convertToClass("EX3A S11.txt");
                //    convertToClass("EX3A S12.txt");
                //    convertToClass("EX3A S12A.txt");
            }
            else if (_siteName == "EX3B")
            {
                SQLiteHelper.InitializeDatabaseEX3B();

                _fandata = new fanData[16];
                _fandata[0] = new fanData("S26", "S26-FAN-T-A");
                _fandata[1] = new fanData("S26", "S26-FAN-T-B");
                _fandata[2] = new fanData("S26", "S26-FAN-B-A");
                _fandata[3] = new fanData("S26", "S26-FAN-B-B");
                
                _fandata[4] = new fanData("S27", "S27-FAN-T-A");
                _fandata[5] = new fanData("S27", "S27-FAN-T-B");
                _fandata[6] = new fanData("S27", "S27-FAN-B-A");
                _fandata[7] = new fanData("S27", "S27-FAN-B-B");

                _fandata[8] = new fanData("S28", "S28-FAN-T-A");
                _fandata[9] = new fanData("S28", "S28-FAN-T-B");
                _fandata[10] = new fanData("S28", "S28-FAN-B-A");
                _fandata[11] = new fanData("S28", "S28-FAN-B-B");

                _fandata[12] = new fanData("S29", "S29-FAN-T-A");
                _fandata[13] = new fanData("S29", "S29-FAN-T-B");
                _fandata[14] = new fanData("S29", "S29-FAN-B-A");
                _fandata[15] = new fanData("S29", "S29-FAN-B-B");
                
            }
            else if (_siteName == "1B")
            {
                SQLiteHelper.InitializeDatabase1B();

                _fandata = new fanData[18];
                _fandata[0] = new fanData("S9", "S9-FAN-T-A");
                _fandata[1] = new fanData("S9", "S9-FAN-B-A");
                _fandata[2] = new fanData("S9", "S9-FAN-B-B");

                _fandata[3] = new fanData("S10", "S10-FAN-T-A");
                _fandata[4] = new fanData("S10", "S10-FAN-B-A");
                _fandata[5] = new fanData("S10", "S10-FAN-B-B");

                _fandata[6] = new fanData("S11", "S11-FAN-T-A");
                _fandata[7] = new fanData("S11", "S11-FAN-B-A");
                _fandata[8] = new fanData("S11", "S11-FAN-B-B");

                _fandata[9] = new fanData("S12", "S12-FAN-T-A");
                _fandata[10] = new fanData("S12", "S12-FAN-B-A");
                _fandata[11] = new fanData("S12", "S12-FAN-B-B");

                _fandata[12] = new fanData("S13", "S13-FAN-T-A");
                _fandata[13] = new fanData("S13", "S13-FAN-B-A");
                _fandata[14] = new fanData("S13", "S13-FAN-B-B");

                _fandata[15] = new fanData("S14", "S14-FAN-T-A");
                _fandata[16] = new fanData("S14", "S14-FAN-B-A");
                _fandata[17] = new fanData("S14", "S14-FAN-B-B");

            }
            else if (_siteName == "3A")
            {
                SQLiteHelper.InitializeDatabase3A();

                _fandata = new fanData[16];
                _fandata[0] = new fanData("S1", "S1-FAN-B-A");
                _fandata[1] = new fanData("S1", "S1-FAN-B-B");

                _fandata[2] = new fanData("S2", "S2-FAN-B-A");
                _fandata[3] = new fanData("S2", "S2-FAN-B-B");

                _fandata[4] = new fanData("S3", "S3-FAN-B-A");
                _fandata[5] = new fanData("S3", "S3-FAN-B-B");

                _fandata[6] = new fanData("S4", "S4-FAN-B-A");
                _fandata[7] = new fanData("S4", "S4-FAN-B-B");

                _fandata[8] = new fanData("S5", "S5-FAN-B-A");
                _fandata[9] = new fanData("S5", "S5-FAN-B-B");

                _fandata[10] = new fanData("S6", "S6-FAN-B-A");
                _fandata[11] = new fanData("S6", "S6-FAN-B-B");

                _fandata[12] = new fanData("S7", "S7-FAN-B-A");
                _fandata[13] = new fanData("S7", "S7-FAN-B-B");

                _fandata[14] = new fanData("S8", "S8-FAN-B-A");
                _fandata[15] = new fanData("S8", "S8-FAN-B-B");
            }

        }


        private void MainUI_FormClosing(object sender, FormClosingEventArgs e)
        {
            // บันทึกการปิดโปรแกรม (ปิดเอง / Windows shutdown / Task Manager)
            string reason;
            switch (e.CloseReason)
            {
                case CloseReason.UserClosing: reason = "ผู้ใช้ปิดโปรแกรม"; break;
                case CloseReason.WindowsShutDown: reason = "Windows Shutdown / Restart"; break;
                case CloseReason.TaskManagerClosing: reason = "ปิดจาก Task Manager"; break;
                case CloseReason.ApplicationExitCall: reason = "Application.Exit"; break;
                default: reason = e.CloseReason.ToString(); break;
            }
            try { EventMonitor.AppStop(reason); } catch { }
        }

        private int SensorLineQty()
        {
            if (_siteName == "EX3A") return 4;
            if (_siteName == "EX3B") return 6;
            if (_siteName == "1B") return 6;
            if (_siteName == "3A") return 7;
            return 1;
        }

        // ================= Timer / Worker =================

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (!backgroundWorker1.IsBusy)
            {
                timer1.Stop();
                backgroundWorker1.RunWorkerAsync();
            }
        }

        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            // เดิมเรียก login()/readTemp...() แบบไม่ await -> ส่ง PLC ด้วยค่ารอบก่อน และรอบอ่านซ้อนกันได้
            // ตอนนี้รอให้แต่ละขั้นเสร็จจริงก่อน (BackgroundWorker ไม่มี SynchronizationContext จึงไม่ deadlock)
            try
            {
                RunCycleAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log("Cycle error: " + ex.Message);
            }
        }

        private void backgroundWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            timer1.Start();
        }

        private async Task RunCycleAsync()
        {
            if (!_indexesChecked)
            {
                _indexesChecked = true;
                try
                {
                    SQLiteHelper.EnsureIndexes();
                }
                catch (Exception ex)
                {
                    Log("Create index fail: " + ex.Message);
                }
            }

            // ลบ EventLog เก่า วันละครั้ง
            if (_lastPurgeDate != DateTime.Today)
            {
                _lastPurgeDate = DateTime.Today;
                try
                {
                    int n = SQLiteHelper.purgeEventLog(_eventLogKeepDays);
                    if (n > 0) Log("ลบ EventLog เก่ากว่า " + _eventLogKeepDays + " วัน : " + n + " แถว");
                }
                catch (Exception ex)
                {
                    Log("Purge EventLog fail: " + ex.Message);
                }
            }

            if (!_isLogin || API.NeedRelogin)
            {
                await login().ConfigureAwait(false);
                if (!_isLogin) return;
            }

            CycleData data = await ReadAllAsync().ConfigureAwait(false);

            // token หมดอายุระหว่างอ่าน -> login ใหม่แล้วอ่านซ้ำ 1 ครั้ง (ไม่ส่งค่า 0 ทั้งชุดเข้า PLC)
            if (API.NeedRelogin)
            {
                Log("Token หมดอายุ -> Login ใหม่");
                _isLogin = false;
                await login().ConfigureAwait(false);
                if (!_isLogin) return;
                data = await ReadAllAsync().ConfigureAwait(false);
            }

            // ===== EventLog: ตรวจ API หลุด / สายอ่านไม่ได้ / ค่าเป็น 0 =====
            try
            {
                EventMonitor.EvaluateCycle(data.Weather, data.Volumes, data.Lines);
            }
            catch (Exception ex)
            {
                Log("EventMonitor error: " + ex.Message);
            }

            // ===== ส่ง/รับ PLC ด้วย connection เดียว =====
            _mb = new ModbusTcpClient();
            _mbFailed = false;
            _mbError = "";
            try
            {
                _mb.Connect(new IPEndPoint(IPAddress.Parse(API._plcIP), 502));

                if (_siteName == "EX3A") sendDataEX3A();
                else if (_siteName == "EX3B") sendDataEX3B();
                else if (_siteName == "1B") sendData1B();
                else if (_siteName == "3A") sendData3A();

                if (!_mbFailed)
                {
                    Log("Send MBTCP complete");
                    mbTCPRcv();
                }
            }
            catch (Exception ex)
            {
                _mbFailed = true;
                _mbError = ex.Message;
                Log("MBTCP fail " + ex.Message);
            }
            finally
            {
                try { _mb.Disconnect(); } catch { }
                _mb = null;
            }

            // ===== EventLog: PLC หลุด / กลับมา =====
            if (_mbFailed) EventMonitor.PlcFail(API._plcIP, _mbError);
            else EventMonitor.PlcOk(API._plcIP);

            // ===== บันทึก DB =====
            TimeSpan sp = DateTime.Now - _lastLogDate;
            if (sp.TotalMinutes > _logMinuteInteval)
            {
                _lastLogDate = DateTime.Now;
                try
                {
                    SQLiteHelper.insertAMB();
                    Log("Log Weather Station");

                    if (_siteName == "EX3A") SQLiteHelper.insertLogEX3A();
                    else if (_siteName == "EX3B") SQLiteHelper.insertLogEX3B();
                    else if (_siteName == "1B") SQLiteHelper.insertLog1B();
                    else if (_siteName == "3A") SQLiteHelper.insertLog3A();
                    Log("Log Silo Data");
                }
                catch (Exception ex)
                {
                    Log("Log DB fail " + ex.Message);
                }
            }
        }

        // ================= อ่าน API (ขนานกัน จำกัดจำนวนด้วย apiParallel) =================

        private static async Task Gated(SemaphoreSlim gate, Func<Task> work)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await work().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private static string Join(decimal[] v)
        {
            StringBuilder sb = new StringBuilder(v.Length * 10);
            for (int i = 0; i < v.Length; i++)
                sb.Append('[').Append(i).Append("] ").Append(v[i]).Append(" ; ");
            return sb.ToString();
        }

        /// <summary>
        /// ใช้ได้ทุกไซต์ (แทน readTempEX3A / EX3B / 1B / 3A ที่ซ้ำกัน)
        /// </summary>
        private class CycleData
        {
            public ReadResult Weather;
            public readonly List<ReadResult> Volumes = new List<ReadResult>();
            public readonly List<EventMonitor.LineRead> Lines = new List<EventMonitor.LineRead>();
        }

        private async Task<CycleData> ReadAllAsync()
        {
            DateTime t0 = DateTime.Now;
            List<Task> tasks = new List<Task>();
            int lineOk = 0, lineErr = 0;
            CycleData data = new CycleData();

            using (SemaphoreSlim gate = new SemaphoreSlim(_apiParallel))
            {
                // Weather
                tasks.Add(Gated(gate, async () =>
                {
                    ReadResult r = await API.ReadWeatherAsync().ConfigureAwait(false);
                    data.Weather = r;
                    _wether.temperature = r.Values[0];
                    _wether.humidity = r.Values[1];
                    if (!r.Ok) Log("Error Weather " + r.Error);
                    else Log("Wather Temp = " + _wether.temperature.ToString("#0.00") + ";  Humidity = " + _wether.humidity.ToString("#0.00"));
                }));

                foreach (silodata s in _dicSiloData.Values)
                {
                    silodata silo = s;

                    // Volume
                    tasks.Add(Gated(gate, async () =>
                    {
                        ReadResult r = await API.ReadVolumeAsync(silo.siloID).ConfigureAwait(false);
                        lock (data) data.Volumes.Add(r);
                        silo.siloVolume = r.Values[0];
                        silo.cropVolume = r.Values[1];
                        silo.cropWeight = r.Values[2];
                        silo.cropLevelMin = r.Values[3];
                        silo.cropLevelAvg = r.Values[4];
                        silo.cropLevelMax = r.Values[5];
                        if (!r.Ok) Log(silo.siloname + " Error Volume " + r.Error);
                        else if (_verboseLog) Log(silo.siloname + " Volume = " + silo.cropVolume.ToString("#0.00"));
                    }));

                    // Sensor lines (สาย 1 อ่าน moisture ใน request เดียวกัน)
                    for (int li = 0; li < silo.line_id.Length; li++)
                    {
                        string id = silo.line_id[li];
                        if (string.IsNullOrEmpty(id)) continue;
                        int lineIdx = li;

                        tasks.Add(Gated(gate, async () =>
                        {
                            ReadResult r = await API.ReadLineAsync(id, lineIdx == 0).ConfigureAwait(false);
                            lock (data) data.Lines.Add(new EventMonitor.LineRead { Silo = silo.siloname, LineIndex = lineIdx, Result = r });
                            silo.lines[lineIdx] = r.Values;
                            if (lineIdx == 0) silo.mois = r.Values2;

                            if (!r.Ok)
                            {
                                Interlocked.Increment(ref lineErr);
                                Log(silo.siloname + " L" + (lineIdx + 1) + " Error " + id + " " + r.Error);
                            }
                            else
                            {
                                Interlocked.Increment(ref lineOk);
                                if (_verboseLog)
                                {
                                    if (lineIdx == 0) Log("Read Moist " + silo.siloname + " L1\r\n" + Join(r.Values2));
                                    Log("Read Temp " + silo.siloname + " L" + (lineIdx + 1) + "\r\n" + Join(r.Values));
                                }
                            }
                        }));
                    }
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            Log("Read API: " + lineOk + " lines OK, " + lineErr + " error, " +
                (DateTime.Now - t0).TotalSeconds.ToString("0.0") + " s");

            // เรียงตามไซโล/สาย เพื่อให้ log ออกมาเป็นลำดับเดียวกันทุกรอบ
            data.Lines.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Silo, b.Silo);
                return c != 0 ? c : a.LineIndex.CompareTo(b.LineIndex);
            });
            return data;
        }

        // ================= ส่งข้อมูลเข้า PLC =================

        private void sendDataEX3A()
        {
            //Silo11 Temp
            modbusTCPSend(_dicSiloData["S11"].line1, 0 / 2);
            modbusTCPSend(_dicSiloData["S11"].line2, 40 / 2);
            modbusTCPSend(_dicSiloData["S11"].line3, 80 / 2);
            modbusTCPSend(_dicSiloData["S11"].line4, 120 / 2);
            //Silo12 Temp
            modbusTCPSend(_dicSiloData["S12"].line1, 160 / 2);
            modbusTCPSend(_dicSiloData["S12"].line2, 200 / 2);
            modbusTCPSend(_dicSiloData["S12"].line3, 240 / 2);
            modbusTCPSend(_dicSiloData["S12"].line4, 280 / 2);
            //Silo12A Temp
            modbusTCPSend(_dicSiloData["S12A"].line1, 320 / 2);
            modbusTCPSend(_dicSiloData["S12A"].line2, 360 / 2);
            modbusTCPSend(_dicSiloData["S12A"].line3, 400 / 2);
            modbusTCPSend(_dicSiloData["S12A"].line4, 440 / 2);

            decimal[] d0 = new decimal[2];
            d0[0] = _wether.temperature;
            d0[1] = _wether.humidity;
            modbusTCPSend(d0, 480 / 2);

            decimal[] d1 = new decimal[6];
            d1[0] = _dicSiloData["S11"].siloVolume;
            d1[1] = _dicSiloData["S11"].cropVolume;
            d1[2] = _dicSiloData["S11"].cropWeight;
            d1[3] = _dicSiloData["S11"].cropLevelMin;
            d1[4] = _dicSiloData["S11"].cropLevelAvg;
            d1[5] = _dicSiloData["S11"].cropLevelMax;
            modbusTCPSend(d1, 488 / 2);

            decimal[] d2 = new decimal[6];
            d2[0] = _dicSiloData["S12"].siloVolume;
            d2[1] = _dicSiloData["S12"].cropVolume;
            d2[2] = _dicSiloData["S12"].cropWeight;
            d2[3] = _dicSiloData["S12"].cropLevelMin;
            d2[4] = _dicSiloData["S12"].cropLevelAvg;
            d2[5] = _dicSiloData["S12"].cropLevelMax;
            modbusTCPSend(d2, 516 / 2);

            decimal[] d3 = new decimal[6];
            d3[0] = _dicSiloData["S12A"].siloVolume;
            d3[1] = _dicSiloData["S12A"].cropVolume;
            d3[2] = _dicSiloData["S12A"].cropWeight;
            d3[3] = _dicSiloData["S12A"].cropLevelMin;
            d3[4] = _dicSiloData["S12A"].cropLevelAvg;
            d3[5] = _dicSiloData["S12A"].cropLevelMax;
            modbusTCPSend(d3, 544 / 2);

            //Moiture
            modbusTCPSend(_dicSiloData["S11"].mois, 588 / 2);
            modbusTCPSend(_dicSiloData["S12"].mois, 628 / 2);
            modbusTCPSend(_dicSiloData["S12A"].mois, 668 / 2);

        }

        private void sendDataEX3B()
        {
            //Silo26 Temp
            modbusTCPSend(_dicSiloData["S26"].line1, 0 / 2);
            modbusTCPSend(_dicSiloData["S26"].line2, 40 / 2);
            modbusTCPSend(_dicSiloData["S26"].line3, 80 / 2);
            modbusTCPSend(_dicSiloData["S26"].line4, 120 / 2);
            modbusTCPSend(_dicSiloData["S26"].line5, 160 / 2);
            modbusTCPSend(_dicSiloData["S26"].line6, 200 / 2);
            //Silo27 Temp
            modbusTCPSend(_dicSiloData["S27"].line1, 240 / 2);
            modbusTCPSend(_dicSiloData["S27"].line2, 280 / 2);
            modbusTCPSend(_dicSiloData["S27"].line3, 320 / 2);
            modbusTCPSend(_dicSiloData["S27"].line4, 360 / 2);
            modbusTCPSend(_dicSiloData["S27"].line5, 400 / 2);
            modbusTCPSend(_dicSiloData["S27"].line6, 440 / 2);
            //Silo28 Temp
            modbusTCPSend(_dicSiloData["S28"].line1, 480 / 2);
            modbusTCPSend(_dicSiloData["S28"].line2, 520 / 2);
            modbusTCPSend(_dicSiloData["S28"].line3, 560 / 2);
            modbusTCPSend(_dicSiloData["S28"].line4, 600 / 2);
            modbusTCPSend(_dicSiloData["S28"].line5, 640 / 2);
            modbusTCPSend(_dicSiloData["S28"].line6, 680 / 2);
            //Silo28 Temp
            modbusTCPSend(_dicSiloData["S29"].line1, 720 / 2);
            modbusTCPSend(_dicSiloData["S29"].line2, 760 / 2);
            modbusTCPSend(_dicSiloData["S29"].line3, 800 / 2);
            modbusTCPSend(_dicSiloData["S29"].line4, 840 / 2);
            modbusTCPSend(_dicSiloData["S29"].line5, 880 / 2);
            modbusTCPSend(_dicSiloData["S29"].line6, 920 / 2);

            decimal[] d0 = new decimal[2];
            d0[0] = _wether.temperature;
            d0[1] = _wether.humidity;
            modbusTCPSend(d0, 960 / 2);

            decimal[] d1 = new decimal[6];
            d1[0] = _dicSiloData["S26"].siloVolume;
            d1[1] = _dicSiloData["S26"].cropVolume;
            d1[2] = _dicSiloData["S26"].cropWeight;
            d1[3] = _dicSiloData["S26"].cropLevelMin;
            d1[4] = _dicSiloData["S26"].cropLevelAvg;
            d1[5] = _dicSiloData["S26"].cropLevelMax;
            modbusTCPSend(d1, 968 / 2);

            decimal[] d2 = new decimal[6];
            d2[0] = _dicSiloData["S27"].siloVolume;
            d2[1] = _dicSiloData["S27"].cropVolume;
            d2[2] = _dicSiloData["S27"].cropWeight;
            d2[3] = _dicSiloData["S27"].cropLevelMin;
            d2[4] = _dicSiloData["S27"].cropLevelAvg;
            d2[5] = _dicSiloData["S27"].cropLevelMax;
            modbusTCPSend(d2, 996 / 2);

            decimal[] d3 = new decimal[6];
            d3[0] = _dicSiloData["S28"].siloVolume;
            d3[1] = _dicSiloData["S28"].cropVolume;
            d3[2] = _dicSiloData["S28"].cropWeight;
            d3[3] = _dicSiloData["S28"].cropLevelMin;
            d3[4] = _dicSiloData["S28"].cropLevelAvg;
            d3[5] = _dicSiloData["S28"].cropLevelMax;
            modbusTCPSend(d3, 1024 / 2);

            decimal[] d4 = new decimal[6];
            d4[0] = _dicSiloData["S29"].siloVolume;
            d4[1] = _dicSiloData["S29"].cropVolume;
            d4[2] = _dicSiloData["S29"].cropWeight;
            d4[3] = _dicSiloData["S29"].cropLevelMin;
            d4[4] = _dicSiloData["S29"].cropLevelAvg;
            d4[5] = _dicSiloData["S29"].cropLevelMax;
            modbusTCPSend(d4, 1052 / 2); // แก้: เดิมส่ง d3 (S28) แทน S29

            //Moiture
            modbusTCPSend(_dicSiloData["S26"].mois, 1100 / 2);
            modbusTCPSend(_dicSiloData["S27"].mois, 1140 / 2);
            modbusTCPSend(_dicSiloData["S28"].mois, 1180 / 2);
            modbusTCPSend(_dicSiloData["S29"].mois, 1220 / 2);

        }

        private void sendData1B()
        {
            //Silo9 Temp
            modbusTCPSend(_dicSiloData["S9"].line1, 0 / 2);
            modbusTCPSend(_dicSiloData["S9"].line2, 40 / 2);
            modbusTCPSend(_dicSiloData["S9"].line3, 80 / 2);
            modbusTCPSend(_dicSiloData["S9"].line4, 120 / 2);
            modbusTCPSend(_dicSiloData["S9"].line5, 80 / 2);
            modbusTCPSend(_dicSiloData["S9"].line6, 120 / 2);
            //Silo10 Temp
            modbusTCPSend(_dicSiloData["S10"].line1, 160 / 2);
            modbusTCPSend(_dicSiloData["S10"].line2, 200 / 2);
            modbusTCPSend(_dicSiloData["S10"].line3, 240 / 2);
            modbusTCPSend(_dicSiloData["S10"].line4, 280 / 2);
            modbusTCPSend(_dicSiloData["S10"].line5, 240 / 2);
            modbusTCPSend(_dicSiloData["S10"].line6, 280 / 2);
            //Silo11 Temp
            modbusTCPSend(_dicSiloData["S11"].line1, 320 / 2);
            modbusTCPSend(_dicSiloData["S11"].line2, 360 / 2);
            modbusTCPSend(_dicSiloData["S11"].line3, 400 / 2);
            modbusTCPSend(_dicSiloData["S11"].line4, 440 / 2);
            modbusTCPSend(_dicSiloData["S11"].line5, 400 / 2);
            modbusTCPSend(_dicSiloData["S11"].line6, 440 / 2);
            //Silo12 Temp
            modbusTCPSend(_dicSiloData["S12"].line1, 320 / 2);
            modbusTCPSend(_dicSiloData["S12"].line2, 360 / 2);
            modbusTCPSend(_dicSiloData["S12"].line3, 400 / 2);
            modbusTCPSend(_dicSiloData["S12"].line4, 440 / 2);
            modbusTCPSend(_dicSiloData["S12"].line5, 400 / 2);
            modbusTCPSend(_dicSiloData["S12"].line6, 440 / 2);
            //Silo13 Temp
            modbusTCPSend(_dicSiloData["S13"].line1, 320 / 2);
            modbusTCPSend(_dicSiloData["S13"].line2, 360 / 2);
            modbusTCPSend(_dicSiloData["S13"].line3, 400 / 2);
            modbusTCPSend(_dicSiloData["S13"].line4, 440 / 2);
            modbusTCPSend(_dicSiloData["S13"].line5, 400 / 2);
            modbusTCPSend(_dicSiloData["S13"].line6, 440 / 2);
            //Silo14 Temp
            modbusTCPSend(_dicSiloData["S14"].line1, 320 / 2);
            modbusTCPSend(_dicSiloData["S14"].line2, 360 / 2);
            modbusTCPSend(_dicSiloData["S14"].line3, 400 / 2);
            modbusTCPSend(_dicSiloData["S14"].line4, 440 / 2);
            modbusTCPSend(_dicSiloData["S14"].line5, 400 / 2);
            modbusTCPSend(_dicSiloData["S14"].line6, 440 / 2);

            decimal[] d0 = new decimal[2];
            d0[0] = _wether.temperature;
            d0[1] = _wether.humidity;
            modbusTCPSend(d0, 480 / 2);

            decimal[] d1 = new decimal[6];
            d1[0] = _dicSiloData["S9"].siloVolume;
            d1[1] = _dicSiloData["S9"].cropVolume;
            d1[2] = _dicSiloData["S9"].cropWeight;
            d1[3] = _dicSiloData["S9"].cropLevelMin;
            d1[4] = _dicSiloData["S9"].cropLevelAvg;
            d1[5] = _dicSiloData["S9"].cropLevelMax;
            modbusTCPSend(d1, 488 / 2);

            decimal[] d2 = new decimal[6];
            d2[0] = _dicSiloData["S10"].siloVolume;
            d2[1] = _dicSiloData["S10"].cropVolume;
            d2[2] = _dicSiloData["S10"].cropWeight;
            d2[3] = _dicSiloData["S10"].cropLevelMin;
            d2[4] = _dicSiloData["S10"].cropLevelAvg;
            d2[5] = _dicSiloData["S10"].cropLevelMax;
            modbusTCPSend(d2, 516 / 2);

            decimal[] d3 = new decimal[6];
            d3[0] = _dicSiloData["S11"].siloVolume;
            d3[1] = _dicSiloData["S11"].cropVolume;
            d3[2] = _dicSiloData["S11"].cropWeight;
            d3[3] = _dicSiloData["S11"].cropLevelMin;
            d3[4] = _dicSiloData["S11"].cropLevelAvg;
            d3[5] = _dicSiloData["S11"].cropLevelMax;
            modbusTCPSend(d3, 544 / 2);

            decimal[] d4 = new decimal[6];
            d4[0] = _dicSiloData["S12"].siloVolume;
            d4[1] = _dicSiloData["S12"].cropVolume;
            d4[2] = _dicSiloData["S12"].cropWeight;
            d4[3] = _dicSiloData["S12"].cropLevelMin;
            d4[4] = _dicSiloData["S12"].cropLevelAvg;
            d4[5] = _dicSiloData["S12"].cropLevelMax;
            modbusTCPSend(d4, 544 / 2);

            decimal[] d5 = new decimal[6];
            d5[0] = _dicSiloData["S13"].siloVolume;
            d5[1] = _dicSiloData["S13"].cropVolume;
            d5[2] = _dicSiloData["S13"].cropWeight;
            d5[3] = _dicSiloData["S13"].cropLevelMin;
            d5[4] = _dicSiloData["S13"].cropLevelAvg;
            d5[5] = _dicSiloData["S13"].cropLevelMax;
            modbusTCPSend(d5, 544 / 2);

            decimal[] d6 = new decimal[6];
            d6[0] = _dicSiloData["S14"].siloVolume;
            d6[1] = _dicSiloData["S14"].cropVolume;
            d6[2] = _dicSiloData["S14"].cropWeight;
            d6[3] = _dicSiloData["S14"].cropLevelMin;
            d6[4] = _dicSiloData["S14"].cropLevelAvg;
            d6[5] = _dicSiloData["S14"].cropLevelMax;
            modbusTCPSend(d6, 544 / 2);

            //Moiture
            modbusTCPSend(_dicSiloData["S9"].mois, 588 / 2);
            modbusTCPSend(_dicSiloData["S10"].mois, 628 / 2);
            modbusTCPSend(_dicSiloData["S11"].mois, 668 / 2);
            modbusTCPSend(_dicSiloData["S12"].mois, 668 / 2);
            modbusTCPSend(_dicSiloData["S13"].mois, 668 / 2);
            modbusTCPSend(_dicSiloData["S14"].mois, 668 / 2);

        }

        // ====== 3A : register map เดิมทุกตำแหน่ง (เขียนเป็น loop) ======
        // Temp    : (siloIndex*7 + line)*20         -> S1 L1 = 0 ... S8 L7 = 1100
        // Weather : 1132
        // Volume  : 1139 + siloIndex*14              -> S1 = 1139 ... S8 = 1237
        // Moisture: 1251 + siloIndex*20              -> S1 = 1251 ... S8 = 1391
        private static readonly string[] _3A_Silos = { "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8" };
        private const int _3A_Lines = 7;

        private void sendData3A()
        {
            // Temp
            for (int si = 0; si < _3A_Silos.Length; si++)
            {
                silodata s;
                if (!_dicSiloData.TryGetValue(_3A_Silos[si], out s))
                {
                    Log("ไม่พบ Silo " + _3A_Silos[si] + " จาก API");
                    continue;
                }
                for (int li = 0; li < _3A_Lines; li++)
                    modbusTCPSend(s.GetLine(li), (si * _3A_Lines + li) * 20);
            }

            // Weather
            modbusTCPSend(new decimal[] { _wether.temperature, _wether.humidity }, 2264 / 2);

            // Volume / Weight / Level
            // (เดิม S7, S8 เขียนค่าลง d6 แต่ส่ง d7/d8 ที่ว่าง -> PLC ได้ 0 ตลอด)
            for (int si = 0; si < _3A_Silos.Length; si++)
            {
                silodata s;
                if (!_dicSiloData.TryGetValue(_3A_Silos[si], out s)) continue;
                decimal[] d = { s.siloVolume, s.cropVolume, s.cropWeight, s.cropLevelMin, s.cropLevelAvg, s.cropLevelMax };
                modbusTCPSend(d, 2278 / 2 + si * 14);
            }

            // Moisture
            for (int si = 0; si < _3A_Silos.Length; si++)
            {
                silodata s;
                if (!_dicSiloData.TryGetValue(_3A_Silos[si], out s)) continue;
                modbusTCPSend(s.mois, 2502 / 2 + si * 20);
            }
        }

        // ================= Login =================

        private async Task login()
        {
            const int stepDelay = 200; // เดิม Thread.Sleep(1000) ทุกขั้น

            if (!await API.login_agrolog().ConfigureAwait(false))
            {
                _isLogin = false;
                Log("Login fail . " + API._resTxt + " " + API._errTxt);
                EventMonitor.LoginFail((API._resTxt + " " + API._errTxt).Trim());
                return;
            }
            Log("Login complete.");

            bool ok = await API.get_customer_ID().ConfigureAwait(false);
            if (ok) { await Task.Delay(stepDelay).ConfigureAwait(false); ok = await API.post_asset_ID().ConfigureAwait(false); }
            if (ok) { await Task.Delay(stepDelay).ConfigureAwait(false); ok = await API.post_asset_List().ConfigureAwait(false); }
            if (!ok || _dicSiloData.Count == 0)
            {
                _isLogin = false;
                Log("Get asset fail . " + API._resTxt + " " + API._errTxt);
                EventMonitor.LoginFail(("Get asset fail " + API._resTxt + " " + API._errTxt).Trim());
                return;
            }

            await Task.Delay(stepDelay).ConfigureAwait(false);
            if (!await API.post_Weather_Device_ID().ConfigureAwait(false))
                Log("Get weather device fail . " + API._resTxt);

            int qty = SensorLineQty();
            foreach (silodata s in _dicSiloData.Values)
            {
                await Task.Delay(stepDelay).ConfigureAwait(false);
                if (!await API.post_Silo_Device_ID(s.siloname, qty).ConfigureAwait(false))
                    Log(s.siloname + " Get device fail . " + API._resTxt + " " + API._errTxt);
            }

            Log("Found " + _dicSiloData.Count + " silo(s).");
            _isLogin = true;
            EventMonitor.LoginOk();
        }

        // ================= Log window =================

        private void richTextBox1_TextChanged(object sender, EventArgs e)
        {
            // ตัดบรรทัดย้ายไปทำใน timer2_Tick (ทำครั้งเดียวต่อรอบ แทนทุกครั้งที่ข้อความเปลี่ยน)
        }

        private void timer2_Tick(object sender, EventArgs e)
        {
            if (_qStatus.IsEmpty) return;

            StringBuilder sb = new StringBuilder();
            string msg;
            while (_qStatus.TryDequeue(out msg))
                sb.Append(msg).Append("\r\n");

            // เดิมใช้ richTextBox1.Text += ... (สร้าง string ใหม่ทั้งก้อนทุกบรรทัด กิน CPU/RAM)
            richTextBox1.AppendText(sb.ToString());

            int excess = richTextBox1.Lines.Length - MaxLogLines;
            if (excess > 0)
            {
                int cut = richTextBox1.GetFirstCharIndexFromLine(excess);
                if (cut > 0)
                {
                    richTextBox1.Select(0, cut);
                    richTextBox1.SelectedText = "";
                }
            }

            richTextBox1.SelectionStart = richTextBox1.TextLength;
            richTextBox1.ScrollToCaret();
        }

        // ================= Modbus =================

        private static ushort[] ToRegisters(decimal[] pValue)
        {
            ushort[] registers = new ushort[pValue.Length * 2];
            for (int i = 0; i < pValue.Length; i++)
            {
                byte[] bytes = BitConverter.GetBytes((float)pValue[i]);
                // ให้ผลเท่ากับโค้ดเดิม: BitConverter.ToUInt16(new byte[] { bytes[3], bytes[2] }, 0)
                registers[i * 2] = (ushort)(bytes[3] | (bytes[2] << 8));     // High word
                registers[i * 2 + 1] = (ushort)(bytes[1] | (bytes[0] << 8)); // Low word
            }
            return registers;
        }

        private void modbusTCPSend(decimal[] pValue, int startAddr)
        {
            if (_mb == null || _mbFailed || pValue == null) return;
            try
            {
                _mb.WriteMultipleRegisters(1, startAddr, ToRegisters(pValue));
                if (_verboseLog) Log("Send MBTCP Start Address " + startAddr);
            }
            catch (Exception ex)
            {
                // หยุดส่งบล็อกที่เหลือในรอบนี้ (ไม่ต้องรอ timeout ซ้ำ ๆ อีก 70 ครั้ง)
                _mbFailed = true;
                _mbError = "Send at " + startAddr + ": " + ex.Message;
                Log("Send MBTCP Fail at " + startAddr + " : " + ex.Message);
            }
        }

        private void mbTCPRcv()
        {
            int start, length;
            if (_siteName == "EX3A") { start = 708 / 2; length = 9; }
            else if (_siteName == "EX3B") { start = 1260 / 2; length = 16; }
            else if (_siteName == "1B") { start = 1260 / 2; length = 18; }
            else if (_siteName == "3A") { start = 2854 / 2; length = 16; }
            else return;

            try
            {
                Span<short> res = _mb.ReadHoldingRegisters<short>(0, start, length);
                int n = Math.Min(Math.Min(res.Length, length), _fandata.Length);
                for (int j = 0; j < n; j++)
                {
                    // สลับ byte (เหมือนเดิม)
                    ushort u = (ushort)res[j];
                    short v = (short)((u >> 8) | ((u & 0xFF) << 8));
                    _fandata[j].checkStatus(v);
                }
                Log("Read MBTCP Fan Status");
            }
            catch (Exception err)
            {
                _mbFailed = true;
                _mbError = "Read fan status: " + err.Message;
                Log("Read MBTCP Fail " + err.Message);
            }
        }
    }
}
