# Trickster Quest Sender — Windows

เวอร์ชัน Windows (C# / .NET 8 / WPF) ทำงานแบบเดียวกับแอป Mac:
**หา NPC ด้วยชื่อ → กด 1 จนจบบทสนทนา → คลิกพื้นที่ว่างเพื่อปิดเควส → วนใหม่**
ไฟล์ preset ใช้ JSON รูปแบบเดียวกับแอป Mac

```
windows/
  TricksterQuestSender.Core/        ตรรกะทั้งหมด (ไม่ผูกกับระบบ) — รันได้ทุก OS
  TricksterQuestSender.Core.Tests/  ชุดทดสอบ (มีเกมจำลอง: เซิร์ฟช้า, กดหาย, ข้อความเปลี่ยน ฯลฯ)
  TricksterQuestSender.Windows/     แอป WPF: คลิก/กดปุ่ม (SendInput), อ่านจอ (Windows OCR), F8/Esc, แท็บ
```

## ทดสอบบน Mac (ไม่ต้องมี Windows)
```bash
brew install --cask dotnet-sdk          # ครั้งแรกครั้งเดียว
dotnet test windows/TricksterQuestSender.Core.Tests     # รันชุดทดสอบตรรกะ
dotnet build windows/TricksterQuestSender.Windows       # เช็กว่าแอป Windows คอมไพล์ผ่าน (รันบน Mac ไม่ได้)
```
ลองกับเกมจริง: ใช้ Windows 11 ARM ใน VM (UTM / VMware Fusion / Parallels) แล้วติดตั้งจาก Setup.exe

## ออกเวอร์ชันใหม่ (อัปเดตอัตโนมัติด้วย Velopack + GitHub Releases)
ครั้งแรก:
1. สร้าง repo บน GitHub (**public** — แอปจะเช็กอัปเดตจาก Releases ของ repo นี้)
2. ในโฟลเดอร์โปรเจกต์:
   ```bash
   git init && git add . && git commit -m "Trickster Quest Sender"
   git remote add origin https://github.com/<you>/<repo>.git
   git push -u origin main
   ```
ทุกครั้งที่จะปล่อยเวอร์ชัน:
```bash
git tag v1.0.0 && git push origin v1.0.0
```
GitHub Actions (`.github/workflows/windows-release.yml`) จะรันเทสต์ → build → ทำ `TricksterQuestSender-win-Setup.exe` → อัปโหลดขึ้น Releases ให้เอง
ไม่ต้องมีเครื่อง Windows

## คนใช้ Windows
1. ดาวน์โหลด `...-Setup.exe` จากหน้า Releases แล้วเปิด (ติดตั้งเอง ไม่ต้องใช้ admin)
2. ครั้งแรก Windows อาจขึ้น “Windows protected your PC” → **More info → Run anyway** (เพราะไฟล์ยังไม่ได้เซ็น)
3. ให้ OCR อ่านไทยได้: Settings → Time & language → Language & region → เพิ่ม **ภาษาไทย**
4. เปิดแอปครั้งต่อไป ถ้ามีเวอร์ชันใหม่จะขึ้นแถบเขียว “อัปเดตแล้วเปิดใหม่”

## ใช้งาน
- **+** (Ctrl+T) = แท็บใหม่ว่างเปล่า ชื่อ `untitled-N` → เลือก **เพิ่มขั้นตอนใหม่** หรือ **บันทึกการเล่น**
- บันทึกการเล่น: เล่นในเกม 1 รอบ (คลิก NPC → กด 1 จนจบ → คลิกพื้นที่ว่างปิดเควส) แล้วกด **F8**
  แอปสร้างขั้นตอนให้ในแท็บนั้น พร้อม **ภาพของแต่ละหน้าบทสนทนา**
- แท็บที่มีขั้นตอนแล้ว: ปุ่ม **● เริ่มบันทึกการเล่นใหม่** (Ctrl+Shift+R) แทนที่ขั้นตอนเดิม (เมื่อบันทึกสำเร็จเท่านั้น)
- ตอนรัน ขั้น "กดจนจบบทสนทนา" ตัดสินหน้าจาก **ภาพก่อน** ข้อความเป็นตัวสำรอง · หน้าใหม่ต้องเห็น 2 ครั้งติดก่อนกด ·
  เจอหน้าที่ไม่ได้บันทึก = หยุด · หน้าสุดท้ายกดครั้งเดียว
- แท็บแบบเบราว์เซอร์: คลิก = สลับ · ดับเบิลคลิก = เปลี่ยนชื่อ · ✕ = ปิด · คลิกขวา = เมนู
- คีย์ลัด: Ctrl+T · Ctrl+Shift+T เปิดแท็บที่ปิดคืน · Ctrl+W · Ctrl+Tab · Ctrl+1–9 · Ctrl+Shift+R บันทึกใหม่
- **F8** เริ่ม/หยุด (และหยุดบันทึก) · **Esc** หยุด / ยกเลิกบันทึก / ยกเลิกการเริ่มอัตโนมัติ
- หน้าต่างแอปถูกซ่อนจากการจับภาพหน้าจอ และคลิก/กดในหน้าต่างแอปไม่ถูกบันทึก

## หมายเหตุ
- preset ใช้ JSON เดียวกับแอป Mac ได้ แต่ภาพหน้าที่บันทึกบน Mac อาจไม่ตรงกับบน Windows (ย่อภาพต่างวิธี)
  แอปจะใช้ข้อความแทนแล้วจำภาพใหม่ระหว่างรัน — แนะนำให้บันทึกการเล่นใหม่บนเครื่องที่ใช้จริง
