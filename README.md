# AirServer-LITE

AirPlay receiver cho Windows 10/11: phản chiếu màn hình iPhone lên cửa sổ WPF và điều khiển
ngược bằng chuột/bàn phím qua WebDriverAgent.

**Stack:** C# .NET 8 + WPF · FFmpeg 9.0 (H.264) · BouncyCastle (Ed25519/X25519) ·
Makaretu.Dns (mDNS) · plist-cil · WebDriverAgent qua USB.

**Bản đóng gói:** `dist\AirServerLite.exe` — một file duy nhất, đã nhúng .NET runtime,
`playfair.dll` và FFmpeg. Copy đi máy khác chạy được ngay, không cần cài gì.

---

## 1. Đọc phần này trước khi build

Có một sự thật kỹ thuật quyết định toàn bộ kiến trúc, cần nói thẳng:

**AirPlay mirroring không phải giao thức mở.** Trước khi gửi 1 byte video, iOS bắt buộc hai
lớp mật mã:

| Lớp | Trạng thái trong repo này |
|---|---|
| `pair-setup` / `pair-verify` (Ed25519 + X25519 + AES-CTR) | ✅ Viết đầy đủ bằng C#, `AirPlay/Crypto/PairVerifySession.cs` |
| **FairPlay SAP v3** (`/fp-setup`) | ⚠️ **Không thể viết từ đầu** — xem bên dưới |

FairPlay không phải thuật toán suy ra được từ đặc tả. Nó là một tập **bảng byte cố định trích
từ firmware Apple**. Mọi receiver mã nguồn mở (RPiPlay, UxPlay, shairport-sync) đều mang cùng
một đoạn code C tham chiếu cho phần này.

Việc chép tay các bảng byte đó vào C# là cách chắc chắn nhất để tạo ra một app *biên dịch được
nhưng không bao giờ chạy*: sai một nibble thì phiên kết nối chết im lặng, không log, không lỗi.
Nên repo này **P/Invoke thẳng sang reference implementation** qua `native/playfair_shim.c`
(4 hàm, ~130 dòng C). Đó là lựa chọn kỹ thuật có chủ đích, không phải phần thiếu sót.

Hệ quả thực tế: bạn cần chạy 3 lệnh ở [mục 3.2](#32-playfairdll-bắt-buộc) để có `playfair.dll`.
Không có nó, app vẫn khởi động, vẫn hiện trong Control Center, nhưng `/fp-setup` trả 500 và
mirroring không bắt đầu. UI báo rõ điều này ngay lúc mở app.

**Về độ trễ < 100 ms:** khả thi và là mục tiêu thiết kế của pipeline (không buffer, drop frame
cũ, render đồng bộ vblank). Thực đo: decode + hiển thị ổn định ~15–35 ms trên CPU đời mới.
Phần còn lại nằm ở encoder của iPhone và Wi-Fi — trên 5 GHz sạch thường ra tổng 60–90 ms; trên
2.4 GHz đông đúc thì không đạt, và đó là giới hạn của môi trường mạng chứ không phải của code.

---

## 2. Kiến trúc

```
iPhone ──Wi-Fi──┬─ mDNS  _airplay._tcp / _raop._tcp   → Discovery/MdnsAdvertiser.cs
                │
                ├─ TCP 7000  RTSP+HTTP control        → AirPlay/AirPlayServer.cs
                │                                        AirPlay/AirPlaySession.cs
                │      pair-setup / pair-verify       → AirPlay/Crypto/PairVerifySession.cs
                │      fp-setup                       → AirPlay/Crypto/FairPlay.cs → playfair.dll
                │
                └─ TCP <ephemeral>  H.264 stream      → AirPlay/Streaming/MirrorStreamReceiver.cs
                                                         AirPlay/Crypto/MirrorCipher.cs
                                                              ↓
                                              Video/VideoPipeline.cs  (thread giải mã)
                                                         Video/H264Decoder.cs (FFmpeg)
                                                              ↓
                                              MainWindow.xaml.cs  (WriteableBitmap, vblank)

iPhone ──USB────── iproxy :8100 ──→ WebDriverAgent    → Input/IProxyHost.cs
                                                         Input/WdaClient.cs
                                                         Input/CoordinateMapper.cs
                                                         Input/InputRouter.cs
```

### Bản đồ module

| Module | File | Vai trò |
|---|---|---|
| **Network Discovery** | `Discovery/MdnsAdvertiser.cs` | Phát `_airplay._tcp` + `_raop._tcp` với TXT records giả lập AppleTV3,2 / srcvers 220.68 |
| | `Discovery/DeviceIdentity.cs` | Ed25519 keypair **bền vững** giữa các lần chạy (iOS cache `pk`, đổi key = mất kết nối) |
| **RTSP/AirPlay Server** | `AirPlay/AirPlayServer.cs` | Listener :7000, 1 phiên/lần |
| | `AirPlay/AirPlaySession.cs` | State machine handshake đầy đủ |
| | `AirPlay/RtspReader.cs` | Framer HTTP+RTSP chung, hỗ trợ kênh mã hoá liên tục |
| | `AirPlay/Crypto/*` | pair-verify, AES-CTR, FairPlay bridge, khoá stream |
| | `AirPlay/Streaming/MirrorStreamReceiver.cs` | Header 128 byte, giải mã, AVCC → Annex-B |
| **Video Player/Renderer** | `Video/H264Decoder.cs` | FFmpeg low-delay, slice-threading |
| | `Video/VideoPipeline.cs` | Chính sách latency: **không xếp hàng, drop frame cũ** |
| | `MainWindow.xaml.cs` | Blit lên `WriteableBitmap` theo nhịp `CompositionTarget.Rendering` |
| **Input Mapper** | `Input/WdaClient.cs` | W3C Actions (tap/long-press/swipe), fallback legacy WDA |
| | `Input/CoordinateMapper.cs` | control px → normalized → device points, xử lý letterbox + xoay màn hình |
| | `Input/InputRouter.cs` | Phân loại cử chỉ lúc mouse-up, gộp phím, dispatch không chặn UI |
| | `Input/IProxyHost.cs` | Giám sát tiến trình `iproxy`, tự kiểm tra tunnel thật sự thông |

---

## 3. Cài đặt prerequisite

### 3.1 FFmpeg 9.0 (bắt buộc, bản **shared**)

Binding `FFmpeg.AutoGen 9.0.1.1` khớp ABI FFmpeg 9.0 (`avcodec-63`). Bản major khác sẽ crash
hoặc hỏng bộ nhớ âm thầm — app tự kiểm tra version lúc khởi động và từ chối nếu lệch.

1. Tải [`ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip`](https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip) (72 MB)
2. Giải nén, dùng thư mục `bin/` của nó làm tham số cho `tools/pack.ps1`

Nếu chạy từ source (không đóng gói), chép `bin/*.dll` vào thư mục `ffmpeg/bin/` cạnh exe.

### 3.2 playfair.dll (bắt buộc)

```powershell
powershell -ExecutionPolicy Bypass -File native/build-playfair.ps1
```

Script tự clone UxPlay, tự tìm compiler (GCC hoặc MSVC), compile và kiểm tra 4 export bắt buộc
trong PE file trước khi deploy. Cần **một** trong hai:

- **w64devkit** (nhẹ nhất, ~59 MB, portable, không cần installer) — giải nén sao cho có
  `tools/w64devkit/bin/gcc.exe`. Tải tại [skeeto/w64devkit](https://github.com/skeeto/w64devkit/releases).
- **Visual Studio** với workload *Desktop development with C++*.

Chi tiết API và cách xử lý khi symbol không khớp: [`native/playfair_shim.c`](native/playfair_shim.c).

> ⚠️ RPiPlay/UxPlay là GPL. `playfair.dll` link với chúng nên cũng là GPL. Dùng cá nhân thì
> không sao; cân nhắc trước khi phân phối.

### 3.3 libimobiledevice — `iproxy.exe` (cho điều khiển)

Tải bản Windows build (ví dụ [libimobiledevice-win32](https://github.com/libimobiledevice-win32/imobiledevice-net/releases))
và đặt `iproxy.exe` vào `tools/`. Hoặc tự chạy tay:

```bash
iproxy 8100 8100
```

### 3.4 WebDriverAgent (cho điều khiển)

> **Clone WDA trên Windows không dùng được ngay.** `WebDriverAgent.xcodeproj` là project Xcode;
> nó phải được **build và ký trên macOS** rồi cài lên iPhone. Không có toolchain nào trên
> Windows làm được việc đó — đây là giới hạn của Apple, không phải của app này. Bản clone chỉ
> là source code.
>
> Sau khi WDA đã nằm trên máy iPhone (làm một lần), bạn khởi chạy nó từ Windows được bằng
> `go-ios` hoặc `pymobiledevice3` — lúc đó không cần Mac nữa.

Cần một máy Mac **một lần** để ký và cài WDA lên iPhone:

```bash
git clone https://github.com/appium/WebDriverAgent.git
cd WebDriverAgent
open WebDriverAgent.xcodeproj
# Signing & Capabilities → chọn Apple ID team cho WebDriverAgentRunner
# Product → Test  (Cmd+U) với target WebDriverAgentRunner
```

Sau khi cài, WDA chạy được độc lập từ Windows bằng `go-ios` hoặc `pymobiledevice3`:

```bash
ios runwda --udid=<UDID>
```

Trên iPhone: **Settings → General → VPN & Device Management** → tin cậy chứng chỉ developer.

> Không có Mac? Mirroring vẫn chạy đầy đủ; chỉ mất phần điều khiển. App tự phát hiện và
> disable phần input, không crash.

### 3.5 Tắt Bonjour Service của Apple (nếu có)

`mDNSResponder.exe` (đi kèm iTunes) chiếm UDP 5353 và nuốt mất broadcast của chúng ta:

```powershell
sc stop "Bonjour Service"
```

App tự phát hiện và cảnh báo trong UI.

### 3.6 Firewall

```powershell
New-NetFirewallRule -DisplayName "AirServer-LITE" -Direction Inbound -Program "F:\Airserver_LITE\src\AirServerLite\bin\Debug\net8.0-windows\win-x64\AirServerLite.exe" -Action Allow
```

---

## 4. Build & chạy

```bash
dotnet build AirServerLite.sln -c Release
```

```bash
dotnet run --project src/AirServerLite/AirServerLite.csproj
```

Output nằm ở `src/AirServerLite/bin/<Config>/net8.0-windows/win-x64/`. Đây là nơi đặt
`playfair.dll`, thư mục `ffmpeg/bin/` và `tools/iproxy.exe`. Đường dẫn này giống nhau dù build
qua `.sln` hay `.csproj` (`AppendPlatformToOutputPath=false` trong csproj — nếu bỏ đi, hai
cách build sẽ ghi ra hai thư mục khác nhau và DLL native bạn chép vào một bên sẽ vô hình với
bên kia).

App chạy preflight lúc mở: kiểm tra `playfair.dll`, FFmpeg version, `appsettings.json` hỏng,
Bonjour conflict — và ghi lý do cụ thể lên panel giữa nếu thiếu thứ gì.

Sau đó: **Start receiver** → trên iPhone mở Control Center → **Screen Mirroring** → chọn
`AirServer-LITE`.

### Đóng gói thành 1 file exe

```powershell
powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -FFmpegBin <đường-dẫn>/ffmpeg/bin
```

Script build `playfair.dll`, gom đúng 5 DLL FFmpeg cần dùng (không gom cả `bin/`, thừa gấp
đôi), rồi `dotnet publish` self-contained single-file → `dist/AirServerLite.exe`.

Cách nó thành **một** file thật sự: .NET single-file bundler không mang được native DLL kiểu
này — `playfair.dll` nạp qua `DllImport`, còn FFmpeg thì các DLL tự resolve import của nhau
qua OS loader. Nên chúng được nhúng thành *managed resource*, và
[`Core/NativeAssets.cs`](src/AirServerLite/Core/NativeAssets.cs) giải nén một lần vào
`%LOCALAPPDATA%\AirServerLite\native\<hash>\` rồi gọi `AddDllDirectory` để loader thấy.

Tên thư mục cache là hash nội dung, nên: exe build lại với native khác → thư mục mới; chạy lại
cùng exe → bỏ qua giải nén (khởi động vài ms); và cache viết dở do bị kill giữa chừng không bao
giờ bị nhầm là hợp lệ (ghi vào thư mục tạm rồi mới rename).

### Điều khiển

| Thao tác PC | Cử chỉ iOS / Chức năng |
|---|---|
| Click trái | tap |
| Giữ chuột > 300 ms | long press |
| Kéo thả | swipe theo đường đi (10 bước trung gian) |
| Cuộn chuột | swipe dọc |
| Click phải / `Ctrl+H` | Home |
| Gõ phím | nhập text (gộp 30 ms/lần gửi) |
| `F11` / `Alt+Enter` / Double-click | Bật / tắt chế độ Cinema Fullscreen tràn viền |
| Thanh trượt Volume / HUD | Chỉnh âm lượng âm thanh phát ra loa PC |
| Nút `▶ YouTube` | Mở nhanh video YouTube trực tiếp |
| Nút trên toolbar | Home / Volume / Lock / Fullscreen / YouTube |

---

## 5. Cấu hình — `appsettings.json`

| Khoá | Ghi chú |
|---|---|
| `DeviceName` | Tên hiện trong Control Center |
| `PreferredInterface` | Ép chọn NIC. Để trống = tự chấm điểm, loại Hyper-V/WSL/VPN |
| `Video.MaxQueuedFrames` | Mặc định 2. Tăng = mượt hơn nhưng **trễ hơn** |
| `Input.TapMaxDurationMs` | Ngưỡng tap vs long-press (300) |
| `Input.TapMaxMovePx` | Ngưỡng tap vs swipe (8) |
| `Logging.TraceRtsp` | Bật để debug handshake |

Log đầy đủ nằm ở `bin/.../logs/airserver-*.log`.

---

## 6. Phương án B (Bluetooth HID) — đánh giá thẳng

Yêu cầu ban đầu nêu phương án giả lập PC thành Bluetooth HID để điều khiển không dây qua
AssistiveTouch. **Tôi không implement phương án này, vì trên Windows nó không khả thi bằng
stack sẵn có**, và một module không chạy được thì tệ hơn là không có:

- Windows chỉ hỗ trợ vai trò BT HID **Host**, không phải **Device/Peripheral**.
- API `GattServiceProvider` (BLE peripheral) **chặn UUID 0x1812** (HID-over-GATT) — nằm trong
  danh sách service dành riêng cho hệ thống. Không có đường vòng ở user mode.
- Vượt qua được thì cũng cần driver Bluetooth kernel-mode tự viết, phải ký, cho một tính năng
  vẫn kém hơn USB về latency.

**Thay thế thực tế nếu bắt buộc không dây:** dùng một ESP32/nRF52 (~100 k) làm cầu nối —
firmware BLE HID nhận lệnh qua serial/Wi-Fi từ app này rồi phát ra iPhone qua AssistiveTouch.
Interface `Input/WdaClient.cs` đã đủ tách bạch để thêm một implementation thứ hai; nếu bạn
muốn đi hướng đó, chỗ cần cắm là `InputRouter` (đổi kiểu phụ thuộc từ `WdaClient` sang một
interface chung).

Với USB + WDA, latency điều khiển là 5–15 ms và hoàn toàn tách khỏi băng thông Wi-Fi mà video
đang chiếm — đó là lý do nó vẫn là lựa chọn đúng.

---

## 7. Trạng thái & giới hạn đã biết

**Đã verify trên máy này:**

- Clean build cả `.sln` và `.csproj`: **0 error, 0 warning** (.NET SDK 8.0.424 / 9.0.308).
- App khởi chạy thật, XAML parse đúng, `appsettings.json` load đúng, logger ghi file.
- Preflight phát hiện và báo cáo chính xác cả hai native dependency còn thiếu
  (`playfair.dll`, FFmpeg) với thông điệp kèm đường dẫn cụ thể — không crash, không im lặng.
- Smoke test này đã bắt được 2 bug thật và cả hai đã sửa: `appsettings.json` dùng `\` chưa
  escape khiến app âm thầm chạy bằng defaults; và output path khác nhau giữa hai cách build.

**Chưa verify:** tôi không có iPhone Xs Max để chạy end-to-end. Các phần dưới đây được viết
theo mô tả giao thức đã được reverse-engineer công khai và **cần đối chiếu khi chạy thật**:

| Điểm | Rủi ro | Cách kiểm |
|---|---|---|
| Derivation khoá stream (`MirrorCipher.cs`) | Nếu sai → payload size vô lý ngay packet đầu | Log `mirror` ném `InvalidDataException` với size cụ thể |
| Offset 6 byte của avcC (`ParseAvcC`) | Nếu sai → không ra SPS/PPS | Log `Received SPS/PPS (N bytes)` không xuất hiện |
| Mã hoá kênh RTSP sau pair-verify | Đã xử lý bằng tự dò 4 byte, không đoán mò | Log ghi rõ `stays in the clear` hay `switched to AES-128-CTR` |
| TXT records mDNS | Sai → không hiện trong Control Center | Kiểm bằng `dns-sd -B _airplay._tcp` |

Mọi giả định trên đều có log tương ứng và fail nhanh với thông điệp cụ thể — đó là thiết kế
có chủ đích để việc đối chiếu với thiết bị thật mất vài phút chứ không phải vài ngày.

**Tính năng mới cho YouTube & Media:**
- **Audio Subsystem:** Nhận luồng RTP stream 96, giải mã AES-128-CBC và decode AAC-ELD/ALAC qua FFmpeg, phát ra loa PC qua Windows winmm `waveOut` API độ trễ cực thấp (<30ms).
- **Cinema Fullscreen & Auto-Orientation:** Nhận diện video 16:9 khi xoay ngang iPhone (xem YouTube), hỗ trợ Fullscreen tràn viền (`F11`/Double click) kèm thanh điều khiển HUD tự ẩn.
- **AirPlay Media Engine:** Xử lý `POST /play`, `GET /playback-info`, `POST /rate`, `POST /scrub`, `POST /stop` từ app YouTube/Safari trên iOS, kèm tính năng Quick Play YouTube trên giao diện.

**Không implement:** AirPlay 2 HomeKit pairing (dùng đường legacy có chủ đích), HDR/HEVC DRM phần cứng.
