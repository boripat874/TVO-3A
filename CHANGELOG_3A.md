# TVO 3A – สิ่งที่แก้ไข (7 พ.ย. 2026)

## TVO API Reader
### แก้บั๊ก
- PLC ได้ Volume/Weight/Level ของ S7, S8 = 0 ตลอด (เดิมเขียนค่าลง d6 แต่ส่ง d7/d8) → แก้แล้ว
- EX3B: S29 ส่งค่า Volume ของ S28 (ส่ง d3 แทน d4) → แก้แล้ว
- การอ่าน API กับการส่ง PLC ไม่ได้รอกัน (async ไม่ได้ await) → ตอนนี้ทำทีละขั้น อ่านครบ → ส่ง PLC → บันทึก DB
- Token หมดอายุแล้วไม่ login ใหม่ (ได้ค่า 0 ทุกตัว) → ถ้าเจอ 401/403 จะ login ใหม่แล้วอ่านซ้ำทันที
- ไฟล์ DB ไปอยู่ตาม working directory (connectionString คำนวณก่อนตั้ง path) → ใช้ path ข้าง .exe เสมอ
- Login ซ้ำแล้ว error "key ซ้ำ" ใน _dicSiloData → อัปเดต ID ของ silo เดิมแทน
- Login: เช็ก json["token"] ผิดตัวตอนได้ message กลับมา → แก้แล้ว
- ชื่อ device ที่ลงท้ายเลขหลักเดียวทำให้ map สายทั้ง silo ล้ม → ใช้ regex หาเลขท้ายชื่อ
- Log queue / error text ใช้ข้าม thread ไม่ปลอดภัย → ใช้ ConcurrentQueue, error แยกต่อ request
- DB: Avg/Min/Max ไม่นับค่า 0 (อ่านไม่ได้/ข้อมูลเก่า) → Min ไม่เป็น 0.0 อีก (ค่า S1..S8 ดิบยังเก็บเหมือนเดิม)

### เร็วขึ้น / ใช้ RAM น้อยลง
- HttpClient ตัวเดียวทั้งโปรแกรม (เดิมสร้างใหม่ ~70 ครั้ง/รอบ) + ปลดล็อกจำกัด 2 connection
- อ่าน API ขนานกัน (ค่า apiParallel ใน App.config, ค่าเริ่มต้น 4)
- สาย 1 อ่าน temperature + moisture ใน request เดียว (ลดจาก 2 เหลือ 1)
- Modbus เปิด connection เดียวต่อรอบ (เดิมเปิด/ปิด 73 ครั้ง) ถ้าส่งล้มจะหยุดทันทีไม่รอ timeout ซ้ำ
- Login: delay ระหว่างขั้น 1000 ms → 200 ms
- หน้าต่าง log ใช้ AppendText + ตัดบรรทัดครั้งเดียว (เดิม Text += สร้าง string ใหม่ทุกบรรทัด)
- Log แสดงเฉพาะสรุป/ข้อผิดพลาด (ตั้ง verboseLog=true เพื่อดูค่าทุกสายเหมือนเดิม)
- SQLite: WAL mode + index ตาม LogTime/Siloname (สร้างอัตโนมัติรอบแรก – DB ใหญ่อาจใช้เวลาสักครู่ครั้งเดียว)
- silodata เก็บสายใน array (line1..line56 ยังใช้ได้เหมือนเดิม)

### ไม่เปลี่ยน
- Modbus register map ของ 3A ทุกตำแหน่ง (ตรวจแล้ว 73 บล็อกตรงกับเดิม) และรูปแบบ float/word เหมือนเดิม
- โครงสร้างตาราง DB เดิม (ไฟล์ DB เดิมใช้ต่อได้)

### App.config ใหม่
- `apiParallel` = 4, `verboseLog` = false

## TVO_Report
- Sensor Detail: เปิด virtualization ของ DataGrid (เดิมอยู่ใน ScrollViewer → สร้างทุกแถว กิน RAM และค้าง)
- Query 5 ส่วนรันพร้อมกัน + ใช้ index ที่ API Reader สร้าง
- ทุกตารางใช้รอบ 08:00–08:00 (เดิม Weather ใช้ 00:00–24:00) และไม่รวมขอบปลาย (เดิม BETWEEN)
- ไม่นับค่า 0 ใน Avg/Min/Max (รวมข้อมูลเก่าใน DB ด้วย)
- ลด RAM ของ Detail (string ซ้ำใช้ตัวเดียวกัน), ปิด reader/command ทุกตัว
- dbPath ตั้งได้ใน App.config (ค่าเริ่มต้น C:/TVO API Reader/TVOData.db)
- CSV: เพิ่มตาราง Silo Weight, เขียน Detail เร็วขึ้น
- เรียงไซโล S1, S2 … ตามตัวเลข

## ยังไม่ได้แตะ (ต้องการข้อมูลเพิ่ม)
- sendData1B: address ซ้ำกันหลายบล็อก – ต้องมี Modbus map ของ 1B

---

# Monitor Log (EventLog) – 7 พ.ย. 2026 (รอบ 2)

## TVO API Reader
- ตารางใหม่ `EventLog` ใน TVOData.db (สร้างอัตโนมัติ, ไม่แตะตารางเดิม)
  คอลัมน์: LogTime, Category, Level, Source, Event, Detail, DurationSec
- บันทึกเฉพาะตอน "เริ่มมีปัญหา" และ "กลับมาใช้งานได้" (พร้อมระยะเวลา) ไม่บันทึกซ้ำทุกรอบ
- เหตุการณ์
  - SYSTEM : APP_START, APP_STOP (พร้อมสาเหตุ: ผู้ใช้ปิด / Windows Shutdown / Task Manager), APP_ABNORMAL_EXIT
  - API    : API_LOGIN_FAIL / API_LOGIN_OK, API_DISCONNECTED (อ่านไม่ได้ทั้งรอบ) / API_RECONNECTED
  - PLC    : PLC_DISCONNECTED / PLC_RECONNECTED
  - DATA   : LINE_READ_FAIL / LINE_READ_OK (รายสาย), TEMP_ZERO / TEMP_RECOVER, MOIST_ZERO / MOIST_RECOVER,
             WEATHER_TEMP_ZERO / WEATHER_TEMP_RECOVER, WEATHER_HUM_ZERO / WEATHER_HUM_RECOVER
- Weight = 0 ไม่บันทึก
- ตรวจเฉพาะจุดที่มีจริง: สาย 1 = 8 จุด, สาย 2+ = 7 จุด, moisture = 8 จุด
- ระหว่าง API หลุดทั้งรอบ ไม่ตรวจค่า 0 / รายสาย ; สายที่อ่านไม่ได้บันทึก LINE_READ_FAIL อย่างเดียว
- Detail ระบุจุดและสาเหตุ เช่น "จุด P4, P7 = 0 (ข้อมูลเก่าเกิน 4 ชม.: P4 ; ค่า 0 จาก API: P7)"
  ถ้ารายการจุดที่เป็น 0 เปลี่ยน จะบันทึกแถวใหม่
- ลบ log เก่ากว่า `eventLogKeepDays` (ค่าเริ่มต้น 365) วันละครั้ง
- หมายเหตุ: สถานะเก็บในหน่วยความจำ ถ้าปิด/เปิดโปรแกรมระหว่างที่ปัญหายังอยู่ จะบันทึกปัญหานั้นใหม่อีกครั้งหลังเปิด

ไฟล์: EventMonitor.cs (ใหม่), API.cs, MainUI.cs, MainUI.Designer.cs (FormClosing), SQLiteHelper.cs,
TVO API Reader.csproj, App.config

## TVO_Report – แท็บ "Monitor Log"
- กรอง: วันที่จาก–ถึง (08:00 ของวันเริ่ม ถึง 08:00 ของวันถัดจากวันสิ้นสุด), Category, Level, Source, ค้นหา (Enter ได้)
- สรุปทั้งช่วง: API หลุด (ครั้ง / เวลารวม), Login ไม่ผ่าน, PLC หลุด (ครั้ง / เวลารวม), สายอ่านไม่ได้,
  Temp = 0, Moist = 0, Weather = 0, เปิด / ปิด / ปิดผิดปกติ
  (เวลารวมนับจากแถวที่กลับมาใช้งานได้ภายในช่วงที่เลือก)
- ตาราง: ใหม่สุดอยู่บน, ERROR สีแดง, WARN สีเหลือง, virtualization
- Export Log CSV (ตามตัวกรอง)
- ถ้า DB ยังไม่มีตาราง EventLog จะแจ้งให้อัปเดต TVO API Reader ก่อน

ไฟล์: MainWindow.xaml, MainWindow.xaml.cs, Class1.cs
