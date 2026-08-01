# Debug Script - Ki?m tra Server Kh?i Ch?y

Khi không th?y server ch?y, hãy ch?y các l?nh này qua ADB ?? debug:

## 1. Ki?m tra file jar ?ã ???c push lên ch?a
```bash
adb -s demoai.ddns.net:5554 shell "ls -lah /data/local/tmp/scrcpy-server.jar"
```
- N?u **file không t?n t?i** ? push th?t b?i
- N?u **file có nh?ng size = 0** ? push b? interrupt
- N?u **file t?n t?i + có size** ? OK, ti?p t?c b??c 2

## 2. Ki?m tra script ?ã ???c push lên ch?a
```bash
adb -s demoai.ddns.net:5554 shell "ls -lah /data/local/tmp/start-scrcpy.sh"
```
- N?u không t?n t?i ho?c size = 0 ? script push b? l?i
- Ki?m tra n?i dung script:
```bash
adb -s demoai.ddns.net:5554 shell "cat /data/local/tmp/start-scrcpy.sh"
```

## 3. Ki?m tra quy?n execute c?a script
```bash
adb -s demoai.ddns.net:5554 shell "chmod +x /data/local/tmp/start-scrcpy.sh"
adb -s demoai.ddns.net:5554 shell "ls -l /data/local/tmp/start-scrcpy.sh"
```
Nên th?y `-rwxrwx---` ho?c t??ng t? (ph?n ??u có `x`)

## 4. Ch?y script th? công và xem log
```bash
adb -s demoai.ddns.net:5554 shell "sh /data/local/tmp/start-scrcpy.sh"
```
Ch? 1 giây, sau ?ó xem log:
```bash
adb -s demoai.ddns.net:5554 shell "cat /data/local/tmp/scrcpy-server.log"
```

## 5. Ki?m tra ti?n trình server có ch?y không
```bash
adb -s demoai.ddns.net:5554 shell "ps -A | grep app_process"
```
Nên th?y 1 dòng có ch?a `com.genymobile.scrcpy.Server`

## 6. Ki?m tra socket ?ã ???c t?o hay ch?a
```bash
adb -s demoai.ddns.net:5554 shell "netstat -ln | grep scrcpy"
```
ho?c (trên Android 10+):
```bash
adb -s demoai.ddns.net:5554 shell "ss -ln | grep scrcpy"
```
Nên th?y socket `localabstract:scrcpy_<scid>` (ví d? `scrcpy_12345678`)

## 7. N?u server crash, xem chi ti?t l?i
```bash
adb -s demoai.ddns.net:5554 shell "cat /data/local/tmp/scrcpy-server.log"
```

## 8. N?u port forward b? k?t, xóa t?t c? forward c?
```bash
adb -s demoai.ddns.net:5554 forward --remove-all
```

## Các l?i th??ng g?p:

### ? "Error: app_process: not found"
- Thi?t b? B không ph?i Android (ho?c b?n Android quá c?)
- Ho?c app_process b? xóa/s?a ??i (ph?n m?m cleanup)

### ? "Error: class not found com.genymobile.scrcpy.Server"
- File jar b? h?ng ho?c push không ?úng
- Version trong ScrcpyVersion (code C#) không kh?p v?i version jar

### ? "Error: permission denied"
- Ki?m tra l?i quy?n: `chmod +x /data/local/tmp/start-scrcpy.sh`

### ? Không th?y socket t?o ra
- Server ch?y nh?ng crash ngay sau
- Xem log file ?? bi?t lý do crash

### ? "adb: command not found" (khi push qua PC)
- ??m b?o ?ã add adb.exe vào PATH ho?c dùng ???ng d?n ??y ??

## N?u v?n không fix ???c:

1. Xóa toàn b? file c?:
```bash
adb -s demoai.ddns.net:5554 shell "rm -f /data/local/tmp/scrcpy-server.* /data/local/tmp/start-scrcpy.sh"
```

2. Ki?m tra l?i app có quy?n INTERNET + các quy?n c?n thi?t không (xem AndroidManifest.xml)

3. ??m b?o thi?t b? B không b?t mode WiFi-only ho?c ch? ?? ti?t ki?m pin c?c ?oan

4. Th? trên thi?t b? B khác ho?c qua USB cable (c?m tr?c ti?p) thay vì WiFi ?? lo?i tr? v?n ?? m?ng
