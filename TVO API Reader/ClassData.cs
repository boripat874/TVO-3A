using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace TVO_API_Reader
{
    public class DeviceRoot
    {
        public IdInfo Id { get; set; }
        public long CreatedTime { get; set; }
        public object AdditionalInfo { get; set; }
        public IdInfo TenantId { get; set; }
        public IdInfo CustomerId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Label { get; set; }
    }

    public class WeatherStation
    {
        public string weatherID { get; set; }
        public decimal temperature { get; set; }
        public decimal humidity { get; set; }

        public WeatherStation()
        {
            weatherID = "";
            temperature = 0;
            humidity = 0;
        }

        public WeatherStation(string pID)
        {
            weatherID = pID;
            temperature = 0;
            humidity = 0;
        }
    }

    public class IdInfo
    {
        public string EntityType { get; set; }
        public string Id { get; set; }
    }

    public class silodata
    {
        public const int MaxLines = 56;      // รองรับสูงสุด 56 สาย (เท่าของเดิม)
        public const int MaxLineId = 12;     // จำนวน device line ที่ map จาก API ได้
        public const int PointsPerLine = 9;  // จุดวัดต่อสาย

        public string siloname { get; set; }
        public string siloID { get; set; }
        public string[] line_id { get; set; }

        // เก็บค่าทุกสายใน array เดียว แทน property 56 ตัว (ลดโค้ด + ใช้ index ได้)
        // สายที่ยังไม่ใช้จะเป็น null (ไม่จองหน่วยความจำ)
        public readonly decimal[][] lines = new decimal[MaxLines][];

        // property เดิม line1..line56 ยังใช้ได้เหมือนเดิม (ชี้เข้า lines[])
        public decimal[] line1 { get { return lines[0]; } set { lines[0] = value; } }
        public decimal[] line2 { get { return lines[1]; } set { lines[1] = value; } }
        public decimal[] line3 { get { return lines[2]; } set { lines[2] = value; } }
        public decimal[] line4 { get { return lines[3]; } set { lines[3] = value; } }
        public decimal[] line5 { get { return lines[4]; } set { lines[4] = value; } }
        public decimal[] line6 { get { return lines[5]; } set { lines[5] = value; } }
        public decimal[] line7 { get { return lines[6]; } set { lines[6] = value; } }
        public decimal[] line8 { get { return lines[7]; } set { lines[7] = value; } }
        public decimal[] line9 { get { return lines[8]; } set { lines[8] = value; } }
        public decimal[] line10 { get { return lines[9]; } set { lines[9] = value; } }
        public decimal[] line11 { get { return lines[10]; } set { lines[10] = value; } }
        public decimal[] line12 { get { return lines[11]; } set { lines[11] = value; } }
        public decimal[] line13 { get { return lines[12]; } set { lines[12] = value; } }
        public decimal[] line14 { get { return lines[13]; } set { lines[13] = value; } }
        public decimal[] line15 { get { return lines[14]; } set { lines[14] = value; } }
        public decimal[] line16 { get { return lines[15]; } set { lines[15] = value; } }
        public decimal[] line17 { get { return lines[16]; } set { lines[16] = value; } }
        public decimal[] line18 { get { return lines[17]; } set { lines[17] = value; } }
        public decimal[] line19 { get { return lines[18]; } set { lines[18] = value; } }
        public decimal[] line20 { get { return lines[19]; } set { lines[19] = value; } }
        public decimal[] line21 { get { return lines[20]; } set { lines[20] = value; } }
        public decimal[] line22 { get { return lines[21]; } set { lines[21] = value; } }
        public decimal[] line23 { get { return lines[22]; } set { lines[22] = value; } }
        public decimal[] line24 { get { return lines[23]; } set { lines[23] = value; } }
        public decimal[] line25 { get { return lines[24]; } set { lines[24] = value; } }
        public decimal[] line26 { get { return lines[25]; } set { lines[25] = value; } }
        public decimal[] line27 { get { return lines[26]; } set { lines[26] = value; } }
        public decimal[] line28 { get { return lines[27]; } set { lines[27] = value; } }
        public decimal[] line29 { get { return lines[28]; } set { lines[28] = value; } }
        public decimal[] line30 { get { return lines[29]; } set { lines[29] = value; } }
        public decimal[] line31 { get { return lines[30]; } set { lines[30] = value; } }
        public decimal[] line32 { get { return lines[31]; } set { lines[31] = value; } }
        public decimal[] line33 { get { return lines[32]; } set { lines[32] = value; } }
        public decimal[] line34 { get { return lines[33]; } set { lines[33] = value; } }
        public decimal[] line35 { get { return lines[34]; } set { lines[34] = value; } }
        public decimal[] line36 { get { return lines[35]; } set { lines[35] = value; } }
        public decimal[] line37 { get { return lines[36]; } set { lines[36] = value; } }
        public decimal[] line38 { get { return lines[37]; } set { lines[37] = value; } }
        public decimal[] line39 { get { return lines[38]; } set { lines[38] = value; } }
        public decimal[] line40 { get { return lines[39]; } set { lines[39] = value; } }
        public decimal[] line41 { get { return lines[40]; } set { lines[40] = value; } }
        public decimal[] line42 { get { return lines[41]; } set { lines[41] = value; } }
        public decimal[] line43 { get { return lines[42]; } set { lines[42] = value; } }
        public decimal[] line44 { get { return lines[43]; } set { lines[43] = value; } }
        public decimal[] line45 { get { return lines[44]; } set { lines[44] = value; } }
        public decimal[] line46 { get { return lines[45]; } set { lines[45] = value; } }
        public decimal[] line47 { get { return lines[46]; } set { lines[46] = value; } }
        public decimal[] line48 { get { return lines[47]; } set { lines[47] = value; } }
        public decimal[] line49 { get { return lines[48]; } set { lines[48] = value; } }
        public decimal[] line50 { get { return lines[49]; } set { lines[49] = value; } }
        public decimal[] line51 { get { return lines[50]; } set { lines[50] = value; } }
        public decimal[] line52 { get { return lines[51]; } set { lines[51] = value; } }
        public decimal[] line53 { get { return lines[52]; } set { lines[52] = value; } }
        public decimal[] line54 { get { return lines[53]; } set { lines[53] = value; } }
        public decimal[] line55 { get { return lines[54]; } set { lines[54] = value; } }
        public decimal[] line56 { get { return lines[55]; } set { lines[55] = value; } }

        public decimal[] mois { get; set; }

        public decimal siloVolume { get; set; }
        public decimal cropVolume { get; set; }
        public decimal cropWeight { get; set; }
        public decimal cropLevelMax { get; set; }
        public decimal cropLevelMin { get; set; }
        public decimal cropLevelAvg { get; set; }

        public silodata(string pName) : this(pName, "") { }

        public silodata(string pName, string pID)
        {
            siloname = pName;
            siloID = pID;
            line_id = new string[MaxLineId];
            mois = new decimal[PointsPerLine];
            for (int i = 0; i < MaxLineId; i++)
                lines[i] = new decimal[PointsPerLine];

            siloVolume = 0;
            cropVolume = 0;
            cropWeight = 0;
            cropLevelMax = 0;
            cropLevelMin = 0;
            cropLevelAvg = 0;
        }

        /// <summary>คืนค่า array ของสาย (ถ้ายังไม่มีให้คืน array ศูนย์ เพื่อไม่ให้ null)</summary>
        public decimal[] GetLine(int index)
        {
            decimal[] l = lines[index];
            if (l == null)
            {
                l = new decimal[PointsPerLine];
                lines[index] = l;
            }
            return l;
        }
    }

    public class fanData
    {
        public string siloname { get; set; }
        public string FanName { get; set; }
        public short status { get; set; }
        public string statusTxt { get; set; }

        public fanData(string siloname, string fanName)
        {
            this.siloname = siloname;
            FanName = fanName;
            status = 99;
            statusTxt = "";
        }

        public void checkStatus(short pStatus)
        {
            if (status != pStatus)
            {
                status = pStatus;

                if (status == 0)
                {
                    statusTxt = "Stop";
                }
                else if (status == 1)
                {
                    statusTxt = "Running";
                }
                else if (status == 2)
                {
                    statusTxt = "Fault";
                }
                else if (status == 3)
                {
                    statusTxt = "Timeout";
                }
                else if (status == 4)
                {
                    statusTxt = "Fault & Timeout";
                }
                else if (status == 5)
                {
                    statusTxt = "Emergency";
                }
                else if (status == 6)
                {
                    statusTxt = "Safety Interlock";
                }

                SQLiteHelper.insertFanLog(this);
            }
        }
        
    }

}
