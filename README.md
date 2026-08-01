# MauiRemoteAdb

Ứng dụng .NET MAUI (Android) điều khiển từ xa một thiết bị Android khác qua ADB,
theo cơ chế của scrcpy (stream màn hình H.264 + inject input ngược).

## ⚠️ Đính chính quan trọng — bắt buộc đọc trước khi build

Bản viết ban đầu có 1 sai sót kiến trúc: `AdvancedSharpAdbClient` **không tự thực hiện
giao thức ADB với thiết bị** — nó chỉ là 1 client nói chuyện với **ADB Host Server**
(bình thường do `adb.exe` chạy trên PC cung cấp, lắng nghe ở `127.0.0.1:5037`). Vì app
này chạy trên điện thoại Android chứ không phải PC, không có sẵn `adb.exe` nào — nên nếu
không xử lý gì thêm, app sẽ luôn báo lỗi `Connection refused 127.0.0.1:5037`.

**Cách khắc phục:** phải tự bundle 1 file thực thi `adb` biên dịch cho kiến trúc
ARM/Android, rồi tự khởi động nó **làm tiến trình nền ngay trong app** bằng
`System.Diagnostics.Process` (đã viết sẵn trong
`AdbConnectionService.EnsureLocalAdbServerStartedAsync()`) trước khi gọi bất kỳ API
ADB nào khác. Xem Bước 0 bên dưới.

⚠️ **Lưu ý thêm:** ban đầu mình định dùng hàm `AdbServer.StartServer()` có sẵn của
thư viện, nhưng hàm đó có đoạn kiểm tra hệ điều hành chỉ chấp nhận Windows/macOS/Linux
(theo cách .NET runtime nhận diện OS) — Android không được xem là "Linux" trong kiểm
tra này dù chạy chung nhân Linux, nên luôn báo lỗi *"SharpAdbClient only supports
launching adb on Windows, Mac OS and Linux"*. Code hiện tại đã bỏ qua hàm đó, tự chạy
binary bằng `Process.Start` trực tiếp thay thế.

## Bước 0 — Lấy binary `adb` cho ARM Android qua Termux (bắt buộc, làm trước Bước 1)

**0.1. Cài Termux từ F-Droid** (không dùng bản Play Store, đã ngừng cập nhật):
`https://f-droid.org/packages/com.termux/`

**0.2. Cài package `android-tools`:**
```bash
pkg update && pkg upgrade -y
pkg install android-tools -y
```

**0.3. Gom file `adb` + toàn bộ `.so` trong `$PREFIX/lib`, nén lại thành 1 file zip:**
```bash
mkdir -p ~/adb-export
cp $(which adb) ~/adb-export/adb
cp $PREFIX/lib/*.so ~/adb-export/ 2>/dev/null
cp $PREFIX/lib/*.so.* ~/adb-export/ 2>/dev/null

# Nếu có cài python/các gói khác không liên quan tới adb, xóa bớt cho gọn (không bắt buộc)
rm -f ~/adb-export/libpython*.so

cd ~
pkg install zip -y
zip -r adb-export.zip adb-export/
```

**0.4. Tải file zip về máy tính:**
```bash
python -m http.server 8080
```
Mở trình duyệt máy tính (cùng Wi-Fi điện thoại): `http://<IP-điện-thoại>:8080/adb-export.zip`
→ tải về, giải nén.

**0.5. Đặt vào project, đổi tên đúng quy ước:**
```
Platforms/Android/Resources/lib/arm64-v8a/
```
- Đổi tên `adb` → `libadb.so` (bắt buộc, vì đây mới là file thực thi chính)
- **File `libz.so.1` và `libzstd.so.1` giữ NGUYÊN tên, không đổi thành `.so`** —
  đây là *SONAME* thật đã ghi cứng vào `adb` (và các thư viện phụ thuộc nó) lúc
  biên dịch, đổi tên sẽ khiến không tìm thấy thư viện lúc chạy dù file vẫn nằm
  đúng chỗ.
- Toàn bộ file đuôi `.so` khác (kể cả hàng trăm file `libabsl_*.so` do `protobuf`
  kéo theo) giữ nguyên tên, đặt hết vào đúng thư mục trên — `.csproj` đã khai báo
  sẵn bằng wildcard `*.so`, không cần sửa gì thêm khi có thêm/bớt file.

Lưu ý bắt buộc:
- Đặt đúng thư mục `Resources/lib/{abi}/` để công cụ AAPT nhận diện và đóng gói vào
  `nativeLibraryDir` của app — đây là vị trí **duy nhất** được Android cho phép app tự
  exec() 1 binary do chính app bundle, kể từ Android 10 trở lên (chính sách W^X chặn
  exec từ các thư mục ghi được khác như thư mục dữ liệu riêng của app).
- ABI của binary lấy từ Termux khớp với CPU của chính điện thoại bạn cài Termux — nếu
  cần hỗ trợ thêm kiến trúc khác (armeabi-v7a, x86_64...), lặp lại toàn bộ quy trình
  trên bằng 1 điện thoại/máy ảo có CPU tương ứng. Đơn giản hơn, có thể giới hạn app chỉ
  chạy trên `arm64-v8a` (đa số điện thoại hiện nay) bằng cách thêm vào `.csproj`:
  ```xml
  <RuntimeIdentifiers>android-arm64</RuntimeIdentifiers>
  ```
- Code trong `AdbConnectionService.EnsureLocalAdbServerStartedAsync()` đã tự set biến
  môi trường `LD_LIBRARY_PATH` trỏ về `nativeLibraryDir` trước khi khởi động `adb`, để
  nó tìm thấy các `.so` phụ thuộc đặt cùng chỗ.

## Cấu trúc

```
MauiRemoteAdb/
├─ MauiRemoteAdb.csproj
├─ MauiProgram.cs
├─ App.xaml / App.xaml.cs
├─ Services/
│  ├─ AdbConnectionService.cs     # connect/pair/push/forward qua AdvancedSharpAdbClient
│  ├─ ScrcpyServerLauncher.cs     # push scrcpy-server.jar + build lệnh khởi chạy
│  ├─ ScrcpyVideoClient.cs        # đọc socket video, parse header + frame H264
│  ├─ ScrcpyControlProtocol.cs    # encode message touch/key/scroll gửi ngược
│  └─ RemoteSessionManager.cs     # điều phối toàn bộ pipeline 1 phiên remote
├─ Views/
│  ├─ ConnectPage.xaml(.cs)       # UI kết nối qua adb tcpip (IP:Port)
│  ├─ RemoteScreenPage.xaml(.cs)  # UI hiển thị màn hình + nút Back/Home/Recents
│  └─ RemoteSurfaceView.cs        # view trừu tượng, ánh xạ sang SurfaceView native
├─ Platforms/Android/
│  ├─ VideoDecoder.cs             # MediaCodec giải mã H264 phần cứng
│  ├─ RemoteSurfaceViewHandler.cs # Handler MAUI -> Android.Views.SurfaceView
│  └─ AndroidManifest.xml         # quyền INTERNET...
└─ Resources/Raw/scrcpy-server.jar  # (bạn tự thêm — xem bước 1 bên dưới)
```

## Bước 1 — Lấy scrcpy-server.jar

File này **không** có link tải riêng — nó nằm bên trong bản release desktop của scrcpy.

1. Tải bản release chính thức (chỉ từ GitHub, không tải từ site khác):
   `https://github.com/Genymobile/scrcpy/releases` — ví dụ `scrcpy-win64-v4.1.zip`
2. Giải nén, tìm file tên **`scrcpy-server`** (không có đuôi `.jar` trong bản release —
   xác nhận từ chính tác giả scrcpy tại github.com/Genymobile/scrcpy/issues/2506).
3. Đổi tên thành `scrcpy-server.jar`, copy vào `Resources/Raw/scrcpy-server.jar` trong project.
4. Mở `Services/ScrcpyServerLauncher.cs`, sửa `ScrcpyVersion` khớp đúng version bạn tải
   (mặc định code đang để `"4.1"`).

**Cách khác — build từ mã nguồn** (nếu không muốn tải bản desktop):
```bash
git clone https://github.com/Genymobile/scrcpy.git
cd scrcpy/server
./gradlew assembleRelease
# file .jar nằm ở server/build/outputs/apk/.../scrcpy-server.jar
```

⚠️ **Cảnh báo quan trọng:** phần đọc byte trong `ScrcpyVideoClient.cs` và
`ScrcpyControlProtocol.cs` được viết dựa theo tài liệu scrcpy ở giai đoạn ~2.x.
Từ bản 3.x/4.x, scrcpy đã đổi khá nhiều (thêm audio stream, camera capture, flex
display...). Mình đã thêm cờ `audio=false` để tránh server mở thêm socket ngoài dự
tính, nhưng bạn **nên đối chiếu lại** `server/src/main/java/com/genymobile/scrcpy/`
trong repo scrcpy đúng tag version mình dùng (ví dụ tag `v4.1`) trước khi chạy thật,
đặc biệt là class `DesktopConnection.java` (thứ tự mở socket) và `Server.java`
(danh sách tham số dòng lệnh) — vì đây là phần rủi ro cao nhất trong toàn bộ code mẫu.

⚠️ **Version trong `ScrcpyServerLauncher.cs` (`ScrcpyVersion`) phải khớp chính xác**
với version của jar — sai version server sẽ crash ngay khi start vì client gọi
đúng entrypoint theo protocol của version đó.

## Bước 2 — Build

```bash
dotnet workload install maui
dotnet restore
dotnet build -t:Run -f net8.0-android
```

## Bước 3 — Chuẩn bị thiết bị B (thiết bị bị điều khiển)

1. Bật Developer options → USB debugging.
2. Cắm dây USB thiết bị B vào máy tính **một lần**, chạy:
   ```bash
   adb tcpip 5555
   ```
   (dùng `adb` thật trên máy tính lúc này, không phải app). Sau đó rút dây — thiết bị
   B sẽ tiếp tục lắng nghe ADB qua Wi-Fi ở cổng `5555` cho tới khi reboot.
3. Trong app, nhập IP (hoặc domain nếu port-forward qua router/DDNS) và cổng `5555`
   ở màn hình Kết nối.
4. Lần đầu server chạy sẽ có popup "Bắt đầu ghi/truyền màn hình?" trên thiết bị B —
   người dùng thiết bị B cần bấm chấp nhận (không thể bypass nếu không root).

## Những phần cần tinh chỉnh thêm cho production

- **Xác thực/lưu trạng thái pairing**: hiện tại phải pair lại mỗi lần cài app mới;
  có thể lưu ADB key trong `FileSystem.AppDataDirectory` để tái sử dụng.
- **Xử lý xoay màn hình / đổi kích thước** khi thiết bị B xoay ngang/dọc — server
  scrcpy sẽ gửi lại frame với width/height mới, cần Reconfigure MediaCodec.
- **Audio**: scrcpy 2.x hỗ trợ stream cả audio (Opus/AAC) qua cùng cơ chế — có thể
  mở thêm 1 kết nối/track riêng nếu cần, không nằm trong scope code mẫu này.
- **Bảo mật**: pipeline này chỉ nên dùng trong mạng tin cậy (LAN nội bộ/VPN) —
  ADB không mã hóa traffic control ngoài bước xác thực RSA ban đầu.
- **Đối chiếu protocol**: định dạng byte trong `ScrcpyVideoClient` và
  `ScrcpyControlProtocol` bám theo tài liệu scrcpy dòng ~2.x — nếu đổi version
  server, kiểm tra lại `app/src/util/net.c` và `server/.../ControlMessage.java`
  trong mã nguồn scrcpy tương ứng để đảm bảo khớp byte-for-byte.
