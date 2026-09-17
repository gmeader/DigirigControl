# DigiRig Control Center — CAT v6

Changes in v6:

- Added Icom IC-7000 profile (default CI-V address `70`).
- Added Baofeng BF-F8HP Pro profile with `CATprotocol: "none"` (no CAT support assumed).
- Renamed JSON `protocol` field to `CATprotocol`.
- Added `civAddress` to Icom profiles; the CI-V frequency-read test frame is generated from that address.
- Added `pttMethod` to radio profiles for future profile-driven PTT behavior.
- DigiRig Lite PTT Test continues to use C-Media GPIO3 HID.
- DigiRig Mobile PTT Test now asserts RTS on the COM port selected in the CAT panel, with hardware flow control disabled.
- TX meters are now independent of the Input Monitor. They continuously monitor the actual DigiRig render/output endpoint with WASAPI loopback, so audio sent by the test tone or another Windows application (browser, modem software, etc.) appears on the TX meters.
- Starting/stopping Input Monitor only starts/stops RX capture and speaker monitoring; it no longer creates/replaces the TX loopback capture.

Build on Windows:

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release
```


## v7 - Windows default audio safety check

- Checks the Windows default playback and recording endpoints at startup and on Refresh.
- Shows a prominent warning if DigiRig Out is the default playback endpoint or DigiRig In is the default recording endpoint.
- Marks a DigiRig default endpoint as `[DEFAULT — NOT RECOMMENDED]` in Detected Audio Devices.
- Blocks Start Input Monitor while DigiRig Out is the Windows default playback device to prevent RX monitor audio from being routed back to the radio.
- Does not change Windows default devices automatically; use **Open Windows Audio Properties** to correct them.
