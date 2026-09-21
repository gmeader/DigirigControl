# DigiRig Control Center

A Windows desktop utility for configuring, monitoring, and testing **DigiRig Lite** and **DigiRig Mobile** interfaces.

DigiRig Control Center brings common Windows audio checks, audio level monitoring, PTT testing, COM-port identification, and radio CAT testing into one application.

## Features

### DigiRig interface selection

Select the interface being used:

- DigiRig Lite
- DigiRig Mobile

The application adjusts available PTT and CAT functions according to the selected interface.

### Audio device detection

Displays detected DigiRig audio devices and provides controls for:

- DigiRig input level
- DigiRig output level
- RX/input level monitoring
- TX/output level monitoring
- Test tone generation

The TX meters monitor the actual DigiRig playback endpoint, so they can display audio being sent to DigiRig by other Windows applications as well as the built-in test tone.

For example, audio routed to DigiRig from a browser or digital-mode application should appear on the TX meters.

### Windows default audio device warnings

DigiRig normally should **not** be the Windows default playback or recording device.

The application checks the Windows default audio endpoints and warns if:

- DigiRig Output is the default playback device
- DigiRig Input is the default recording device

This is important because making DigiRig Output the Windows default could send normal computer audio, notifications, browser audio, etc. to the radio.

Applications that need DigiRig audio should explicitly select the appropriate DigiRig input/output devices.

### PTT testing

The PTT Test function supports different keying methods depending on the DigiRig interface.

- **DigiRig Lite:** GPIO3/HID PTT
- **DigiRig Mobile:** RTS PTT using the selected COM port

The PTT duration is configurable.

DigiRig Mobile RTS PTT has been tested with an **Icom IC-7000**.

### COM device detection

The application displays detected COM ports along with available USB/PnP identifying information to help determine which port belongs to the DigiRig interface.

Information may include:

- COM port
- Device description
- Manufacturer
- USB VID/PID
- PnP device ID

This is especially useful on systems with multiple USB serial devices.

### CAT testing

For radios that support CAT control, the application provides:

- COM port selection
- CAT speed
- Data bits
- Stop bits
- CAT test command
- CAT response display

Radio-specific CAT settings are loaded from the external `cat-profiles.json` file.

### Icom CI-V support

Icom radios use binary CI-V commands.

Icom profiles include a CI-V radio address in `cat-profiles.json`, and the application generates the appropriate binary CAT test command.

For example, the default CI-V address for an IC-7000 profile is `70`, producing a frequency query:

```text
FE FE 70 E0 03 FD
```

A successful IC-7000 response contains the radio-to-controller CI-V frame and the current operating frequency.

The IC-7000 + DigiRig Mobile combination has been tested successfully for both CI-V communication and RTS PTT.

### Radios without CAT

Radios may be included in `cat-profiles.json` even if they do not provide CAT control.

For example, the **Baofeng BF-F8HP Pro** profile uses:

```json
"CATprotocol": "none"
```

## Radio profiles

Radio configuration is stored externally in:

```text
cat-profiles.json
```

This allows radio definitions to be added or modified without rebuilding the application. Here are the CATprotocols:

* ascii  → testCommand is sent as ASCII characters
* civ    → Icom CI-V command handling
* hex    → testCommand contains space-separated hexadecimal bytes
* none

A profile can contain fields such as:

```json
{
  "brand": "Icom",
  "model": "IC-7000",
  "CATprotocol": "civ",
  "civAddress": "70",
  "baudRate": 19200,
  "dataBits": 8,
  "stopBits": 1,
  "pttMethod": "rts"
},
{
  "model": "FT-857D",
  "CATprotocol": "hex",
  "civAddress": "",
  "baud": 4800,
  "dataBits": 8,
   "stopBits": 1,
  "pttMethod": "rts",
  "testCommand": "00 00 00 00 03"
},
{
  "model": "TS-590S / TS-590SG",
  "CATprotocol": "ascii",
  "civAddress": "",
  "baud": 9600,
  "dataBits": 8,
   "stopBits": 1,
  "pttMethod": "rts",
  "testCommand": "ID;"
}
```

CAT parameters should always be checked against the radio manufacturer's documentation and the settings configured in the radio.

## Audio monitoring

The RX and TX meters represent different Windows audio paths.

**RX/Input**

```text
Radio -> DigiRig Input -> Windows
```

The RX meter becomes active when input monitoring is started.

**TX/Output**

```text
Windows application -> DigiRig Output -> Radio
```

The TX meters use Windows audio loopback monitoring of the DigiRig playback endpoint. They therefore respond whenever audio is actually being sent to that endpoint, regardless of which application generated it.

Starting the input monitor by itself should not cause TX activity.

## Requirements

- Windows 10 or Windows 11
- x64 PC
- DigiRig Lite or DigiRig Mobile
- .NET 8 SDK when building from source

A self-contained release build can be distributed without requiring the user to install the .NET runtime separately.

## Building from source

Clone the repository and open a terminal in the project directory.

Restore dependencies:

```powershell
dotnet restore
```

Build:

```powershell
dotnet build -c Release
```

Run:

```powershell
dotnet run -c Release
```

## Creating a self-contained Windows build

Run:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

The resulting application will normally be located under:

```text
bin\Release\net8.0-windows\win-x64\publish\
```

Distribute the **entire contents** of the publish directory, including `cat-profiles.json`.

## Important radio safety notes

Before transmitting:

- Verify that Windows is using the intended speakers/headphones as its default playback device.
- Do not use DigiRig Output as the normal Windows default playback device.
- Verify the selected DigiRig audio devices.
- Verify the correct COM port before using CAT or RTS PTT.
- Verify CAT parameters against the radio configuration.
- Start with conservative audio levels.
- Ensure the radio is connected to a suitable antenna or dummy load as appropriate.

PTT testing can place the connected radio into transmit.

## Tested hardware

Hardware confirmed during development includes:

- DigiRig Mobile with Icom IC-7000
  - RTS PTT
  - Icom CI-V CAT communication
  - CI-V frequency query/response
- DigiRig Lite with Baofeng BF-F8HP Pro
  - PTT via GPIO3/HID or Right channel VOX
  - audio routing and output metering

Other profiles may be based on documented radio protocols but have not necessarily been tested on physical hardware.

If you test another radio successfully, reports and profile corrections are welcome.

## Project files

Typical repository contents:

```text
DigiRigControlCenter/
├── DigiRigControlCenter.csproj
├── App.xaml
├── App.xaml.cs
├── MainWindow.xaml
├── MainWindow.xaml.cs
├── cat-profiles.json
├── README.md
├── LICENSE
└── .gitignore
```

Build output directories such as `bin/` and `obj/` should not be committed to the repository.

## Contributing radio profiles

Contributions for additional radios are welcome.

When adding a profile, please include accurate:

- Brand and model
- CAT protocol
- Baud rate
- Data bits
- Stop bits
- PTT method
- CI-V address for Icom radios
- CAT test command information where applicable

Please indicate whether the profile has been tested with physical hardware.

## License

Add the license selected for this project in the repository's `LICENSE` file.
